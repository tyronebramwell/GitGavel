using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Models;

public enum SubagentProgressStatus
{
    Started,
    Completed,
    Failed,
    Skipped
}

public record SubagentProgress(
    string AgentName,
    SubagentProgressStatus Status,
    SubagentResult? Result = null,
    long ElapsedMs = 0,
    string? SkipReason = null);
