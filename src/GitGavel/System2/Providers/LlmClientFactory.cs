using GitGavel.Configuration;
using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Providers;

public class LlmClientFactory : ILlmClientFactory
{
    private readonly bool _forceMock;
    private readonly ILlmClient? _fallbackClient;

    public LlmClientFactory(bool forceMock = false, ILlmClient? fallbackClient = null)
    {
        _forceMock = forceMock;
        _fallbackClient = fallbackClient;
    }

    public ILlmClient CreateClient(SubagentSettings settings, GitGavelConfig config)
    {
        if (_forceMock)
        {
            return _fallbackClient ?? new MockLlmClient();
        }

        var provider = settings.Provider?.ToLowerInvariant() ?? "gemini";

        return provider switch
        {
            "mock" => new MockLlmClient(),
            "openai" => new OpenAiLlmClient(KeyResolver.Resolve(config.Subagents.ApiKey, config.Subagents.ApiKeyEnv, "OPENAI_API_KEY", "AI_API_KEY")),
            "anthropic" => new AnthropicLlmClient(KeyResolver.Resolve(config.Subagents.ApiKey, config.Subagents.ApiKeyEnv, "ANTHROPIC_API_KEY", "AI_API_KEY")),
            _ => _fallbackClient ?? new GeminiLlmClient(KeyResolver.Resolve(config.Subagents.ApiKey, config.Subagents.ApiKeyEnv, "GEMINI_API_KEY", "AI_API_KEY"))
        };
    }
}
