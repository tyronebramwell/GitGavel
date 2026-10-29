using System.Security;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitGavel.Configuration;

public static partial class ConfigLoader
{
    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "gemini",
        "openai",
        "anthropic",
        "mock"
    };

    [GeneratedRegex(@"(?m)^(?!\s*#)\s*(?:apiKey|api_key)\s*:\s*['""]?\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DirectApiKeyRegex();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static GitGavelConfig LoadFromString(string yamlContent, bool allowExternalPaths = false)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            return new GitGavelConfig();
        }

        if (DirectApiKeyRegex().IsMatch(yamlContent))
        {
            throw new InvalidOperationException("Direct API keys in configuration files are forbidden for security reasons. Please use environment variables via 'apiKeyEnv' instead.");
        }

        var config = Deserializer.Deserialize<GitGavelConfig>(yamlContent) ?? new GitGavelConfig();
        Validate(config, allowExternalPaths);
        return config;
    }

    public static async Task<GitGavelConfig> LoadFromFileAsync(string path, bool allowExternalPaths = false, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"GitGavel configuration file was not found: '{path}'", path);
        }

        var content = await File.ReadAllTextAsync(path, ct);
        return LoadFromString(content, allowExternalPaths);
    }

    public static string Serialize(GitGavelConfig config)
    {
        return Serializer.Serialize(config);
    }

    public static void Validate(GitGavelConfig config, bool allowExternalPaths = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        ValidateTriage(config.Triage);
        ValidateScope(config.Scope);
        ValidateSubagents(config.Subagents, allowExternalPaths);
        ValidateReporting(config.Reporting, allowExternalPaths);
    }

    public static void ValidateTriage(TriageConfig triage)
    {
        ArgumentNullException.ThrowIfNull(triage);

        if (triage.MaxIssuesPerRun <= 0)
        {
            throw new InvalidOperationException("triage.maxIssuesPerRun must be greater than 0.");
        }

        if (triage.SubagentGating != null && triage.SubagentGating.MinConfidence is < 0 or > 1.0)
        {
            throw new InvalidOperationException("triage.subagentGating.minConfidence must be between 0.0 and 1.0.");
        }

        var thresholds = triage.Jev.Thresholds;
        if (thresholds.AutoBacklogConfidence is < 0 or > 1.0)
        {
            throw new InvalidOperationException("triage.jev.thresholds.autoBacklogConfidence must be between 0.0 and 1.0.");
        }

        if (thresholds.HumanReviewConfidence is < 0 or > 1.0)
        {
            throw new InvalidOperationException("triage.jev.thresholds.humanReviewConfidence must be between 0.0 and 1.0.");
        }

        if (thresholds.HumanReviewConfidence > thresholds.AutoBacklogConfidence)
        {
            throw new InvalidOperationException("triage.jev.thresholds.humanReviewConfidence cannot be greater than autoBacklogConfidence.");
        }

        if (thresholds.AutoCloseIssueConfidence is < 0 or > 1.0)
        {
            throw new InvalidOperationException("triage.jev.thresholds.autoCloseIssueConfidence must be between 0.0 and 1.0.");
        }
    }

    public static void ValidateScope(ScopeConfig scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.MaxFileSizeBytes <= 0)
        {
            throw new InvalidOperationException("scope.maxFileSizeBytes must be greater than 0.");
        }

        ValidateScopePatterns("scope.specs.include", scope.Specs?.Include);
        ValidateScopePatterns("scope.source.include", scope.Source?.Include);
        ValidateScopePatterns("scope.source.exclude", scope.Source?.Exclude);
        ValidateScopePatterns("scope.tests.include", scope.Tests?.Include);
        ValidateScopePatterns("scope.tests.exclude", scope.Tests?.Exclude);
    }

    public static void ValidateSubagents(SubagentsConfig subagents, bool allowExternalPaths = false)
    {
        ArgumentNullException.ThrowIfNull(subagents);

        if (subagents.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("subagents.timeoutSeconds must be greater than 0.");
        }

        if (subagents.IssueResolution != null && subagents.IssueResolution.CommitLookbackCount <= 0)
        {
            throw new InvalidOperationException("subagents.issueResolution.commitLookbackCount must be greater than 0.");
        }

        ValidateSubagentProvider("subagents.specDrift", subagents.SpecDrift);
        ValidateSubagentProvider("subagents.security", subagents.Security);
        ValidateSubagentProvider("subagents.reliability", subagents.Reliability);
        ValidateSubagentProvider("subagents.refactoring", subagents.Refactoring);
        ValidateSubagentProvider("subagents.tests", subagents.Tests);
        ValidateSubagentProvider("subagents.issueResolution", subagents.IssueResolution);

        if (subagents.Security?.SarifReports != null)
        {
            foreach (var sarifPath in subagents.Security.SarifReports)
            {
                if (string.IsNullOrWhiteSpace(sarifPath)) continue;
                if (!allowExternalPaths && !PathSecurity.IsSafeRelativePath(sarifPath))
                {
                    throw new SecurityException($"Unsafe SARIF report path detected: '{sarifPath}'. SARIF report paths must be relative and cannot contain directory traversal ('..'). Use --allow-external-paths to override.");
                }
            }
        }
    }

    public static void ValidateReporting(ReportingConfig reporting, bool allowExternalPaths = false)
    {
        ArgumentNullException.ThrowIfNull(reporting);

        if (string.IsNullOrWhiteSpace(reporting.OutputDir))
        {
            throw new InvalidOperationException("reporting.outputDir cannot be empty.");
        }

        if (!allowExternalPaths && !PathSecurity.IsSafeRelativePath(reporting.OutputDir))
        {
            throw new SecurityException($"Unsafe reporting output directory detected: '{reporting.OutputDir}'. Reporting output directory must be relative and cannot contain directory traversal ('..'). Use --allow-external-paths to override.");
        }
    }

    private static void ValidateSubagentProvider(string sectionName, SubagentSettings? settings)
    {
        if (settings == null) return;

        if (!string.IsNullOrWhiteSpace(settings.Provider) && !SupportedProviders.Contains(settings.Provider))
        {
            throw new InvalidOperationException($"Unsupported LLM provider '{settings.Provider}' in {sectionName}. Supported providers are: {string.Join(", ", SupportedProviders)}.");
        }

        if (settings.TimeoutSeconds is <= 0)
        {
            throw new InvalidOperationException($"{sectionName}.timeoutSeconds must be greater than 0.");
        }
    }

    private static void ValidateScopePatterns(string sectionName, IEnumerable<string>? patterns)
    {
        if (patterns == null) return;
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (!PathSecurity.IsSafeRelativePath(pattern))
            {
                throw new SecurityException($"Malicious scope glob pattern detected in {sectionName}: '{pattern}'. Scope patterns must be relative to the repository root and cannot contain directory traversal ('..').");
            }
        }
    }

    public static string? ResolveEnv(string envVarName, IEnvironmentProvider? environment = null)
    {
        if (string.IsNullOrWhiteSpace(envVarName)) return null;
        return (environment ?? SystemEnvironmentProvider.Instance).GetEnvironmentVariable(envVarName);
    }
}
