using GitGavel.Configuration;
using GitGavel.System1.Models;

namespace GitGavel.GitHub;

public class MockGitHubService : IGitHubService
{
    public List<ExistingIssue> ExistingIssues { get; set; } = [];
    public List<(TriageDecision Decision, CreatedIssueResult Result)> CreatedIssues { get; } = [];
    public List<(CreatedIssueResult Issue, string Column)> ProjectAssignments { get; } = [];

    private int _nextIssueNumber = 100;

    public Task<List<ExistingIssue>> GetExistingIssuesAsync(string owner, string repo, CancellationToken ct = default)
    {
        return Task.FromResult(ExistingIssues);
    }

    public Task<CreatedIssueResult> CreateOrUpdateIssueAsync(
        string owner,
        string repo,
        TriageDecision decision,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var num = ++_nextIssueNumber;
        var res = new CreatedIssueResult(
            IssueNumber: num,
            HtmlUrl: $"https://github.com/{owner}/{repo}/issues/{num}",
            NodeId: $"I_kwDO{num}",
            Title: decision.Finding.Title,
            WasUpdated: false
        );

        CreatedIssues.Add((decision, res));
        return Task.FromResult(res);
    }

    public Task AssignToProjectV2Async(
        string owner,
        string repo,
        CreatedIssueResult issueResult,
        TriageDecision decision,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var col = decision.TargetColumn == "Backlog"
            ? config.Github.ProjectsV2.Columns.Backlog
            : config.Github.ProjectsV2.Columns.HumanReview;

        ProjectAssignments.Add((issueResult, col));
        return Task.CompletedTask;
    }
}
