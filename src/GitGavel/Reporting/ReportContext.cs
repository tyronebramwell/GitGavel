using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.GitHub;
using GitGavel.System1.Models;
using GitGavel.System2;

namespace GitGavel.Reporting;

public record ReportContext
{
    public required GitGavelConfig Config { get; init; }
    public required RepoContext RepoContext { get; init; }
    public required OrchestrationResult Orchestration { get; init; }
    public required IReadOnlyList<TriageDecision> TriageDecisions { get; init; }
    public IReadOnlyList<IssueResolutionDecision> IssueResolutionDecisions { get; init; } = [];
    public IReadOnlyList<ClosedIssueResult> ClosedIssues { get; init; } = [];
    public IReadOnlyList<PrCommentTriageDecision> PrCommentDecisions { get; init; } = [];
    public PostedPrReviewResult? PostedPrReview { get; init; }
    public int? ActivePullRequestNumber { get; init; }
    public DateTime StartTimeUtc { get; init; } = DateTime.UtcNow;
    public DateTime EndTimeUtc { get; init; } = DateTime.UtcNow;
    public TimeSpan Duration => EndTimeUtc - StartTimeUtc;
    public string GitCommitSha { get; init; } = "unknown";
    public string GitBranch { get; init; } = "main";
}
