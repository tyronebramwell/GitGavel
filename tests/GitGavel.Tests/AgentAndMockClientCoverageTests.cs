using FluentAssertions;
using GitGavel.Discovery;
using GitGavel.Git;
using GitGavel.System1.Models;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Agents;
using GitGavel.System2.Models;
using GitGavel.System2.Providers;

namespace GitGavel.Tests;

public class AgentAndMockClientCoverageTests
{
    [Fact]
    public async Task TestSentinelAgent_ShouldSkipExecution_AndBuildPromptAsync_CoversAllBranches()
    {
        string? capturedPrompt = null;
        var mockLlm = new MockLlmClient(req =>
        {
            capturedPrompt = req.Prompt;
            return new LlmResponse { Content = "[]", ModelUsed = req.Model };
        });

        var agent = new TestSentinelAgent(mockLlm, "gemini-2.5-pro", ["unit", "integration"]);

        // 1. ShouldSkipExecution when both SourceFiles and TestFiles are empty
        var emptyCtx = new RepoContext { RootDirectory = "/repo", SpecFiles = [], SourceFiles = [], TestFiles = [], SarifFindings = [] };
        var skippedRes = await agent.ExecuteAsync(emptyCtx);
        skippedRes.IsSuccess.Should().BeTrue();
        skippedRes.Findings.Should().BeEmpty();
        capturedPrompt.Should().BeNull();

        // 2. BuildPromptAsync with SourceFiles, TestFiles, and PR mode
        var populatedCtx = new RepoContext
        {
            RootDirectory = "/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/App.cs", "/repo/src/App.cs", 10, 100, "public class App { public void Run() {} }", false, false, "@@ -1,1 +1,1 @@")],
            TestFiles = [new ScannedFile("tests/AppTests.cs", "/repo/tests/AppTests.cs", 10, 100, "public class AppTests { public void TestRun() {} }", false, true)],
            SarifFindings = [],
            ActivePullRequest = new PullRequestContext { PrNumber = 121, Title = "Fix issues" }
        };

        var execRes = await agent.ExecuteAsync(populatedCtx);
        execRes.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().Contain("Target Test Tiers: unit, integration");
        capturedPrompt.Should().Contain("PULL REQUEST REVIEW MODE (PR #121: Fix issues)");
        capturedPrompt.Should().Contain("### Production Symbol & Complexity Map:");
        capturedPrompt.Should().Contain("### Existing Automated Test Suite Symbol Map");
        capturedPrompt.Should().Contain("TestRun");
    }

    [Fact]
    public async Task SpecDriftAgent_ShouldSkipExecution_AndBuildPromptAsync_CoversAllBranches()
    {
        string? capturedPrompt = null;
        var mockLlm = new MockLlmClient(req =>
        {
            capturedPrompt = req.Prompt;
            return new LlmResponse { Content = "[]", ModelUsed = req.Model };
        });

        var agent = new SpecDriftAgent(mockLlm);

        // 1. ShouldSkipExecution when SpecFiles is empty
        var noSpecCtx = new RepoContext
        {
            RootDirectory = "/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/App.cs", "/repo/src/App.cs", 5, 50, "class App {}", false, false)],
            SarifFindings = []
        };
        var skipped = await agent.ExecuteAsync(noSpecCtx);
        skipped.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().BeNull();

        // 2. BuildPromptAsync with SpecFiles, SourceFiles, and PR mode
        var withSpecCtx = noSpecCtx with
        {
            SpecFiles = [new ScannedFile("SPECIFICATION.md", "/repo/SPECIFICATION.md", 5, 50, "# Spec", true, false)],
            ActivePullRequest = new PullRequestContext { PrNumber = 99, Title = "Spec update" }
        };
        var ran = await agent.ExecuteAsync(withSpecCtx);
        ran.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().Contain("PULL REQUEST REVIEW MODE (PR #99: Spec update)");
        capturedPrompt.Should().Contain("### Specification Documents:");
        capturedPrompt.Should().Contain("SPECIFICATION.md");
        capturedPrompt.Should().Contain("### Source Code Implementation:");
    }

    [Fact]
    public async Task BaseLlmAgent_ExecuteAsync_WhenLlmThrowsOrReturnsMalformedJson_HandlesGracefully()
    {
        var throwingClient = new MockLlmClient(_ => throw new HttpRequestException("API unavailable"));
        var agent1 = new SpecDriftAgent(throwingClient);
        var ctx = new RepoContext
        {
            RootDirectory = "/repo",
            SpecFiles = [new ScannedFile("README.md", "/repo/README.md", 1, 10, "# Doc", true, false)],
            SourceFiles = [],
            SarifFindings = []
        };

        var res1 = await agent1.ExecuteAsync(ctx);
        res1.IsSuccess.Should().BeFalse();
        res1.ErrorMessage.Should().Contain("API unavailable");

        var malformedClient = new MockLlmClient(_ => new LlmResponse { Content = "not valid json at all", ModelUsed = "gemini-2.5-pro" });
        var agent2 = new SpecDriftAgent(malformedClient);
        var res2 = await agent2.ExecuteAsync(ctx);
        res2.IsSuccess.Should().BeTrue();
        res2.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task IssueResolutionAgent_EvaluateOpenIssuesAsync_AndEnrichCommitSummaries_CoversAllScenarios()
    {
        var mockLlm = new MockLlmClient(_ => new LlmResponse
        {
            Content = """
            [
              {
                "issueNumber": 10,
                "issueTitle": "First issue",
                "isResolved": true,
                "howItWasFixed": "Fixed in commit",
                "resolvingCommitShas": []
              },
              {
                "issueNumber": 11,
                "issueTitle": "Second issue",
                "isResolved": true,
                "howItWasFixed": "Fixed in multiple commits",
                "resolvingCommitShas": ["abcdef1234567", "9999999888888"]
              }
            ]
            """,
            ModelUsed = "gemini-2.5-flash"
        });

        var agent = new IssueResolutionAgent(mockLlm);
        var ctx = new RepoContext
        {
            RootDirectory = "/repo",
            SpecFiles = [],
            SourceFiles = [],
            SarifFindings = [],
            RecentCommits =
            [
                new GitCommitRecord("abcdef1234567", "abcdef1", "fix: resolve issue", "dev", "2026-10-28", ["src/App.cs"])
            ]
        };

        // 1. Empty or only closed issues -> returns early
        var emptyRes = await agent.EvaluateOpenIssuesAsync([new ExistingIssue(1, "Closed", "body", "closed")], ctx);
        emptyRes.Candidates.Should().BeEmpty();

        // 2. Active open issues -> enriches missing SHA from latest commit, matches known SHA, formats unknown SHA
        var openIssues = new List<ExistingIssue>
        {
            new(10, "First issue", "Body 10", "open"),
            new(11, "Second issue", "Body 11", "open")
        };
        var res = await agent.EvaluateOpenIssuesAsync(openIssues, ctx);
        res.IsSuccess.Should().BeTrue();
        res.Candidates.Should().HaveCount(2);
        res.Candidates[0].ResolvingCommitShas.Should().Equal("abcdef1234567");
        res.Candidates[0].ResolvingCommitSummaries.Should().ContainSingle().Which.Should().Contain("`abcdef1` — *fix: resolve issue*");
        res.Candidates[1].ResolvingCommitSummaries.Should().HaveCount(2);
        res.Candidates[1].ResolvingCommitSummaries[1].Should().Be("`9999999`");
    }

    [Fact]
    public async Task MockLlmClient_RegisterAgentResponse_RegisterRule_AndInferAgentFromPrompt_WorksForAllPrefixes()
    {
        var client = new MockLlmClient();

        // InferAgentFromPrompt fallback when AgentName is omitted
        (await client.GenerateAsync(new LlmRequest { Prompt = "You are the Spec Drift Agent..." })).Content.Should().Contain("SpecDrift");
        (await client.GenerateAsync(new LlmRequest { Prompt = "You are the Security Auditor Agent..." })).Content.Should().Contain("Security");
        (await client.GenerateAsync(new LlmRequest { Prompt = "You are the Test Sentinel Agent..." })).Content.Should().Contain("Testing");
        (await client.GenerateAsync(new LlmRequest { Prompt = "You are the Issue Resolution Agent..." })).Content.Should().Contain("isResolved");
        (await client.GenerateAsync(new LlmRequest { Prompt = "Unrecognized prompt prefix" })).Content.Should().Contain("Refactoring");

        // RegisterAgentResponse overrides default response for that agent
        client.RegisterAgentResponse("SpecDriftAgent", "[{\"title\":\"CustomSpecFinding\"}]");
        (await client.GenerateAsync(new LlmRequest { AgentName = "SpecDriftAgent", Prompt = "any" })).Content.Should().Contain("CustomSpecFinding");

        // RegisterRule takes priority over AgentName
        client.RegisterRule(r => r.Prompt.Contains("OVERRIDE_RULE"), "[{\"title\":\"RuleMatch\"}]");
        (await client.GenerateAsync(new LlmRequest { AgentName = "SpecDriftAgent", Prompt = "OVERRIDE_RULE" })).Content.Should().Contain("RuleMatch");
    }
}
