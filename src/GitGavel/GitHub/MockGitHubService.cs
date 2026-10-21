using GitGavel.Configuration;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.GitHub;

public class MockGitHubService : IGitHubService
{
    public List<ExistingIssue> ExistingIssues { get; set; } = [];
    public List<(TriageDecision Decision, CreatedIssueResult Result)> CreatedIssues { get; } = [];
    public List<(CreatedIssueResult Issue, string Column)> ProjectAssignments { get; } = [];
    public List<(IssueResolutionDecision Decision, ClosedIssueResult Result, string CommentBody)> ClosedIssues { get; } = [];
    public PullRequestContext? MockPullRequest { get; set; }
    public List<(PostedPrReviewResult Result, string SummaryBody, List<string> InlineBodies)> PostedPrReviews { get; } = [];
    public List<(int PrNumber, string FilePath, int DiffPosition, string Body)> PostedPrInlineComments { get; } = [];
    public List<(int PrNumber, string CommentBody)> PostedPrComments { get; } = [];

    private int _nextIssueNumber = 100;
    private long _nextReviewId = 500;

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

    public Task<ClosedIssueResult> CloseResolvedIssueAsync(
        string owner,
        string repo,
        IssueResolutionDecision decision,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var c = decision.Candidate;
        var commentBody = IssueFormatter.FormatIssueResolutionComment(decision);
        var closedResult = new ClosedIssueResult(
            IssueNumber: c.IssueNumber,
            Title: c.IssueTitle,
            HtmlUrl: $"https://github.com/{owner}/{repo}/issues/{c.IssueNumber}",
            NodeId: $"I_kwDO{c.IssueNumber}",
            HowItWasFixed: c.HowItWasFixed,
            ResolvingCommits: c.ResolvingCommitSummaries.Count > 0 ? c.ResolvingCommitSummaries : c.ResolvingCommitShas,
            ResolutionConfidence: decision.ResolutionProbability,
            ClosedAtUtc: DateTime.UtcNow
        );

        ClosedIssues.Add((decision, closedResult, commentBody));
        ProjectAssignments.Add((new CreatedIssueResult(c.IssueNumber, closedResult.HtmlUrl, closedResult.NodeId, c.IssueTitle, true), config.Github.ProjectsV2.Columns.Done));
        return Task.FromResult(closedResult);
    }

    public Task<PullRequestContext?> GetPullRequestContextAsync(
        string owner,
        string repo,
        int prNumber,
        CancellationToken ct = default)
    {
        if (MockPullRequest != null)
        {
            return Task.FromResult<PullRequestContext?>(MockPullRequest);
        }

        var simulatedPr = new PullRequestContext
        {
            PrNumber = prNumber,
            Title = $"Simulated Pull Request #{prNumber}",
            Body = "Implements recent audit fixes and test isolation improvements.",
            BaseRef = "main",
            HeadRef = $"feature/pr-{prNumber}",
            HeadSha = "cfad09afc6f436fb3af957ff2d94eba2d3e1ea36",
            ChangedFiles =
            [
                new PullRequestChangedFile(
                    FilePath: "src/GitGavel/Commands/AuditCommand.cs",
                    Status: "modified",
                    Additions: 32,
                    Deletions: 8,
                    Patch: "@@ -57,8 +57,32 @@ public override async Task<int> ExecuteAsync(CommandContext context, AuditSettings settings, CancellationToken cancellationToken)"),
                new PullRequestChangedFile(
                    FilePath: "src/GitGavel/Configuration/KeyResolver.cs",
                    Status: "modified",
                    Additions: 12,
                    Deletions: 2,
                    Patch: "@@ -12,2 +12,12 @@ public static ResolvedKeys Resolve(GitGavelConfig config)"),
                new PullRequestChangedFile(
                    FilePath: "src/GitGavel/Git/GitService.cs",
                    Status: "modified",
                    Additions: 25,
                    Deletions: 3,
                    Patch: "@@ -65,3 +65,25 @@ public IReadOnlyList<GitCommitRecord> GetRecentCommits(string workingDirectory, int maxCount = 50)")
            ],
            ExistingReviewComments =
            [
                new ExistingPrReviewComment(
                    Id: 10,
                    FilePath: "tests/GitGavel.Tests/GitServiceTests.cs",
                    Line: 20,
                    Body: "Please isolate GITHUB_SHA and GITHUB_REF_NAME in the fallback test.",
                    Author: "maintainer",
                    CreatedAtUtc: DateTime.UtcNow.AddHours(-2))
            ],
            ExistingReviews =
            [
                new ExistingPrReviewSummary(
                    Id: 1,
                    State: "COMMENTED",
                    Body: "Looks promising, please check CI env var isolation.",
                    Author: "maintainer",
                    SubmittedAtUtc: DateTime.UtcNow.AddHours(-2))
            ]
        };

        return Task.FromResult<PullRequestContext?>(simulatedPr);
    }

    public Task<PostedPrReviewResult> PostPullRequestReviewAsync(
        string owner,
        string repo,
        PullRequestContext prContext,
        IReadOnlyList<PrCommentTriageDecision> decisions,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var prNumber = prContext.PrNumber;
        var inlineDecisions = config.Github.PullRequests.PostInlineComments
            ? decisions.Where(d => d.Action == PrCommentAction.PostInlineReview).ToList()
            : [];

        var patchByFile = prContext.ChangedFiles
            .GroupBy(f => f.FilePath.Replace('\\', '/').TrimStart('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Patch, StringComparer.OrdinalIgnoreCase);

        var inlineBodies = new List<string>();
        foreach (var d in inlineDecisions)
        {
            var body = IssueFormatter.FormatInlinePrComment(d);
            inlineBodies.Add(body);

            var normPath = d.Finding.FilePath.Replace('\\', '/').TrimStart('/');
            var diffPos = patchByFile.TryGetValue(normPath, out var patch) &&
                          GitHubService.TryMapFileLineToDiffPosition(patch, d.Finding.StartLine, out var mappedPos)
                ? mappedPos
                : Math.Max(1, d.Finding.StartLine);

            PostedPrInlineComments.Add((prNumber, normPath, diffPos, body));
        }

        var summaryBody = config.Github.PullRequests.PostSummaryComment
            ? IssueFormatter.FormatPrReviewSummary(prContext, decisions)
            : "";

        var verdict = config.Github.PullRequests.DefaultReviewEvent;

        var revId = ++_nextReviewId;
        var result = new PostedPrReviewResult(
            PrNumber: prNumber,
            ReviewId: revId,
            HtmlUrl: $"https://github.com/{owner}/{repo}/pull/{prNumber}#pullrequestreview-{revId}",
            InlineCommentsPosted: inlineBodies.Count,
            SummaryPosted: !string.IsNullOrWhiteSpace(summaryBody),
            Verdict: verdict
        );

        PostedPrReviews.Add((result, summaryBody, inlineBodies));
        return Task.FromResult(result);
    }

    public Task PostPullRequestCommentAsync(
        string owner,
        string repo,
        int prNumber,
        string commentBody,
        CancellationToken ct = default)
    {
        var marker = IssueFormatter.ExtractStickyCommentMarker(commentBody);
        if (!string.IsNullOrWhiteSpace(marker))
        {
            var existingIdx = PostedPrComments.FindIndex(c =>
                c.PrNumber == prNumber &&
                c.CommentBody.Contains(marker, StringComparison.OrdinalIgnoreCase));

            if (existingIdx >= 0)
            {
                PostedPrComments[existingIdx] = (prNumber, commentBody);
                return Task.CompletedTask;
            }
        }

        PostedPrComments.Add((prNumber, commentBody));
        return Task.CompletedTask;
    }
}
