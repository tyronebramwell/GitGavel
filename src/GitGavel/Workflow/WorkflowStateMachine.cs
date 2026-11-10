using System.Diagnostics;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System1.Abstractions;
using GitGavel.System1.Engines;
using GitGavel.System1.Models;
using GitGavel.System2;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Agents;
using GitGavel.System2.Models;

namespace GitGavel.Workflow;

public class WorkflowStateMachine
{
    private readonly WorkflowConfig _workflow;
    private readonly IReadOnlyDictionary<string, AgentDefinition> _agentDefinitions;
    private readonly ILlmClientFactory _clientFactory;
    private readonly ITriageEngine? _triageEngine;
    private readonly Func<string, AgentDefinition, ISubagent>? _customAgentFactory;

    public WorkflowStateMachine(
        WorkflowConfig workflow,
        IReadOnlyDictionary<string, AgentDefinition> agentDefinitions,
        ILlmClientFactory clientFactory,
        ITriageEngine? triageEngine = null,
        Func<string, AgentDefinition, ISubagent>? customAgentFactory = null)
    {
        _workflow = workflow;
        _agentDefinitions = agentDefinitions;
        _clientFactory = clientFactory;
        _triageEngine = triageEngine;
        _customAgentFactory = customAgentFactory;
    }

    public async Task<(OrchestrationResult Result, WorkflowSnapshot FinalSnapshot)> ExecuteAsync(
        RepoContext context,
        GitGavelConfig config,
        Action<SubagentProgress>? onProgress = null,
        Action<WorkflowSnapshot>? onSnapshotUpdated = null,
        CancellationToken ct = default)
    {
        WorkflowLoader.Validate(_workflow);
        var (levels, weights) = WorkflowLoader.ComputeTopologyMetrics(_workflow);

        var stateLock = new object();
        var states = new Dictionary<string, WorkflowNodeRuntimeState>(StringComparer.OrdinalIgnoreCase);
        var nodeConfigs = _workflow.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var nodePassableFindings = new Dictionary<string, List<FindingCandidate>>(StringComparer.OrdinalIgnoreCase);
        var gatingDecisionsByAgent = new Dictionary<string, SubagentGatingDecision>(StringComparer.OrdinalIgnoreCase);
        var allGatingDecisions = new List<SubagentGatingDecision>();
        var subagentResults = new List<SubagentResult>();
        var allFindings = new List<FindingCandidate>();
        var activeSlots = 0;
        var maxSlots = Math.Max(1, _workflow.Concurrency.MaxParallelSteps);

        // Initialize node states
        foreach (var node in _workflow.Nodes)
        {
            var isSystem1 = string.Equals(node.System, "system1", StringComparison.OrdinalIgnoreCase);
            AgentDefinition? agentDef = null;
            if (!isSystem1)
            {
                var agentKey = AgentDefinitionRegistry.NormalizeStandardOrCustomKey(node.Agent, node.Id);
                _agentDefinitions.TryGetValue(agentKey, out agentDef);
            }

            var isEnabled = isSystem1
                ? node.Enabled
                : (node.Enabled && (agentDef?.Enabled ?? true));

            var displayName = !string.IsNullOrWhiteSpace(node.Name)
                ? node.Name
                : (agentDef?.Name ?? node.Id);

            var providerOrEngine = isSystem1
                ? (node.Engine ?? config.Triage.SubagentGating.Engine ?? "jev")
                : (agentDef?.Provider ?? "gemini");

            var model = isSystem1 ? null : agentDef?.Model;
            var skillsCount = agentDef?.Skills?.Count ?? 0;
            var artifactsCount = agentDef?.Artifacts?.Count ?? 0;

            states[node.Id] = new WorkflowNodeRuntimeState
            {
                Id = node.Id,
                Name = displayName,
                System = isSystem1 ? "system1" : "system2",
                ProviderOrEngine = providerOrEngine,
                Model = model,
                TopologicalLevel = levels.GetValueOrDefault(node.Id, 0),
                CriticalPathWeight = weights.GetValueOrDefault(node.Id, 0),
                DependsOn = node.DependsOn ?? [],
                InputsFrom = node.InputsFrom ?? [],
                PassResults = node.PassResults,
                SkillsCount = skillsCount,
                ArtifactsCount = artifactsCount,
                Status = isEnabled ? WorkflowNodeStatus.Waiting : WorkflowNodeStatus.NotUsed,
                StatusReason = isEnabled ? null : "Disabled in configuration"
            };
        }

        WorkflowSnapshot CaptureSnapshot()
        {
            lock (stateLock)
            {
                return new WorkflowSnapshot(
                    _workflow.Name,
                    activeSlots,
                    maxSlots,
                    _workflow.Nodes.Select(n => states[n.Id]).ToList());
            }
        }

        void NotifySnapshot()
        {
            try
            {
                onSnapshotUpdated?.Invoke(CaptureSnapshot());
            }
            catch
            {
                // UI callback errors must never disrupt workflow execution
            }
        }

        void NotifyProgress(SubagentProgress progress)
        {
            try
            {
                onProgress?.Invoke(progress);
            }
            catch
            {
                // Progress callback errors must never disrupt workflow execution
            }
        }

        using var globalSemaphore = new SemaphoreSlim(maxSlots, maxSlots);
        var providerSemaphores = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _workflow.Concurrency.MaxPerProvider)
        {
            var cap = Math.Max(1, kvp.Value);
            providerSemaphores[kvp.Key] = new SemaphoreSlim(cap, cap);
        }

        var runningTasks = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        var totalSw = Stopwatch.StartNew();

        void EvaluateWaitingTransitions()
        {
            lock (stateLock)
            {
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var node in _workflow.Nodes)
                    {
                        var current = states[node.Id];
                        if (current.Status != WorkflowNodeStatus.Waiting)
                        {
                            continue;
                        }

                        // Check if pre-gate explicitly skipped this agent
                        var normalizedKey = AgentDefinitionRegistry.NormalizeStandardOrCustomKey(node.Agent, node.Id);
                        if ((gatingDecisionsByAgent.TryGetValue(current.Name, out var gateDec) ||
                             gatingDecisionsByAgent.TryGetValue(node.Id, out gateDec) ||
                             gatingDecisionsByAgent.TryGetValue(normalizedKey, out gateDec)) &&
                            !gateDec.ShouldRun)
                        {
                            states[node.Id] = current with
                            {
                                Status = WorkflowNodeStatus.Skipped,
                                StatusReason = gateDec.Reason,
                                WaitingOnNodeIds = []
                            };
                            NotifyProgress(new SubagentProgress(
                                current.Name,
                                SubagentProgressStatus.Skipped,
                                SkipReason: gateDec.Reason));
                            changed = true;
                            continue;
                        }

                        var allDeps = (node.DependsOn ?? [])
                            .Concat(node.InputsFrom ?? [])
                            .Where(d => !string.IsNullOrWhiteSpace(d))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        var blockers = new List<string>();
                        var upstreamGateFailedOrSkipped = false;
                        string? upstreamSkipReason = null;

                        foreach (var depId in allDeps)
                        {
                            if (!states.TryGetValue(depId, out var depState))
                            {
                                continue;
                            }

                            if (depState.Status is WorkflowNodeStatus.Waiting or WorkflowNodeStatus.Queued or WorkflowNodeStatus.Running)
                            {
                                blockers.Add(depId);
                            }
                            else if (depState.Status is WorkflowNodeStatus.Skipped or WorkflowNodeStatus.Failed)
                            {
                                var depConfig = nodeConfigs[depId];
                                // If a non-root gate or required inputsFrom node was skipped/failed, cascade skip
                                if (depConfig.Type != "subagent-gating" &&
                                    ((node.InputsFrom?.Contains(depId, StringComparer.OrdinalIgnoreCase) ?? false) ||
                                     string.Equals(depConfig.System, "system1", StringComparison.OrdinalIgnoreCase)))
                                {
                                    upstreamGateFailedOrSkipped = true;
                                    upstreamSkipReason = $"Skipped because upstream dependency '{depId}' was {depState.Status.ToString().ToLowerInvariant()}.";
                                }
                            }
                        }

                        if (blockers.Count > 0)
                        {
                            if (!current.WaitingOnNodeIds.SequenceEqual(blockers, StringComparer.OrdinalIgnoreCase))
                            {
                                states[node.Id] = current with { WaitingOnNodeIds = blockers };
                            }
                            continue;
                        }

                        if (upstreamGateFailedOrSkipped)
                        {
                            states[node.Id] = current with
                            {
                                Status = WorkflowNodeStatus.Skipped,
                                StatusReason = upstreamSkipReason,
                                WaitingOnNodeIds = []
                            };
                            if (string.Equals(current.System, "system2", StringComparison.OrdinalIgnoreCase))
                            {
                                NotifyProgress(new SubagentProgress(
                                    current.Name,
                                    SubagentProgressStatus.Skipped,
                                    SkipReason: upstreamSkipReason));
                            }
                            changed = true;
                            continue;
                        }

                        // All dependencies satisfied -> transition Waiting -> Queued!
                        states[node.Id] = current with
                        {
                            Status = WorkflowNodeStatus.Queued,
                            StatusReason = "Ready (queued for execution)",
                            WaitingOnNodeIds = []
                        };
                        changed = true;
                    }
                }
            }
        }

        EvaluateWaitingTransitions();
        NotifySnapshot();

        while (true)
        {
            EvaluateWaitingTransitions();

            List<WorkflowNodeConfig> readyToDispatch;
            lock (stateLock)
            {
                var useCriticalPath = !string.Equals(_workflow.Concurrency.SchedulingPolicy, "fifo", StringComparison.OrdinalIgnoreCase);
                var queuedStates = _workflow.Nodes
                    .Select(n => states[n.Id])
                    .Where(s => s.Status == WorkflowNodeStatus.Queued && !runningTasks.ContainsKey(s.Id));

                if (useCriticalPath)
                {
                    queuedStates = queuedStates
                        .OrderByDescending(s => s.CriticalPathWeight)
                        .ThenBy(s => s.TopologicalLevel);
                }

                readyToDispatch = queuedStates.Select(s => nodeConfigs[s.Id]).ToList();
            }

            foreach (var nodeConfig in readyToDispatch)
            {
                var nodeId = nodeConfig.Id;
                if (string.Equals(nodeConfig.System, "system1", StringComparison.OrdinalIgnoreCase))
                {
                    runningTasks[nodeId] = ExecuteSystem1NodeAsync(nodeConfig);
                }
                else
                {
                    runningTasks[nodeId] = ExecuteSystem2NodeAsync(nodeConfig);
                }
            }

            NotifySnapshot();

            if (runningTasks.Count == 0)
            {
                var anyWaitingOrQueued = false;
                lock (stateLock)
                {
                    anyWaitingOrQueued = states.Values.Any(s => s.Status is WorkflowNodeStatus.Waiting or WorkflowNodeStatus.Queued);
                }

                if (!anyWaitingOrQueued)
                {
                    break;
                }
            }

            if (runningTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(runningTasks.Values);
                var finishedKeys = runningTasks.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList();
                foreach (var k in finishedKeys)
                {
                    await runningTasks[k];
                    runningTasks.Remove(k);
                }
            }
        }

        totalSw.Stop();
        NotifySnapshot();

        SubagentGatingResult? gatingResult = null;
        if (allGatingDecisions.Count > 0)
        {
            gatingResult = TriageEngineHelper.BuildGatingResult(allGatingDecisions);
        }

        var orchestrationResult = new OrchestrationResult
        {
            AllFindings = allFindings,
            SubagentResults = subagentResults,
            TotalDurationMs = totalSw.ElapsedMilliseconds,
            GatingResult = gatingResult,
            WorkflowSnapshot = CaptureSnapshot()
        };

        return (orchestrationResult, orchestrationResult.WorkflowSnapshot!);

        async Task ExecuteSystem1NodeAsync(WorkflowNodeConfig node)
        {
            var sw = Stopwatch.StartNew();
            lock (stateLock)
            {
                states[node.Id] = states[node.Id] with
                {
                    Status = WorkflowNodeStatus.Running,
                    StatusReason = "Evaluating System 1 gate..."
                };
            }
            NotifySnapshot();

            var upstreamFindings = CollectInputsFromFindings(node);

            if (string.Equals(node.Type, "subagent-gating", StringComparison.OrdinalIgnoreCase))
            {
                // Collect candidate System 2 agents that depend on this gate
                var downstreamAgents = _workflow.Nodes
                    .Where(n => string.Equals(n.System, "system2", StringComparison.OrdinalIgnoreCase) &&
                                ((n.DependsOn?.Contains(node.Id, StringComparer.OrdinalIgnoreCase) ?? false) ||
                                 (n.InputsFrom?.Contains(node.Id, StringComparer.OrdinalIgnoreCase) ?? false)))
                    .Where(n => states[n.Id].Status != WorkflowNodeStatus.NotUsed)
                    .ToList();

                var decisions = new List<SubagentGatingDecision>();

                // 1. Evaluate custom node-level deterministic rules if present
                var preDecided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (node.Rules != null)
                {
                    foreach (var da in downstreamAgents)
                    {
                        var agentName = states[da.Id].Name;
                        if (node.Rules.SkipIf.TryGetValue(da.Id, out var skipExpr) ||
                            node.Rules.SkipIf.TryGetValue(agentName, out skipExpr))
                        {
                            if (NonJevGateEvaluator.EvaluateExpression(skipExpr, context, upstreamFindings))
                            {
                                var d = new SubagentGatingDecision(agentName, false, 0.0, $"Skipped by workflow rule: `{skipExpr}`.", true);
                                decisions.Add(d);
                                preDecided.Add(agentName);
                                continue;
                            }
                        }

                        if (node.Rules.ForceActivate.TryGetValue(da.Id, out var forceExpr) ||
                            node.Rules.ForceActivate.TryGetValue(agentName, out forceExpr))
                        {
                            if (NonJevGateEvaluator.EvaluateExpression(forceExpr, context, upstreamFindings))
                            {
                                var d = new SubagentGatingDecision(agentName, true, 1.0, $"Forced active by workflow rule: `{forceExpr}`.", true);
                                decisions.Add(d);
                                preDecided.Add(agentName);
                            }
                        }
                    }
                }

                // 2. Evaluate agent-level deterministic rules if engine includes deterministic
                var engineMode = (node.Engine ?? config.Triage.SubagentGating.Engine ?? "deterministic-then-jev").ToLowerInvariant();
                if (engineMode.Contains("deterministic", StringComparison.Ordinal))
                {
                    foreach (var da in downstreamAgents)
                    {
                        var agentName = states[da.Id].Name;
                        if (preDecided.Contains(agentName)) continue;

                        var key = AgentDefinitionRegistry.NormalizeStandardOrCustomKey(da.Agent, da.Id);
                        if (_agentDefinitions.TryGetValue(key, out var def) && def.Gate?.Deterministic != null)
                        {
                            var detDecision = NonJevGateEvaluator.EvaluateDeterministicRuleSet(agentName, def.Gate.Deterministic, context, upstreamFindings);
                            if (detDecision != null)
                            {
                                decisions.Add(detDecision);
                                preDecided.Add(agentName);
                            }
                        }
                    }
                }

                // 3. Evaluate remaining agents via Jev or Fast-LLM or default pass
                var remainingNames = downstreamAgents
                    .Select(da => states[da.Id].Name)
                    .Where(name => !preDecided.Contains(name))
                    .ToList();

                if (remainingNames.Count > 0)
                {
                    if (engineMode == "deterministic")
                    {
                        foreach (var name in remainingNames)
                        {
                            decisions.Add(new SubagentGatingDecision(name, true, 1.0, "Activated by deterministic gate.", true));
                        }
                    }
                    else if (engineMode == "llm")
                    {
                        var gateClient = _clientFactory.CreateClient(new SubagentSettings { Provider = "gemini", Model = LlmModelDefaults.DefaultGeminiFlashModel }, config);
                        foreach (var da in downstreamAgents.Where(d => remainingNames.Contains(states[d.Id].Name)))
                        {
                            var name = states[da.Id].Name;
                            var key = AgentDefinitionRegistry.NormalizeStandardOrCustomKey(da.Agent, da.Id);
                            var q = _agentDefinitions.TryGetValue(key, out var def) && !string.IsNullOrWhiteSpace(def.Gate?.Jev?.Question)
                                ? def.Gate.Jev.Question!
                                : $"Should {name} run on these repository changes?";
                            var llmDec = await NonJevGateEvaluator.EvaluateFastLlmGateAsync(
                                name, q, context, gateClient, LlmModelDefaults.DefaultGeminiFlashModel, config.Triage.SubagentGating.MinConfidence, ct);
                            decisions.Add(llmDec);
                        }
                    }
                    else if (_triageEngine != null)
                    {
                        try
                        {
                            var jevRes = await _triageEngine.EvaluateSubagentGatingAsync(context, remainingNames, config, ct);
                            if (jevRes?.Decisions != null)
                            {
                                decisions.AddRange(jevRes.Decisions);
                            }
                        }
                        catch
                        {
                            // Proceed if triage engine throws
                        }
                    }
                }

                sw.Stop();
                lock (stateLock)
                {
                    foreach (var d in decisions)
                    {
                        gatingDecisionsByAgent[d.AgentName] = d;
                    }
                    allGatingDecisions.AddRange(decisions);
                    var activeCount = decisions.Count(d => d.ShouldRun);
                    var skippedCount = decisions.Count(d => !d.ShouldRun);
                    states[node.Id] = states[node.Id] with
                    {
                        Status = WorkflowNodeStatus.Completed,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        StatusReason = $"{activeCount} activated, {skippedCount} skipped"
                    };
                }
                return;
            }

            // Inter-stage gate or condition node
            var conditionPassed = true;
            if (!string.IsNullOrWhiteSpace(node.Condition))
            {
                conditionPassed = NonJevGateEvaluator.EvaluateExpression(node.Condition, context, upstreamFindings);
            }

            sw.Stop();
            lock (stateLock)
            {
                if (conditionPassed)
                {
                    if (node.PassResults && upstreamFindings.Count > 0)
                    {
                        nodePassableFindings[node.Id] = upstreamFindings;
                    }

                    states[node.Id] = states[node.Id] with
                    {
                        Status = WorkflowNodeStatus.Completed,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        FindingsCount = upstreamFindings.Count,
                        StatusReason = !string.IsNullOrWhiteSpace(node.Condition)
                            ? $"Condition passed (`{node.Condition}`)"
                            : "Completed"
                    };
                }
                else
                {
                    states[node.Id] = states[node.Id] with
                    {
                        Status = WorkflowNodeStatus.Skipped,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        StatusReason = $"Condition evaluated to false: `{node.Condition}`"
                    };
                }
            }
        }

        async Task ExecuteSystem2NodeAsync(WorkflowNodeConfig node)
        {
            var agentKey = AgentDefinitionRegistry.NormalizeStandardOrCustomKey(node.Agent, node.Id);
            _agentDefinitions.TryGetValue(agentKey, out var agentDef);
            agentDef ??= new AgentDefinition
            {
                Id = node.Id,
                Name = node.Name ?? node.Id,
                Provider = "gemini",
                Model = LlmModelDefaults.DefaultGeminiProModel
            };

            var providerKey = string.IsNullOrWhiteSpace(agentDef.Provider) ? "gemini" : agentDef.Provider;
            providerSemaphores.TryGetValue(providerKey, out var providerSem);

            lock (stateLock)
            {
                if (globalSemaphore.CurrentCount == 0 || (providerSem != null && providerSem.CurrentCount == 0))
                {
                    states[node.Id] = states[node.Id] with
                    {
                        Status = WorkflowNodeStatus.Queued,
                        StatusReason = $"Queued (awaiting {providerKey} concurrency slot)"
                    };
                }
            }
            NotifySnapshot();

            await globalSemaphore.WaitAsync(ct);
            var acquiredProvider = false;
            try
            {
                if (providerSem != null)
                {
                    await providerSem.WaitAsync(ct);
                    acquiredProvider = true;
                }

                var agentInstance = CreateSubagentInstance(agentKey, agentDef, config);
                var agentDisplayName = agentInstance.Name;

                lock (stateLock)
                {
                    activeSlots++;
                    states[node.Id] = states[node.Id] with
                    {
                        Name = agentDisplayName,
                        Status = WorkflowNodeStatus.Running,
                        StatusReason = "Executing LLM subagent..."
                    };
                }

                NotifyProgress(new SubagentProgress(agentDisplayName, SubagentProgressStatus.Started));
                NotifySnapshot();

                var upstreamFindings = CollectInputsFromFindings(node);
                var scopedContext = upstreamFindings.Count > 0
                    ? (context with { UpstreamFindings = upstreamFindings })
                    : (context with { UpstreamFindings = [] });

                var agentSw = Stopwatch.StartNew();
                SubagentResult result;
                SubagentProgressStatus progressStatus;

                try
                {
                    result = await agentInstance.ExecuteAsync(scopedContext, ct);
                    agentSw.Stop();
                    progressStatus = result.IsSuccess ? SubagentProgressStatus.Completed : SubagentProgressStatus.Failed;
                }
                catch (Exception ex)
                {
                    agentSw.Stop();
                    result = new SubagentResult
                    {
                        AgentName = agentDisplayName,
                        IsSuccess = false,
                        ErrorMessage = ex.Message,
                        DurationMs = agentSw.ElapsedMilliseconds
                    };
                    progressStatus = SubagentProgressStatus.Failed;
                }

                lock (stateLock)
                {
                    activeSlots = Math.Max(0, activeSlots - 1);
                    subagentResults.Add(result);
                    var findingsList = result.IsSuccess && result.Findings != null ? result.Findings : [];
                    allFindings.AddRange(findingsList);

                    if (node.PassResults && findingsList.Count > 0)
                    {
                        nodePassableFindings[node.Id] = findingsList;
                    }

                    states[node.Id] = states[node.Id] with
                    {
                        Status = result.IsSuccess ? WorkflowNodeStatus.Completed : WorkflowNodeStatus.Failed,
                        ElapsedMs = agentSw.ElapsedMilliseconds,
                        FindingsCount = findingsList.Count,
                        TokensUsed = result.TotalTokens,
                        StatusReason = result.IsSuccess ? null : result.ErrorMessage
                    };
                }

                NotifyProgress(new SubagentProgress(agentDisplayName, progressStatus, result, agentSw.ElapsedMilliseconds));
                NotifySnapshot();
            }
            finally
            {
                if (acquiredProvider && providerSem != null)
                {
                    providerSem.Release();
                }
                globalSemaphore.Release();
            }
        }

        List<FindingCandidate> CollectInputsFromFindings(WorkflowNodeConfig node)
        {
            if (node.InputsFrom == null || node.InputsFrom.Count == 0)
            {
                return [];
            }

            lock (stateLock)
            {
                var collected = new List<FindingCandidate>();
                foreach (var inputId in node.InputsFrom)
                {
                    if (nodePassableFindings.TryGetValue(inputId, out var list))
                    {
                        collected.AddRange(list);
                    }
                }
                return collected;
            }
        }
    }

    private ISubagent CreateSubagentInstance(string normalizedKey, AgentDefinition def, GitGavelConfig config)
    {
        if (_customAgentFactory != null)
        {
            return _customAgentFactory(normalizedKey, def);
        }

        var client = _clientFactory.CreateClient(def, config);
        return new DeclarativeYamlAgent(client, def);
    }
}
