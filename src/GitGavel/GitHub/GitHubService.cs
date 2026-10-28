using System.Text.Json;
using System.Text.RegularExpressions;
using GitGavel.Configuration;
using GitGavel.System1.Models;
using GitGavel.System2.Models;
using Octokit;

namespace GitGavel.GitHub;

public partial class GitHubService : IGitHubService, IDisposable
{
    [GeneratedRegex(@"^@@\s*-\d+(?:,\d+)?\s*\+(\d+)(?:,\d+)?\s*@@", RegexOptions.Compiled)]
    private static partial Regex DiffHunkHeaderRegex();

    private readonly IGitHubClient _client;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _token;
    private readonly Action<string>? _onWarning;
    private readonly GitHubProjectsV2Client _projectsClient;
    private bool _disposed;

    public GitHubService(
        string? token = null,
        HttpClient? httpClient = null,
        IGitHubClient? githubClient = null,
        Action<string>? onWarning = null)
    {
        _token = token ?? Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "";
        _onWarning = onWarning;
        _client = githubClient ?? new GitHubClient(new ProductHeaderValue("GitGavel"))
        {
            Credentials = string.IsNullOrWhiteSpace(_token) ? Credentials.Anonymous : new Credentials(_token)
        };
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        _projectsClient = new GitHubProjectsV2Client(_httpClient, _token, _onWarning);
    }

    public async Task<List<ExistingIssue>> GetExistingIssuesAsync(string owner, string repo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            return [];
        }

        try
        {
            var req = new RepositoryIssueRequest
            {
                State = ItemStateFilter.All,
                Filter = IssueFilter.All
            };

            var apiOptions = new ApiOptions
            {
                PageSize = 100,
                PageCount = 5
            };

            var issues = await _client.Issue.GetAllForRepository(owner, repo, req, apiOptions);

            return issues
                .Where(i => i.PullRequest == null)
                .Select(i =>
                {
                    var body = i.Body ?? "";
                    var meta = IssueMetadataParser.ParseMetadata(body);
                    return new ExistingIssue(
                        Number: i.Number,
                        Title: i.Title,
                        Body: body,
                        State: i.State.StringValue,
                        Labels: i.Labels.Select(l => l.Name).ToList(),
                        Fingerprint: meta?.Fingerprint
                    );
                }).ToList();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            _onWarning?.Invoke($"Failed to fetch existing issues for {owner}/{repo}: {ex.Message}");
            return [];
        }
    }

    public async Task<CreatedIssueResult> CreateOrUpdateIssueAsync(
        string owner,
        string repo,
        TriageDecision decision,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            throw new InvalidOperationException("GH_TOKEN is required to create issues on GitHub.");
        }

        var labels = new List<string>(config.Github.Labels);
        labels.Add(decision.PriorityLabel.ToLowerInvariant());
        labels.Add(decision.Finding.Category.ToLowerInvariant());

        if (decision.Status == TriageStatus.RoutedToHumanReview)
        {
            labels.Add("needs-maintainer-review");
        }

        var formattedBody = IssueFormatter.FormatIssueBody(decision);

        var newIssue = new NewIssue(decision.Finding.Title)
        {
            Body = formattedBody
        };

        foreach (var l in labels.Distinct())
        {
            newIssue.Labels.Add(l);
        }

        var issue = await _client.Issue.Create(owner, repo, newIssue);

        return new CreatedIssueResult(
            IssueNumber: issue.Number,
            HtmlUrl: issue.HtmlUrl,
            NodeId: issue.NodeId,
            Title: issue.Title,
            WasUpdated: false
        );
    }

    public async Task AssignToProjectV2Async(
        string owner,
        string repo,
        CreatedIssueResult issueResult,
        TriageDecision decision,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var targetColumnName = ResolveTargetColumnName(decision.TargetColumn, config.Github.ProjectsV2.Columns);
        await SyncNodeToProjectV2Async(owner, issueResult.NodeId, targetColumnName, decision.PriorityLabel, config, ct);
    }

    public static string ResolveTargetColumnName(string? targetColumn, ProjectColumnsConfig columns) =>
        targetColumn switch
        {
            "Backlog" => columns.Backlog,
            "Done" => columns.Done,
            "Discard" => columns.Discard,
            _ => columns.HumanReview
        };

    public async Task<ClosedIssueResult> CloseResolvedIssueAsync(
        string owner,
        string repo,
        IssueResolutionDecision decision,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            throw new InvalidOperationException("GH_TOKEN is required to close issues on GitHub.");
        }

        var c = decision.Candidate;
        var commentBody = IssueFormatter.FormatIssueResolutionComment(decision);

        await _client.Issue.Comment.Create(owner, repo, c.IssueNumber, commentBody);

        var stateReason = string.Equals(config.Github.IssueClosure.StateReason, "not_planned", StringComparison.OrdinalIgnoreCase)
            ? ItemStateReason.NotPlanned
            : ItemStateReason.Completed;

        var issueUpdate = new IssueUpdate
        {
            State = ItemState.Closed,
            StateReason = stateReason
        };

        var updatedIssue = await _client.Issue.Update(owner, repo, c.IssueNumber, issueUpdate);

        await SyncNodeToProjectV2Async(
            owner,
            updatedIssue.NodeId,
            config.Github.ProjectsV2.Columns.Done,
            priorityLabel: null,
            config,
            ct);

        return new ClosedIssueResult(
            IssueNumber: updatedIssue.Number,
            Title: updatedIssue.Title,
            HtmlUrl: updatedIssue.HtmlUrl,
            NodeId: updatedIssue.NodeId,
            HowItWasFixed: c.HowItWasFixed,
            ResolvingCommits: c.ResolvingCommitSummaries.Count > 0 ? c.ResolvingCommitSummaries : c.ResolvingCommitShas,
            ResolutionConfidence: decision.ResolutionProbability,
            ClosedAtUtc: DateTime.UtcNow
        );
    }

    public async Task<PullRequestContext?> GetPullRequestContextAsync(
        string owner,
        string repo,
        int prNumber,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token) || prNumber <= 0)
        {
            return null;
        }

        try
        {
            var prTask = _client.PullRequest.Get(owner, repo, prNumber);
            var filesTask = _client.PullRequest.Files(owner, repo, prNumber);
            var reviewsTask = _client.PullRequest.Review.GetAll(owner, repo, prNumber);
            var reviewCommentsTask = _client.PullRequest.ReviewComment.GetAll(owner, repo, prNumber);

            await Task.WhenAll(prTask, filesTask, reviewsTask, reviewCommentsTask);

            var pr = await prTask;
            var files = await filesTask;
            var reviews = await reviewsTask;
            var reviewComments = await reviewCommentsTask;

            var changedFiles = files.Select(f => new PullRequestChangedFile(
                FilePath: f.FileName,
                Status: f.Status ?? "modified",
                Additions: f.Additions,
                Deletions: f.Deletions,
                Patch: f.Patch ?? ""
            )).ToList();

            var patchByPath = changedFiles
                .GroupBy(f => f.FilePath.Replace('\\', '/').TrimStart('/'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Patch, StringComparer.OrdinalIgnoreCase);

            return new PullRequestContext
            {
                PrNumber = pr.Number,
                Title = pr.Title,
                Body = pr.Body ?? "",
                BaseRef = pr.Base?.Ref ?? "main",
                HeadRef = pr.Head?.Ref ?? "feature",
                HeadSha = pr.Head?.Sha ?? "",
                ChangedFiles = changedFiles,
                ExistingReviews = reviews.Select(r => new ExistingPrReviewSummary(
                    Id: r.Id,
                    State: r.State.StringValue,
                    Body: r.Body ?? "",
                    Author: r.User?.Login ?? "unknown",
                    SubmittedAtUtc: r.SubmittedAt.UtcDateTime
                )).ToList(),
                ExistingReviewComments = reviewComments.Select(rc =>
                {
                    var rcPath = (rc.Path ?? "").Replace('\\', '/').TrimStart('/');
                    var resolvedLine = rc.Position;
                    if (rc.Position.HasValue &&
                        patchByPath.TryGetValue(rcPath, out var patch) &&
                        TryMapDiffPositionToFileLine(patch, rc.Position.Value, out var fileLine))
                    {
                        resolvedLine = fileLine;
                    }

                    return new ExistingPrReviewComment(
                        Id: rc.Id,
                        FilePath: rc.Path ?? "",
                        Line: resolvedLine,
                        Body: rc.Body ?? "",
                        Author: rc.User?.Login ?? "unknown",
                        CreatedAtUtc: rc.CreatedAt.UtcDateTime
                    );
                }).ToList()
            };
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            _onWarning?.Invoke($"Failed to fetch Pull Request #{prNumber} context for {owner}/{repo}: {ex.Message}");
            return null;
        }
    }

    public async Task<PostedPrReviewResult> PostPullRequestReviewAsync(
        string owner,
        string repo,
        PullRequestContext prContext,
        IReadOnlyList<PrCommentTriageDecision> decisions,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            throw new InvalidOperationException("GH_TOKEN is required to post Pull Request reviews on GitHub.");
        }

        var prNumber = prContext.PrNumber;
        var inlineDecisions = config.Github.PullRequests.PostInlineComments
            ? decisions.Where(d => d.Action == PrCommentAction.PostInlineReview).ToList()
            : [];

        var summaryBody = config.Github.PullRequests.PostSummaryComment
            ? IssueFormatter.FormatPrReviewSummary(prContext, decisions)
            : "GitGavel automated Pull Request audit.";

        var eventStr = config.Github.PullRequests.DefaultReviewEvent;

        var reviewEvent = eventStr.ToUpperInvariant() switch
        {
            "APPROVE" => PullRequestReviewEvent.Approve,
            "REQUEST_CHANGES" => PullRequestReviewEvent.RequestChanges,
            _ => PullRequestReviewEvent.Comment
        };

        var patchByFile = prContext.ChangedFiles
            .GroupBy(f => f.FilePath.Replace('\\', '/').TrimStart('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Patch, StringComparer.OrdinalIgnoreCase);

        var draftComments = new List<DraftPullRequestReviewComment>();
        foreach (var d in inlineDecisions)
        {
            var normPath = d.Finding.FilePath.Replace('\\', '/').TrimStart('/');
            if (patchByFile.TryGetValue(normPath, out var patch) &&
                TryMapFileLineToDiffPosition(patch, d.Finding.StartLine, out var diffPos))
            {
                draftComments.Add(new DraftPullRequestReviewComment(
                    IssueFormatter.FormatInlinePrComment(d),
                    normPath,
                    diffPos));
            }
        }

        try
        {
            if (draftComments.Count == 0)
            {
                var upserted = await UpsertPullRequestCommentAsync(owner, repo, prNumber, summaryBody);
                return new PostedPrReviewResult(
                    PrNumber: prNumber,
                    ReviewId: upserted.Id,
                    HtmlUrl: upserted.HtmlUrl,
                    InlineCommentsPosted: 0,
                    SummaryPosted: true,
                    Verdict: "COMMENT"
                );
            }

            var reviewCreate = new PullRequestReviewCreate
            {
                Body = summaryBody,
                Event = reviewEvent,
                CommitId = string.IsNullOrWhiteSpace(prContext.HeadSha) ? null : prContext.HeadSha
            };

            foreach (var dc in draftComments)
            {
                reviewCreate.Comments.Add(dc);
            }

            var createdReview = await _client.PullRequest.Review.Create(owner, repo, prNumber, reviewCreate);
            return new PostedPrReviewResult(
                PrNumber: prNumber,
                ReviewId: createdReview.Id,
                HtmlUrl: createdReview.HtmlUrl,
                InlineCommentsPosted: draftComments.Count,
                SummaryPosted: true,
                Verdict: eventStr.ToUpperInvariant()
            );
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            _onWarning?.Invoke($"Falling back to sticky PR comment for PR #{prNumber}: {ex.Message}");
            var comment = await UpsertPullRequestCommentAsync(owner, repo, prNumber, summaryBody);
            return new PostedPrReviewResult(
                PrNumber: prNumber,
                ReviewId: comment.Id,
                HtmlUrl: comment.HtmlUrl,
                InlineCommentsPosted: 0,
                SummaryPosted: true,
                Verdict: "COMMENT"
            );
        }
    }

    public async Task PostPullRequestCommentAsync(
        string owner,
        string repo,
        int prNumber,
        string commentBody,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            throw new InvalidOperationException("GH_TOKEN is required to post Pull Request comments on GitHub.");
        }

        await UpsertPullRequestCommentAsync(owner, repo, prNumber, commentBody);
    }

    private async Task<IssueComment> UpsertPullRequestCommentAsync(
        string owner,
        string repo,
        int prNumber,
        string commentBody)
    {
        var marker = IssueFormatter.ExtractStickyCommentMarker(commentBody);
        if (!string.IsNullOrWhiteSpace(marker))
        {
            try
            {
                var existingComments = await _client.Issue.Comment.GetAllForIssue(owner, repo, prNumber);
                var existingMatch = existingComments.LastOrDefault(c =>
                    c.Body != null && c.Body.Contains(marker, StringComparison.OrdinalIgnoreCase));

                if (existingMatch != null)
                {
                    return await _client.Issue.Comment.Update(owner, repo, existingMatch.Id, commentBody);
                }
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                _onWarning?.Invoke($"Could not update existing sticky comment on PR #{prNumber}, creating new comment: {ex.Message}");
            }
        }

        return await _client.Issue.Comment.Create(owner, repo, prNumber, commentBody);
    }

    public static bool TryMapFileLineToDiffPosition(string? patch, int targetLine, out int position)
    {
        position = 0;
        if (string.IsNullOrWhiteSpace(patch) || targetLine < 1)
        {
            return false;
        }

        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var currentNewLine = 0;
        var inHunk = false;
        var currentPos = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = DiffHunkHeaderRegex().Match(line);
            if (match.Success)
            {
                if (inHunk)
                {
                    currentPos++;
                }
                inHunk = true;
                currentNewLine = int.Parse(match.Groups[1].Value);
                continue;
            }

            if (!inHunk || string.IsNullOrEmpty(line)) continue;

            currentPos++;

            if (line.StartsWith('+'))
            {
                if (currentNewLine == targetLine)
                {
                    position = currentPos;
                    return true;
                }
                currentNewLine++;
            }
            else if (line.StartsWith('-'))
            {
                // Deleted line does not advance new file line number
            }
            else
            {
                if (currentNewLine == targetLine)
                {
                    position = currentPos;
                    return true;
                }
                currentNewLine++;
            }
        }

        return false;
    }

    public static bool TryMapDiffPositionToFileLine(string? patch, int diffPosition, out int fileLine)
    {
        fileLine = 0;
        if (string.IsNullOrWhiteSpace(patch) || diffPosition < 1)
        {
            return false;
        }

        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var currentNewLine = 0;
        var inHunk = false;
        var currentPos = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = DiffHunkHeaderRegex().Match(line);
            if (match.Success)
            {
                if (inHunk)
                {
                    currentPos++;
                }
                inHunk = true;
                currentNewLine = int.Parse(match.Groups[1].Value);
                continue;
            }

            if (!inHunk || string.IsNullOrEmpty(line)) continue;

            currentPos++;

            if (line.StartsWith('-'))
            {
                if (currentPos == diffPosition)
                {
                    fileLine = currentNewLine;
                    return true;
                }
            }
            else
            {
                if (currentPos == diffPosition)
                {
                    fileLine = currentNewLine;
                    return true;
                }
                currentNewLine++;
            }
        }

        return false;
    }

    public Task SyncNodeToProjectV2Async(
        string owner,
        string contentNodeId,
        string targetColumnName,
        string? priorityLabel,
        GitGavelConfig config,
        CancellationToken ct = default) =>
        _projectsClient.SyncNodeToProjectV2Async(owner, contentNodeId, targetColumnName, priorityLabel, config.Github.ProjectsV2, ct);

    private static readonly GitGavel.Git.IGitService DefaultGitService = new GitGavel.Git.GitService();

    public static (string owner, string repo) ResolveRepoCoordinates(string workingDirectory, string? explicitRepo = null) =>
        DefaultGitService.ResolveRepoCoordinates(workingDirectory, explicitRepo);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
