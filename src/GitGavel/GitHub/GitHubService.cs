using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitGavel.Configuration;
using GitGavel.System1.Models;
using Octokit;

namespace GitGavel.GitHub;

public class GitHubService : IGitHubService
{
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

            return issues.Select(i => new ExistingIssue(
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
        if (string.IsNullOrWhiteSpace(_token) || !config.Github.ProjectsV2.Enabled)
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

            // 2. Add Issue to Project
            var addItemMutation = """
            mutation ($projectId: ID!, $contentId: ID!) {
              addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) {
                item { id }
              }
            }
            """;

            var addItemResp = await ExecuteGraphQLAsync(addItemMutation, new { projectId, contentId = issueResult.NodeId }, ct);
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

            // 3. Update Status Column (Backlog vs AI Triage vs Discarded)
            var targetColumnName = decision.TargetColumn switch
            {
                "Backlog" => config.Github.ProjectsV2.Columns.Backlog,
                "Discard" => config.Github.ProjectsV2.Columns.Discard,
                _ => config.Github.ProjectsV2.Columns.HumanReview
            };

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
                    else if (string.Equals(fieldName, config.Github.ProjectsV2.Fields.Priority, StringComparison.OrdinalIgnoreCase) && fNode.TryGetProperty("id", out var prioFId))
                    {
                        var prioFieldId = prioFId.GetString();
                        if (fNode.TryGetProperty("options", out var options))
                        {
                            foreach (var opt in options.EnumerateArray())
                            {
                                var optName = opt.TryGetProperty("name", out var on) ? on.GetString() : null;
                                if (string.Equals(optName, decision.PriorityLabel, StringComparison.OrdinalIgnoreCase) && opt.TryGetProperty("id", out var optId))
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
