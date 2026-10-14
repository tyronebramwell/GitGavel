using GitGavel.Discovery;
using GitGavel.System2.Models;

namespace GitGavel.System2.Abstractions;

public record SubagentResult
{
    public required string AgentName { get; init; }
    public List<FindingCandidate> Findings { get; init; } = [];
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public long DurationMs { get; init; }
    public bool IsSuccess { get; init; } = true;
    public string? ErrorMessage { get; init; }
}

public interface ISubagent
{
    string Name { get; }
    Task<SubagentResult> ExecuteAsync(RepoContext context, CancellationToken ct = default);
}
