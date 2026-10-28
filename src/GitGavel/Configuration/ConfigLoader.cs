using System.Security;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitGavel.Configuration;

public static partial class ConfigLoader
{
    [GeneratedRegex(@"(?m)^\s*(?:apiKey|api_key)\s*:\s*['""]?\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
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

        if (config.Triage.MaxIssuesPerRun <= 0)
        {
            throw new InvalidOperationException("triage.maxIssuesPerRun must be greater than 0.");
        }

        var thresholds = config.Triage.Jev.Thresholds;
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

        // Validate Scope Glob Patterns for directory traversal or absolute paths
        ValidateScopePatterns("scope.specs.include", config.Scope.Specs.Include);
        ValidateScopePatterns("scope.source.include", config.Scope.Source.Include);
        ValidateScopePatterns("scope.source.exclude", config.Scope.Source.Exclude);
        ValidateScopePatterns("scope.tests.include", config.Scope.Tests.Include);
        ValidateScopePatterns("scope.tests.exclude", config.Scope.Tests.Exclude);

        // Validate SARIF report paths
        if (config.Subagents.Security.SarifReports != null)
        {
            foreach (var sarifPath in config.Subagents.Security.SarifReports)
            {
                if (string.IsNullOrWhiteSpace(sarifPath)) continue;
                if (!allowExternalPaths && !PathSecurity.IsSafeRelativePath(sarifPath))
                {
                    throw new SecurityException($"Unsafe SARIF report path detected: '{sarifPath}'. SARIF report paths must be relative and cannot contain directory traversal ('..'). Use --allow-external-paths to override.");
                }
            }
        }

        // Validate reporting output directory
        if (!string.IsNullOrWhiteSpace(config.Reporting.OutputDir))
        {
            if (!allowExternalPaths && !PathSecurity.IsSafeRelativePath(config.Reporting.OutputDir))
            {
                throw new SecurityException($"Unsafe reporting output directory detected: '{config.Reporting.OutputDir}'. Reporting output directory must be relative and cannot contain directory traversal ('..'). Use --allow-external-paths to override.");
            }
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

    public static string? ResolveEnv(string envVarName)
    {
        if (string.IsNullOrWhiteSpace(envVarName)) return null;
        return Environment.GetEnvironmentVariable(envVarName);
    }
}
