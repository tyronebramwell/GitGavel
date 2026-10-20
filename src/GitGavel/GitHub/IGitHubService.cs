using GitGavel.Configuration;
using GitGavel.Reporting;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.GitHub;

public record CreatedIssueResult(
    int IssueNumber,
    string HtmlUrl,
    string NodeId,
    string Title,
    bool WasUpdated);

public record ClosedIssueResult(
    int IssueNumber,
    string Title,
    string HtmlUrl,
    string NodeId,
    string HowItWasFixed,
    List<string> ResolvingCommits,
    double ResolutionConfidence,
    DateTime ClosedAtUtc);

public record PostedPrReviewResult(
    int PrNumber,
    long ReviewId,
    string HtmlUrl,
    int InlineCommentsPosted,
    bool SummaryPosted,
    string Verdict);

public interface IGitHubService
{
    Task<List<ExistingIssue>> GetExistingIssuesAsync(string owner, string repo, CancellationToken ct = default);

    Task<CreatedIssueResult> CreateOrUpdateIssueAsync(
        string owner,
        string repo,
        TriageDecision decision,
        GitGavelConfig config,
        CancellationToken ct = default);

    Task AssignToProjectV2Async(
        string owner,
        string repo,
        CreatedIssueResult issueResult,
        TriageDecision decision,
        GitGavelConfig config,
        CancellationToken ct = default);

    Task<ClosedIssueResult> CloseResolvedIssueAsync(
        string owner,
        string repo,
        IssueResolutionDecision decision,
        GitGavelConfig config,
        CancellationToken ct = default);

    Task<PullRequestContext?> GetPullRequestContextAsync(
        string owner,
        string repo,
        int prNumber,
        CancellationToken ct = default);

    Task<PostedPrReviewResult> PostPullRequestReviewAsync(
        string owner,
        string repo,
        PullRequestContext prContext,
        IReadOnlyList<PrCommentTriageDecision> decisions,
        GitGavelConfig config,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> PostPullRequestDomainReportsAsync(
        string owner,
        string repo,
        int prNumber,
        ReportContext reportContext,
        CancellationToken ct = default);
}
