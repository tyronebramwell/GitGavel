using GitGavel.Configuration;
using GitGavel.System1.Models;

namespace GitGavel.GitHub;

public record CreatedIssueResult(
    int IssueNumber,
    string HtmlUrl,
    string NodeId,
    string Title,
    bool WasUpdated);

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
}
