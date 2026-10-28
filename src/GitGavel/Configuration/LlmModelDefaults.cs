namespace GitGavel.Configuration;

public static class LlmModelDefaults
{
    public const string DefaultGeminiProModel = "gemini-2.5-pro";
    public const string DefaultGeminiFlashModel = "gemini-2.5-flash";
    public const string DefaultGeminiReliabilityModel = "gemini-3.1-pro-preview";
    public const string DefaultOpenAiModel = "gpt-4o";
    public const string DefaultAnthropicModel = "claude-3-5-sonnet-20241022";

    public static string ResolveSubagentModel(string? configuredModel, string agentDefaultModel)
    {
        return string.IsNullOrWhiteSpace(configuredModel) ? agentDefaultModel : configuredModel.Trim();
    }

    public static string ResolveGeminiModel(string? requestedModel)
    {
        var rawModel = string.IsNullOrWhiteSpace(requestedModel) ? DefaultGeminiProModel : requestedModel.Trim();
        return rawModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? rawModel["models/".Length..]
            : rawModel;
    }

    public static string ResolveOpenAiModel(string? requestedModel)
    {
        return string.IsNullOrWhiteSpace(requestedModel) || requestedModel.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
            ? DefaultOpenAiModel
            : requestedModel.Trim();
    }

    public static string ResolveAnthropicModel(string? requestedModel)
    {
        return string.IsNullOrWhiteSpace(requestedModel) || requestedModel.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
            ? DefaultAnthropicModel
            : requestedModel.Trim();
    }
}
