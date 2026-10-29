using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.GitHub;

namespace GitGavel.Tests;

public class GitHubProjectsV2ClientTests
{
    private sealed class MockGraphQLHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    [Fact]
    public async Task SyncNodeToProjectV2Async_ExecutesFullGraphQLFlow_AddsItemAndSetsStatusAndPriorityFields()
    {
        var capturedRequests = new List<(string? AuthHeader, string Body)>();
        var handler = new MockGraphQLHandler(async (req, ct) =>
        {
            var auth = req.Headers.Authorization?.ToString();
            var body = await req.Content!.ReadAsStringAsync(ct);
            capturedRequests.Add((auth, body));

            string responseJson;
            if (body.Contains("projectV2(number:"))
            {
                responseJson = """
                {
                  "data": {
                    "organization": {
                      "projectV2": {
                        "id": "PVT_proj1",
                        "fields": {
                          "nodes": [
                            {
                              "id": "PVTSSF_status",
                              "name": "Status",
                              "options": [
                                { "id": "OPT_backlog", "name": "Backlog" },
                                { "id": "OPT_done", "name": "Done" }
                              ]
                            },
                            {
                              "id": "PVTSSF_prio",
                              "name": "Priority",
                              "options": [
                                { "id": "OPT_p1", "name": "P1" }
                              ]
                            }
                          ]
                        }
                      }
                    }
                  }
                }
                """;
            }
            else if (body.Contains("addProjectV2ItemById"))
            {
                responseJson = """
                {
                  "data": {
                    "addProjectV2ItemById": {
                      "item": { "id": "PVTI_item1" }
                    }
                  }
                }
                """;
            }
            else
            {
                responseJson = """{ "data": { "updateProjectV2ItemFieldValue": { "projectV2Item": { "id": "PVTI_item1" } } } }""";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var client = new GitHubProjectsV2Client(httpClient, "ghp_test_token");
        var config = new ProjectsV2Config { Enabled = true, ProjectNumber = 1 };

        await client.SyncNodeToProjectV2Async("my-org", "I_kwDO123", "Backlog", "P1", config);

        capturedRequests.Should().HaveCount(4);
        capturedRequests.Should().OnlyContain(r => r.AuthHeader == "Bearer ghp_test_token");
        capturedRequests[1].Body.Should().Contain("addProjectV2ItemById");
        capturedRequests[2].Body.Should().Contain("OPT_backlog");
        capturedRequests[3].Body.Should().Contain("OPT_p1");
    }

    [Fact]
    public async Task SyncNodeToProjectV2Async_WhenMissingTokenDisabledOrEmptyNodeId_ReturnsEarlyWithoutHttpCalls()
    {
        var callCount = 0;
        var handler = new MockGraphQLHandler((_, _) =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = new HttpClient(handler);

        var emptyTokenClient = new GitHubProjectsV2Client(httpClient, "");
        await emptyTokenClient.SyncNodeToProjectV2Async("org", "nodeId", "Backlog", "P1", new ProjectsV2Config { Enabled = true });
        callCount.Should().Be(0);

        var validClient = new GitHubProjectsV2Client(httpClient, "ghp_token");
        await validClient.SyncNodeToProjectV2Async("org", "nodeId", "Backlog", "P1", new ProjectsV2Config { Enabled = false });
        callCount.Should().Be(0);

        await validClient.SyncNodeToProjectV2Async("org", "   ", "Backlog", "P1", new ProjectsV2Config { Enabled = true });
        callCount.Should().Be(0);
    }

    [Fact]
    public async Task SyncNodeToProjectV2Async_WhenGraphQLErrorsHttpFailureOrMalformedJson_InvokesOnWarningWithoutThrowing()
    {
        var warnings = new List<string>();
        var responseQueue = new Queue<HttpResponseMessage>();

        var handler = new MockGraphQLHandler((_, _) => Task.FromResult(responseQueue.Dequeue()));
        using var httpClient = new HttpClient(handler);
        var client = new GitHubProjectsV2Client(httpClient, "ghp_token", msg => warnings.Add(msg));
        var config = new ProjectsV2Config { Enabled = true, ProjectNumber = 1 };

        // 1. GraphQL errors array
        responseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "errors": [{ "message": "Project not found" }] }""", Encoding.UTF8, "application/json")
        });
        await client.SyncNodeToProjectV2Async("org", "nodeId", "Backlog", "P1", config);
        warnings.Should().Contain(w => w.Contains("Project not found"));

        // 2. HTTP 500 error
        warnings.Clear();
        responseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await client.SyncNodeToProjectV2Async("org", "nodeId", "Backlog", "P1", config);
        warnings.Should().ContainSingle(w => w.Contains("Projects v2 synchronization failed"));

        // 3. Malformed JSON response
        warnings.Clear();
        responseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<<<not-json>>>", Encoding.UTF8, "application/json")
        });
        await client.SyncNodeToProjectV2Async("org", "nodeId", "Backlog", "P1", config);
        warnings.Should().ContainSingle(w => w.Contains("Projects v2 synchronization failed"));
    }

    [Fact]
    public void TryExtractProjectElement_ExtractsUserAndOrganizationProjectOrReturnsFalse()
    {
        using var userDoc = JsonDocument.Parse("""{ "data": { "user": { "projectV2": { "id": "PVT_user" } } } }""");
        GitHubProjectsV2Client.TryExtractProjectElement(userDoc.RootElement, out var userProj).Should().BeTrue();
        userProj.GetProperty("id").GetString().Should().Be("PVT_user");

        using var orgDoc = JsonDocument.Parse("""{ "data": { "organization": { "projectV2": { "id": "PVT_org" } } } }""");
        GitHubProjectsV2Client.TryExtractProjectElement(orgDoc.RootElement, out var orgProj).Should().BeTrue();
        orgProj.GetProperty("id").GetString().Should().Be("PVT_org");

        using var emptyDoc = JsonDocument.Parse("""{ "data": { "user": null, "organization": null } }""");
        GitHubProjectsV2Client.TryExtractProjectElement(emptyDoc.RootElement, out _).Should().BeFalse();

        using var noDataDoc = JsonDocument.Parse("""{ "errors": [] }""");
        GitHubProjectsV2Client.TryExtractProjectElement(noDataDoc.RootElement, out _).Should().BeFalse();
    }

    [Fact]
    public void FindOptionId_FindsMatchingOptionIdCaseInsensitivelyOrReturnsNull()
    {
        using var fieldDoc = JsonDocument.Parse("""
        {
          "id": "PVTSSF_1",
          "name": "Status",
          "options": [
            { "id": "OPT_1", "name": "Backlog" },
            { "id": "OPT_2", "name": "Done" }
          ]
        }
        """);

        GitHubProjectsV2Client.FindOptionId(fieldDoc.RootElement, "backlog").Should().Be("OPT_1");
        GitHubProjectsV2Client.FindOptionId(fieldDoc.RootElement, "DONE").Should().Be("OPT_2");
        GitHubProjectsV2Client.FindOptionId(fieldDoc.RootElement, "NonExistent").Should().BeNull();

        using var noOptionsDoc = JsonDocument.Parse("""{ "id": "PVTSSF_2", "name": "TextField" }""");
        GitHubProjectsV2Client.FindOptionId(noOptionsDoc.RootElement, "Backlog").Should().BeNull();
    }

    [Fact]
    public void HasGraphQLErrors_DetectsErrorsAndExtractsMessage()
    {
        using var okDoc = JsonDocument.Parse("""{ "data": { "viewer": { "login": "octocat" } } }""");
        GitHubProjectsV2Client.HasGraphQLErrors(okDoc.RootElement, out var noErr).Should().BeFalse();
        noErr.Should().BeEmpty();

        using var errDoc = JsonDocument.Parse("""{ "errors": [{ "message": "Field 'projectV2' doesn't exist" }] }""");
        GitHubProjectsV2Client.HasGraphQLErrors(errDoc.RootElement, out var msg).Should().BeTrue();
        msg.Should().Be("Field 'projectV2' doesn't exist");

        using var rawErrDoc = JsonDocument.Parse("""{ "errors": ["Raw error string"] }""");
        GitHubProjectsV2Client.HasGraphQLErrors(rawErrDoc.RootElement, out var rawMsg).Should().BeTrue();
        rawMsg.Should().Contain("Raw error string");
    }

    [Fact]
    public async Task ExecuteGraphQLAsync_AndSetProjectFieldAsync_HandlesSuccessAndHttpErrors()
    {
        string? capturedBody = null;
        string? capturedAuth = null;
        var shouldFail = false;

        var handler = new MockGraphQLHandler(async (req, ct) =>
        {
            if (shouldFail)
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            capturedAuth = req.Headers.Authorization?.ToString();
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "data": { "updateProjectV2ItemFieldValue": { "projectV2Item": { "id": "PVTI_1" } } } }""", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var client = new GitHubProjectsV2Client(httpClient, "ghp_direct_token");

        await client.SetProjectFieldAsync("PVT_proj", "PVTI_item", "PVTSSF_field", "OPT_val");
        capturedAuth.Should().Be("Bearer ghp_direct_token");
        capturedBody.Should().Contain("updateProjectV2ItemFieldValue");

        shouldFail = true;
        var act = () => client.ExecuteGraphQLAsync("query { viewer { login } }", new { });
        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
