namespace GitGavel.System2.Abstractions;

public record LlmRequest
{
    public required string Prompt { get; init; }
    public string? SystemPrompt { get; init; }
    public string? AgentName { get; init; }
    public string Model { get; init; } = "gemini-2.5-pro";
    public double Temperature { get; init; } = 0.2;
    public bool ExpectJson { get; init; } = true;
}

public record LlmResponse
{
    public required string Content { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public long DurationMs { get; init; }
    public required string ModelUsed { get; init; }
}

public interface ILlmClient
{
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default);
}
