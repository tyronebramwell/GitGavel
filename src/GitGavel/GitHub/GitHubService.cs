using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitGavel.Configuration;
using GitGavel.System1.Models;
using GitGavel.System2.Models;
using Octokit;

namespace GitGavel.GitHub;

public partial class GitHubService : IGitHubService
{
    [GeneratedRegex(@"^@@\s*-\d+(?:,\d+)?\s*\+(\d+)(?:,\d+)?\s*@@", RegexOptions.Compiled)]
    private static partial Regex DiffHunkHeaderRegex();
    private readonly GitHubClient _client;
    private readonly HttpClient _httpClient;
    private readonly string _token;

    public GitHubService(string? token = null, HttpClient? httpClient = null)
    {
        _token = token ?? Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "";
        _client = new GitHubClient(new Octokit.ProductHeaderValue("GitGavel"))
        {
            Credentials = string.IsNullOrWhiteSpace(_token) ? Credentials.Anonymous : new Credentials(_token)
        };
        _httpClient = httpClient ?? new HttpClient();
        if (!string.IsNullOrWhiteSpace(_token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitGavel", "1.0"));
        }
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

            var issues = await _client.Issue.GetAllForRepository(owner, repo, req);

            return issues
                .Where(i => i.PullRequest == null)
                .Select(i => new ExistingIssue(
                    Number: i.Number,
                    Title: i.Title,
                    Body: i.Body ?? "",
                    State: i.State.StringValue,
                    Labels: i.Labels.Select(l => l.Name).ToList()
                )).ToList();
        }
        catch
        {
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
        var targetColumnName = decision.TargetColumn switch
        {
            "Backlog" => config.Github.ProjectsV2.Columns.Backlog,
            "Done" => config.Github.ProjectsV2.Columns.Done,
            "Discard" => config.Github.ProjectsV2.Columns.Discard,
            _ => config.Github.ProjectsV2.Columns.HumanReview
        };

        await SyncNodeToProjectV2Async(owner, issueResult.NodeId, targetColumnName, decision.PriorityLabel, config, ct);
    }

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
            var pr = await _client.PullRequest.Get(owner, repo, prNumber);
            var files = await _client.PullRequest.Files(owner, repo, prNumber);
            var reviews = await _client.PullRequest.Review.GetAll(owner, repo, prNumber);
            var reviewComments = await _client.PullRequest.ReviewComment.GetAll(owner, repo, prNumber);

            return new PullRequestContext
            {
                PrNumber = pr.Number,
                Title = pr.Title,
                Body = pr.Body ?? "",
                BaseRef = pr.Base?.Ref ?? "main",
                HeadRef = pr.Head?.Ref ?? "feature",
                HeadSha = pr.Head?.Sha ?? "",
                ChangedFiles = files.Select(f => new PullRequestChangedFile(
                    FilePath: f.FileName,
                    Status: f.Status ?? "modified",
                    Additions: f.Additions,
                    Deletions: f.Deletions,
                    Patch: f.Patch ?? ""
                )).ToList(),
                ExistingReviews = reviews.Select(r => new ExistingPrReviewSummary(
                    Id: r.Id,
                    State: r.State.StringValue,
                    Body: r.Body ?? "",
                    Author: r.User?.Login ?? "unknown",
                    SubmittedAtUtc: r.SubmittedAt.UtcDateTime
                )).ToList(),
                ExistingReviewComments = reviewComments.Select(rc => new ExistingPrReviewComment(
                    Id: rc.Id,
                    FilePath: rc.Path ?? "",
                    Line: rc.Position,
                    Body: rc.Body ?? "",
                    Author: rc.User?.Login ?? "unknown",
                    CreatedAtUtc: rc.CreatedAt.UtcDateTime
                )).ToList()
            };
        }
        catch
        {
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
        catch
        {
            // Fallback: if diff position validation rejects an inline comment or self-approval, post summary comment on the PR
            var comment = await _client.Issue.Comment.Create(owner, repo, prNumber, summaryBody);
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

        await _client.Issue.Comment.Create(owner, repo, prNumber, commentBody);
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

    private async Task SyncNodeToProjectV2Async(
        string owner,
        string contentNodeId,
        string targetColumnName,
        string? priorityLabel,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token) || !config.Github.ProjectsV2.Enabled || string.IsNullOrWhiteSpace(contentNodeId))
        {
            return;
        }

        try
        {
            var projectNumber = config.Github.ProjectsV2.ProjectNumber;

            // 1. Fetch Project ID and Fields using GraphQL
            var query = """
            query ($owner: String!, $number: Int!) {
              user(login: $owner) {
                projectV2(number: $number) {
                  id
                  fields(first: 30) {
                    nodes {
                      ... on ProjectV2SingleSelectField {
                        id
                        name
                        options { id name }
                      }
                    }
                  }
                }
              }
              organization(login: $owner) {
                projectV2(number: $number) {
                  id
                  fields(first: 30) {
                    nodes {
                      ... on ProjectV2SingleSelectField {
                        id
                        name
                        options { id name }
                      }
                    }
                  }
                }
              }
            }
            """;

            var responseJson = await ExecuteGraphQLAsync(query, new { owner, number = projectNumber }, ct);
            using var doc = JsonDocument.Parse(responseJson);

            JsonElement projectElem;
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object && user.TryGetProperty("projectV2", out var uProj) && uProj.ValueKind == JsonValueKind.Object)
                {
                    projectElem = uProj;
                }
                else if (data.TryGetProperty("organization", out var org) && org.ValueKind == JsonValueKind.Object && org.TryGetProperty("projectV2", out var oProj) && oProj.ValueKind == JsonValueKind.Object)
                {
                    projectElem = oProj;
                }
                else
                {
                    return; // Project not found
                }
            }
            else
            {
                return;
            }

            var projectId = projectElem.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(projectId)) return;

            // 2. Add Issue to Project (idempotent in Projects v2 GraphQL API)
            var addItemMutation = """
            mutation ($projectId: ID!, $contentId: ID!) {
              addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) {
                item { id }
              }
            }
            """;

            var addItemResp = await ExecuteGraphQLAsync(addItemMutation, new { projectId, contentId = contentNodeId }, ct);
            using var addItemDoc = JsonDocument.Parse(addItemResp);

            string? itemId = null;
            if (addItemDoc.RootElement.TryGetProperty("data", out var mData) &&
                mData.TryGetProperty("addProjectV2ItemById", out var addRes) &&
                addRes.TryGetProperty("item", out var itemNode) &&
                itemNode.TryGetProperty("id", out var idProp))
            {
                itemId = idProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(itemId)) return;

            // 3. Update Status Column (Backlog vs AI Triage vs Done vs Discarded)
            if (projectElem.TryGetProperty("fields", out var fieldsElem) && fieldsElem.TryGetProperty("nodes", out var nodes))
            {
                foreach (var fNode in nodes.EnumerateArray())
                {
                    var fieldName = fNode.TryGetProperty("name", out var fn) ? fn.GetString() : null;
                    if (fieldName is "Status" or "status" && fNode.TryGetProperty("id", out var fId))
                    {
                        var statusFieldId = fId.GetString();
                        if (fNode.TryGetProperty("options", out var options))
                        {
                            foreach (var opt in options.EnumerateArray())
                            {
                                var optName = opt.TryGetProperty("name", out var on) ? on.GetString() : null;
                                if (string.Equals(optName, targetColumnName, StringComparison.OrdinalIgnoreCase) && opt.TryGetProperty("id", out var optId))
                                {
                                    var optionId = optId.GetString();
                                    await SetProjectFieldAsync(projectId, itemId, statusFieldId!, optionId!, ct);
                                    break;
                                }
                            }
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(priorityLabel) &&
                             string.Equals(fieldName, config.Github.ProjectsV2.Fields.Priority, StringComparison.OrdinalIgnoreCase) &&
                             fNode.TryGetProperty("id", out var prioFId))
                    {
                        var prioFieldId = prioFId.GetString();
                        if (fNode.TryGetProperty("options", out var options))
                        {
                            foreach (var opt in options.EnumerateArray())
                            {
                                var optName = opt.TryGetProperty("name", out var on) ? on.GetString() : null;
                                if (string.Equals(optName, priorityLabel, StringComparison.OrdinalIgnoreCase) && opt.TryGetProperty("id", out var optId))
                                {
                                    var optionId = optId.GetString();
                                    await SetProjectFieldAsync(projectId, itemId, prioFieldId!, optionId!, ct);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Do not fail the audit run if GitHub Projects v2 automation encounters permission or mapping issues
        }
    }

    private async Task SetProjectFieldAsync(string projectId, string itemId, string fieldId, string optionId, CancellationToken ct)
    {
        var mutation = """
        mutation ($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
          updateProjectV2ItemFieldValue(input: {
            projectId: $projectId,
            itemId: $itemId,
            fieldId: $fieldId,
            value: { singleSelectOptionId: $optionId }
          }) {
            projectV2Item { id }
          }
        }
        """;

        await ExecuteGraphQLAsync(mutation, new { projectId, itemId, fieldId, optionId }, ct);
    }

    private async Task<string> ExecuteGraphQLAsync(string query, object variables, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var resp = await _httpClient.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static readonly GitGavel.Git.IGitService DefaultGitService = new GitGavel.Git.GitService();

    public static (string owner, string repo) ResolveRepoCoordinates(string workingDirectory, string? explicitRepo = null) =>
        DefaultGitService.ResolveRepoCoordinates(workingDirectory, explicitRepo);
}
