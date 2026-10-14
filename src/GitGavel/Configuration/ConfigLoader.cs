using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitGavel.Configuration;

public static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static GitGavelConfig LoadFromString(string yamlContent)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            return new GitGavelConfig();
        }

        var config = Deserializer.Deserialize<GitGavelConfig>(yamlContent) ?? new GitGavelConfig();
        Validate(config);
        return config;
    }

    public static async Task<GitGavelConfig> LoadFromFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"GitGavel configuration file was not found: '{path}'", path);
        }

        var content = await File.ReadAllTextAsync(path, ct);
        return LoadFromString(content);
    }

    public static string Serialize(GitGavelConfig config)
    {
        return Serializer.Serialize(config);
    }

    public static void Validate(GitGavelConfig config)
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
    }

    public static string? ResolveEnv(string envVarName)
    {
        if (string.IsNullOrWhiteSpace(envVarName)) return null;
        return Environment.GetEnvironmentVariable(envVarName);
    }
}
