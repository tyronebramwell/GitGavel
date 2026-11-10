using GitGavel.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitGavel.System2.Agents;

public class AgentListContainer
{
    public int Version { get; set; } = 1;
    public List<AgentDefinition> Agents { get; set; } = [];
}

public static class AgentDefinitionRegistry
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static Dictionary<string, AgentDefinition> LoadAllDefinitions(
        GitGavelConfig config,
        string? repoRootDir = null,
        string? explicitAgentsPath = null,
        bool allowExternalPaths = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        var definitions = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, _, def) in StandardAgentYamlCatalog.GetAllStandardDefinitions())
        {
            definitions[def.Id] = def;
        }

        // Apply inline overrides from GitGavelConfig.Subagents
        ApplySubagentSettingsOverride(definitions["specDrift"], config.Subagents.SpecDrift);
        ApplySubagentSettingsOverride(definitions["security"], config.Subagents.Security);
        ApplySubagentSettingsOverride(definitions["reliability"], config.Subagents.Reliability);
        ApplySubagentSettingsOverride(definitions["refactoring"], config.Subagents.Refactoring);
        ApplySubagentSettingsOverride(definitions["tests"], config.Subagents.Tests);
        if (config.Subagents.Tests?.Tiers != null && config.Subagents.Tests.Tiers.Count > 0)
        {
            definitions["tests"].Tiers = [.. config.Subagents.Tests.Tiers];
        }
        ApplySubagentSettingsOverride(definitions["issueResolution"], config.Subagents.IssueResolution);

        // Load external YAML definitions if present
        var targetPath = !string.IsNullOrWhiteSpace(explicitAgentsPath)
            ? explicitAgentsPath
            : config.Subagents.DefinitionsPath;

        if (!string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(repoRootDir))
        {
            try
            {
                var fullRoot = Path.GetFullPath(repoRootDir);
                var resolved = PathSecurity.ResolveAndVerifyWithinRoot(targetPath, fullRoot, allowExternalPaths);
                if (!string.IsNullOrWhiteSpace(explicitAgentsPath) &&
                    !File.Exists(resolved) &&
                    !Directory.Exists(resolved))
                {
                    throw new FileNotFoundException($"Explicitly requested agent definitions path not found: '{resolved}'", resolved);
                }

                LoadFromPathInto(resolved, fullRoot, allowExternalPaths, definitions);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(explicitAgentsPath))
                {
                    throw new InvalidOperationException($"Failed to load explicit agent definitions from '{explicitAgentsPath}': {ex.Message}", ex);
                }
                // Ignore missing optional definitions path unless explicitly validated
            }
        }

        // Apply inline custom agents from GitGavelConfig.Subagents.Custom
        foreach (var custom in config.Subagents.Custom ?? [])
        {
            if (string.IsNullOrWhiteSpace(custom.Id) && string.IsNullOrWhiteSpace(custom.Name))
            {
                continue;
            }

            var id = !string.IsNullOrWhiteSpace(custom.Id) ? custom.Id : custom.Name;
            custom.Id = id;
            if (string.IsNullOrWhiteSpace(custom.Name))
            {
                custom.Name = id;
            }

            ResolveExternalPromptFileIfAny(custom, repoRootDir, allowExternalPaths);
            definitions[id] = custom;
        }

        return definitions;
    }

    public static IReadOnlyList<string> EjectStandardAgents(
        string targetDirectory,
        string repoRootDir,
        bool force = false,
        bool allowExternalPaths = false)
    {
        var fullRoot = Path.GetFullPath(repoRootDir);
        PathSecurity.ValidateRootDirectory(fullRoot);
        var resolvedTargetDir = PathSecurity.ResolveAndVerifyWithinRoot(targetDirectory, fullRoot, allowExternalPaths);
        Directory.CreateDirectory(resolvedTargetDir);

        var writtenFiles = new List<string>();
        foreach (var (fileName, yaml, _) in StandardAgentYamlCatalog.GetAllStandardDefinitions())
        {
            var filePath = PathSecurity.ResolveAndVerifyWithinRoot(
                Path.Combine(resolvedTargetDir, fileName),
                fullRoot,
                allowExternalPaths);

            if (File.Exists(filePath) && !force)
            {
                continue;
            }

            File.WriteAllText(filePath, yaml.Trim() + Environment.NewLine);
            writtenFiles.Add(filePath);
        }

        return writtenFiles;
    }

    private static void LoadFromPathInto(
        string resolvedPath,
        string repoRootDir,
        bool allowExternalPaths,
        Dictionary<string, AgentDefinition> definitions)
    {
        var files = new List<string>();
        if (File.Exists(resolvedPath))
        {
            files.Add(resolvedPath);
        }
        else if (Directory.Exists(resolvedPath))
        {
            files.AddRange(Directory.GetFiles(resolvedPath, "*.yml", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(resolvedPath, "*.yaml", SearchOption.AllDirectories));
        }

        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var content = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(content)) continue;

            List<AgentDefinition> parsedList = [];
            if (content.Contains("agents:", StringComparison.OrdinalIgnoreCase))
            {
                var container = Deserializer.Deserialize<AgentListContainer>(content);
                if (container?.Agents != null && container.Agents.Count > 0)
                {
                    parsedList.AddRange(container.Agents);
                }
            }

            if (parsedList.Count == 0)
            {
                var single = StandardAgentYamlCatalog.ParseFromYaml(content);
                if (!string.IsNullOrWhiteSpace(single.Id) || !string.IsNullOrWhiteSpace(single.Name))
                {
                    parsedList.Add(single);
                }
            }

            foreach (var loaded in parsedList)
            {
                var key = NormalizeStandardOrCustomKey(loaded.Id, loaded.Name);
                if (string.IsNullOrWhiteSpace(key)) continue;

                loaded.Id = key;
                if (string.IsNullOrWhiteSpace(loaded.Name))
                {
                    loaded.Name = key;
                }

                ResolveExternalPromptFileIfAny(loaded, repoRootDir, allowExternalPaths);

                if (definitions.TryGetValue(key, out var existing))
                {
                    MergeAgentDefinition(existing, loaded);
                }
                else
                {
                    definitions[key] = loaded;
                }
            }
        }
    }

    public static string NormalizeStandardOrCustomKey(string? id, string? name)
    {
        var candidate = !string.IsNullOrWhiteSpace(id) ? id.Trim() : (name?.Trim() ?? string.Empty);
        return candidate.ToLowerInvariant() switch
        {
            "specdrift" or "specdriftagent" or "spec-drift" => "specDrift",
            "security" or "securityauditor" or "securityauditoragent" => "security",
            "reliability" or "reliabilityagent" => "reliability",
            "refactoring" or "refactoringagent" => "refactoring",
            "tests" or "testsentinel" or "testsentinelagent" or "test-sentinel" => "tests",
            "issueresolution" or "issueresolutionauditor" or "issueresolutionagent" or "issue-resolution" => "issueResolution",
            _ => candidate
        };
    }

    private static void ApplySubagentSettingsOverride(AgentDefinition target, SubagentSettings? settings)
    {
        if (settings == null)
        {
            target.Enabled = false;
            return;
        }

        target.Enabled = settings.Enabled;
        if (!string.IsNullOrWhiteSpace(settings.Provider))
        {
            target.Provider = settings.Provider;
        }
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            target.Model = settings.Model;
        }
        if (settings.TimeoutSeconds.HasValue)
        {
            target.TimeoutSeconds = settings.TimeoutSeconds;
        }
        if (settings.Skills != null && settings.Skills.Count > 0)
        {
            target.Skills = [.. settings.Skills];
        }
        if (settings.Artifacts != null && settings.Artifacts.Count > 0)
        {
            target.Artifacts = [.. settings.Artifacts];
        }
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            target.SystemPrompt = settings.SystemPrompt;
        }
        if (!string.IsNullOrWhiteSpace(settings.AdditionalInstructions))
        {
            target.AdditionalInstructions = settings.AdditionalInstructions;
        }
        if (!string.IsNullOrWhiteSpace(settings.GatingQuestion))
        {
            target.GatingQuestion = settings.GatingQuestion;
            target.Gate ??= new AgentGateConfig();
            target.Gate.Jev ??= new JevAgentGateConfig();
            target.Gate.Jev.Question = settings.GatingQuestion;
        }
    }

    private static void MergeAgentDefinition(AgentDefinition target, AgentDefinition overrideDef)
    {
        target.Enabled = overrideDef.Enabled;
        if (!string.IsNullOrWhiteSpace(overrideDef.Name)) target.Name = overrideDef.Name;
        if (!string.IsNullOrWhiteSpace(overrideDef.Category)) target.Category = overrideDef.Category;
        if (!string.IsNullOrWhiteSpace(overrideDef.Provider)) target.Provider = overrideDef.Provider;
        if (!string.IsNullOrWhiteSpace(overrideDef.Model)) target.Model = overrideDef.Model;
        if (overrideDef.TimeoutSeconds.HasValue) target.TimeoutSeconds = overrideDef.TimeoutSeconds;
        if (!string.IsNullOrWhiteSpace(overrideDef.SystemPrompt)) target.SystemPrompt = overrideDef.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(overrideDef.UserPromptIntro)) target.UserPromptIntro = overrideDef.UserPromptIntro;
        if (!string.IsNullOrWhiteSpace(overrideDef.AdditionalPromptBody)) target.AdditionalPromptBody = overrideDef.AdditionalPromptBody;
        if (!string.IsNullOrWhiteSpace(overrideDef.AntiScopeCreepRule)) target.AntiScopeCreepRule = overrideDef.AntiScopeCreepRule;
        if (!string.IsNullOrWhiteSpace(overrideDef.JsonSchemaInstructions)) target.JsonSchemaInstructions = overrideDef.JsonSchemaInstructions;
        if (!string.IsNullOrWhiteSpace(overrideDef.UserPromptTemplate)) target.UserPromptTemplate = overrideDef.UserPromptTemplate;
        if (!string.IsNullOrWhiteSpace(overrideDef.AdditionalInstructions)) target.AdditionalInstructions = overrideDef.AdditionalInstructions;
        if (overrideDef.Skills != null && overrideDef.Skills.Count > 0) target.Skills = [.. overrideDef.Skills];
        if (overrideDef.Artifacts != null && overrideDef.Artifacts.Count > 0) target.Artifacts = [.. overrideDef.Artifacts];
        if (overrideDef.Scope != null) target.Scope = overrideDef.Scope;
        if (overrideDef.Gate != null) target.Gate = overrideDef.Gate;
        if (overrideDef.Tiers != null && overrideDef.Tiers.Count > 0) target.Tiers = [.. overrideDef.Tiers];
    }

    private static void ResolveExternalPromptFileIfAny(AgentDefinition def, string? repoRootDir, bool allowExternalPaths)
    {
        if (string.IsNullOrWhiteSpace(def.PromptFile) || string.IsNullOrWhiteSpace(repoRootDir))
        {
            return;
        }

        try
        {
            var resolved = PathSecurity.ResolveAndVerifyWithinRoot(def.PromptFile, Path.GetFullPath(repoRootDir), allowExternalPaths);
            if (File.Exists(resolved))
            {
                var content = File.ReadAllText(resolved).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    def.SystemPrompt = content;
                }
            }
        }
        catch
        {
            // Ignore unreadable prompt file
        }
    }
}
