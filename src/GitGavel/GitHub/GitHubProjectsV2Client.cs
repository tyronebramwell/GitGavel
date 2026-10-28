using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitGavel.Configuration;

namespace GitGavel.GitHub;

public class GitHubProjectsV2Client
{
    private const string FetchProjectQuery = """
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

    private const string AddItemMutation = """
        mutation ($projectId: ID!, $contentId: ID!) {
          addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) {
            item { id }
          }
        }
        """;

    private const string UpdateFieldValueMutation = """
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

    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly Action<string>? _onWarning;

    public GitHubProjectsV2Client(HttpClient httpClient, string token, Action<string>? onWarning = null)
    {
        _httpClient = httpClient;
        _token = token;
        _onWarning = onWarning;
    }

    public async Task SyncNodeToProjectV2Async(
        string owner,
        string contentNodeId,
        string targetColumnName,
        string? priorityLabel,
        ProjectsV2Config projectsConfig,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token) || !projectsConfig.Enabled || string.IsNullOrWhiteSpace(contentNodeId))
        {
            return;
        }

        try
        {
            var projectNumber = projectsConfig.ProjectNumber;
            var responseJson = await ExecuteGraphQLAsync(FetchProjectQuery, new { owner, number = projectNumber }, ct);
            using var doc = JsonDocument.Parse(responseJson);

            if (HasGraphQLErrors(doc.RootElement, out var errorMsg))
            {
                _onWarning?.Invoke($"Projects v2 GraphQL query error: {errorMsg}");
            }

            if (!TryExtractProjectElement(doc.RootElement, out var projectElem))
            {
                return;
            }

            var projectId = projectElem.TryGetProperty("id", out var pidProp) ? pidProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(projectId)) return;

            var addItemResp = await ExecuteGraphQLAsync(AddItemMutation, new { projectId, contentId = contentNodeId }, ct);
            using var addItemDoc = JsonDocument.Parse(addItemResp);

            if (HasGraphQLErrors(addItemDoc.RootElement, out var mutError))
            {
                _onWarning?.Invoke($"Projects v2 addProjectV2ItemById error: {mutError}");
            }

            string? itemId = null;
            if (addItemDoc.RootElement.TryGetProperty("data", out var mData) &&
                mData.ValueKind == JsonValueKind.Object &&
                mData.TryGetProperty("addProjectV2ItemById", out var addRes) &&
                addRes.ValueKind == JsonValueKind.Object &&
                addRes.TryGetProperty("item", out var itemNode) &&
                itemNode.ValueKind == JsonValueKind.Object &&
                itemNode.TryGetProperty("id", out var idProp))
            {
                itemId = idProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(itemId)) return;

            if (projectElem.TryGetProperty("fields", out var fieldsElem) &&
                fieldsElem.ValueKind == JsonValueKind.Object &&
                fieldsElem.TryGetProperty("nodes", out var nodes) &&
                nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var fNode in nodes.EnumerateArray())
                {
                    if (fNode.ValueKind != JsonValueKind.Object) continue;
                    var fieldName = fNode.TryGetProperty("name", out var fn) ? fn.GetString() : null;

                    if (fieldName is "Status" or "status" && fNode.TryGetProperty("id", out var fId))
                    {
                        var statusFieldId = fId.GetString();
                        var optionId = FindOptionId(fNode, targetColumnName);
                        if (!string.IsNullOrWhiteSpace(statusFieldId) && !string.IsNullOrWhiteSpace(optionId))
                        {
                            await SetProjectFieldAsync(projectId, itemId, statusFieldId, optionId, ct);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(priorityLabel) &&
                             string.Equals(fieldName, projectsConfig.Fields.Priority, StringComparison.OrdinalIgnoreCase) &&
                             fNode.TryGetProperty("id", out var prioFId))
                    {
                        var prioFieldId = prioFId.GetString();
                        var optionId = FindOptionId(fNode, priorityLabel);
                        if (!string.IsNullOrWhiteSpace(prioFieldId) && !string.IsNullOrWhiteSpace(optionId))
                        {
                            await SetProjectFieldAsync(projectId, itemId, prioFieldId, optionId, ct);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _onWarning?.Invoke($"Projects v2 synchronization failed: {ex.Message}");
        }
    }

    private static bool TryExtractProjectElement(JsonElement root, out JsonElement projectElem)
    {
        projectElem = default;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (data.TryGetProperty("user", out var user) &&
            user.ValueKind == JsonValueKind.Object &&
            user.TryGetProperty("projectV2", out var uProj) &&
            uProj.ValueKind == JsonValueKind.Object)
        {
            projectElem = uProj;
            return true;
        }

        if (data.TryGetProperty("organization", out var org) &&
            org.ValueKind == JsonValueKind.Object &&
            org.TryGetProperty("projectV2", out var oProj) &&
            oProj.ValueKind == JsonValueKind.Object)
        {
            projectElem = oProj;
            return true;
        }

        return false;
    }

    private static string? FindOptionId(JsonElement fieldNode, string targetOptionName)
    {
        if (!fieldNode.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var opt in options.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.Object) continue;
            var optName = opt.TryGetProperty("name", out var on) ? on.GetString() : null;
            if (string.Equals(optName, targetOptionName, StringComparison.OrdinalIgnoreCase) &&
                opt.TryGetProperty("id", out var optId))
            {
                return optId.GetString();
            }
        }

        return null;
    }

    private static bool HasGraphQLErrors(JsonElement root, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
        {
            return false;
        }

        var first = errors[0];
        errorMessage = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("message", out var msg)
            ? msg.GetString() ?? "GraphQL error"
            : errors.ToString();
        return true;
    }

    private async Task SetProjectFieldAsync(string projectId, string itemId, string fieldId, string optionId, CancellationToken ct)
    {
        await ExecuteGraphQLAsync(UpdateFieldValueMutation, new { projectId, itemId, fieldId, optionId }, ct);
    }

    private async Task<string> ExecuteGraphQLAsync(string query, object variables, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("GitGavel", "1.0"));

        using var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }
}
