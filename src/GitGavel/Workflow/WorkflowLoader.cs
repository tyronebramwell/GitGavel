using GitGavel.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitGavel.Workflow;

public static class WorkflowLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static WorkflowConfig LoadFromString(string yamlContent)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            throw new InvalidOperationException("Workflow YAML content cannot be empty.");
        }

        var workflow = Deserializer.Deserialize<WorkflowConfig>(yamlContent) ?? new WorkflowConfig();
        Validate(workflow);
        return workflow;
    }

    public static async Task<WorkflowConfig> LoadFromFileAsync(
        string workflowPath,
        string repoRootDir,
        bool allowExternalPaths = false,
        CancellationToken ct = default)
    {
        var fullRoot = Path.GetFullPath(repoRootDir);
        PathSecurity.ValidateRootDirectory(fullRoot);
        var resolvedPath = PathSecurity.ResolveAndVerifyWithinRoot(workflowPath, fullRoot, allowExternalPaths);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Workflow configuration file not found: '{resolvedPath}'", resolvedPath);
        }

        var yaml = await File.ReadAllTextAsync(resolvedPath, ct);
        return LoadFromString(yaml);
    }

    public static WorkflowConfig ResolveOrBuildDefault(
        GitGavelConfig config,
        IReadOnlyDictionary<string, AgentDefinition> agentDefinitions,
        string? repoRootDir = null,
        string? explicitWorkflowPath = null,
        bool allowExternalPaths = false)
    {
        var pathToLoad = !string.IsNullOrWhiteSpace(explicitWorkflowPath)
            ? explicitWorkflowPath
            : config.WorkflowFile;

        if (!string.IsNullOrWhiteSpace(pathToLoad) && !string.IsNullOrWhiteSpace(repoRootDir))
        {
            var fullRoot = Path.GetFullPath(repoRootDir);
            var resolvedPath = PathSecurity.ResolveAndVerifyWithinRoot(pathToLoad, fullRoot, allowExternalPaths);
            if (File.Exists(resolvedPath))
            {
                var yaml = File.ReadAllText(resolvedPath);
                return LoadFromString(yaml);
            }

            if (!string.IsNullOrWhiteSpace(explicitWorkflowPath))
            {
                throw new FileNotFoundException($"Explicitly requested workflow file not found: '{resolvedPath}'", resolvedPath);
            }
        }

        return CreateDefaultWorkflow(config, agentDefinitions);
    }

    public static WorkflowConfig CreateDefaultWorkflow(
        GitGavelConfig config,
        IReadOnlyDictionary<string, AgentDefinition> agentDefinitions)
    {
        var nodes = new List<WorkflowNodeConfig>
        {
            new()
            {
                Id = "pre-gate",
                Name = "Pre-Execution Subagent Gate",
                System = "system1",
                Engine = config.Triage.SubagentGating.Engine,
                Type = "subagent-gating",
                Enabled = true,
                PassResults = true
            }
        };

        var discoveryIds = new List<string> { "specDrift", "security", "reliability", "refactoring", "tests" };
        foreach (var kvp in agentDefinitions)
        {
            if (!discoveryIds.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase) &&
                !string.Equals(kvp.Key, "issueResolution", StringComparison.OrdinalIgnoreCase))
            {
                discoveryIds.Add(kvp.Key);
            }
        }

        foreach (var agentId in discoveryIds)
        {
            if (!agentDefinitions.TryGetValue(agentId, out var def))
            {
                continue;
            }

            nodes.Add(new WorkflowNodeConfig
            {
                Id = agentId,
                Name = !string.IsNullOrWhiteSpace(def.Name) ? def.Name : agentId,
                Agent = agentId,
                System = "system2",
                Enabled = def.Enabled,
                DependsOn = ["pre-gate"],
                InputsFrom = [],
                PassResults = true
            });
        }

        return new WorkflowConfig
        {
            Version = 1,
            Name = "Default GitGavel DAG Workflow",
            Concurrency = new WorkflowConcurrencyConfig
            {
                MaxParallelSteps = 5,
                SchedulingPolicy = "critical-path"
            },
            Nodes = nodes
        };
    }

    public static void Validate(WorkflowConfig workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        if (workflow.Concurrency.MaxParallelSteps <= 0)
        {
            throw new InvalidOperationException("workflow.concurrency.maxParallelSteps must be greater than 0.");
        }

        foreach (var kvp in workflow.Concurrency.MaxPerProvider)
        {
            if (kvp.Value <= 0)
            {
                throw new InvalidOperationException($"workflow.concurrency.maxPerProvider['{kvp.Key}'] must be greater than 0.");
            }
        }

        if (workflow.Nodes == null || workflow.Nodes.Count == 0)
        {
            throw new InvalidOperationException("workflow.nodes must contain at least one node.");
        }

        var nodeMap = new Dictionary<string, WorkflowNodeConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in workflow.Nodes)
        {
            var id = !string.IsNullOrWhiteSpace(node.Id) ? node.Id : node.Agent;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("Every workflow node must specify a non-empty 'id' or 'agent'.");
            }

            node.Id = id;
            if (!nodeMap.TryAdd(id, node))
            {
                throw new InvalidOperationException($"Duplicate workflow node id detected: '{id}'.");
            }
        }

        // Verify all dependency references exist & build adjacency list for Kahn's topological sort
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in nodeMap.Keys)
        {
            inDegree[id] = 0;
            dependents[id] = [];
        }

        foreach (var node in workflow.Nodes)
        {
            var allDeps = (node.DependsOn ?? [])
                .Concat(node.InputsFrom ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dep in allDeps)
            {
                if (!nodeMap.ContainsKey(dep))
                {
                    throw new InvalidOperationException($"Workflow node '{node.Id}' references unknown upstream dependency '{dep}'.");
                }

                if (string.Equals(dep, node.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Workflow node '{node.Id}' cannot depend on itself.");
                }

                inDegree[node.Id]++;
                dependents[dep].Add(node.Id);
            }
        }

        // Kahn's algorithm for cycle detection
        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visitedCount = 0;

        while (queue.Count > 0)
        {
            var curr = queue.Dequeue();
            visitedCount++;
            foreach (var next in dependents[curr])
            {
                inDegree[next]--;
                if (inDegree[next] == 0)
                {
                    queue.Enqueue(next);
                }
            }
        }

        if (visitedCount != nodeMap.Count)
        {
            var cycleNodes = string.Join(", ", inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key));
            throw new InvalidOperationException($"Circular dependency cycle detected in workflow graph involving node(s): {cycleNodes}.");
        }
    }

    public static (Dictionary<string, int> Levels, Dictionary<string, int> CriticalPathWeights) ComputeTopologyMetrics(WorkflowConfig workflow)
    {
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nodeMap = workflow.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        int ComputeLevel(string id)
        {
            if (levels.TryGetValue(id, out var cached)) return cached;
            var node = nodeMap[id];
            var deps = (node.DependsOn ?? []).Concat(node.InputsFrom ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var level = deps.Count == 0 ? 0 : deps.Max(ComputeLevel) + 1;
            levels[id] = level;
            return level;
        }

        HashSet<string> GetTransitiveDependents(string id, Dictionary<string, HashSet<string>> memo)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in workflow.Nodes)
            {
                var deps = (n.DependsOn ?? []).Concat(n.InputsFrom ?? []);
                if (deps.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    set.Add(n.Id);
                    foreach (var trans in GetTransitiveDependents(n.Id, memo))
                    {
                        set.Add(trans);
                    }
                }
            }
            memo[id] = set;
            return set;
        }

        var depMemo = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in workflow.Nodes)
        {
            ComputeLevel(node.Id);
            weights[node.Id] = GetTransitiveDependents(node.Id, depMemo).Count;
        }

        return (levels, weights);
    }
}
