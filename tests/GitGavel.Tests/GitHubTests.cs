using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.GitHub;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.Tests;

public class GitHubTests
{
    private static TriageDecision CreateDecision(string title, string category, string prio, double score, string column, TriageStatus status)
    {
        var finding = new FindingCandidate
        {
            Title = title,
            Summary = "Test summary for " + title,
            Category = category,
            Severity = "High",
            FilePath = "src/Controllers/OrderController.cs",
            StartLine = 15,
            EndLine = 35,
            AgentName = "SecurityAuditor",
            OwaspCategory = "A01: Broken Access Control",
            AttackVector = "Direct object reference manipulation",
            SuggestedRemediation = "Verify user ownership before accessing order."
        };

        return new TriageDecision
        {
            Finding = finding,
            IsDuplicate = false,
            JevScore = score,
            PriorityLabel = prio,
            TargetColumn = column,
            Confidence = 0.94,
            Status = status,
            Reason = "Triage accepted"
        };
    }

    [Fact]
    public void IssueFormatter_FormatsBodyWithMetadataComment()
    {
        var decision = CreateDecision("Insecure Direct Object Reference", "Security", "P1", 4.2, "Backlog", TriageStatus.AcceptedToBacklog);
        var body = IssueFormatter.FormatIssueBody(decision);

        body.Should().Contain("## ⚠️ Summary");
        body.Should().Contain("Insecure Direct Object Reference");
        body.Should().Contain("src/Controllers/OrderController.cs");
        body.Should().Contain("A01: Broken Access Control");
        body.Should().Contain("<!-- gitgavel-metadata");
        body.Should().Contain("category: Security");
        body.Should().Contain("jev_score: 4.2");
        body.Should().Contain("agent: SecurityAuditor");
        body.Should().Contain("-->");
    }

    [Fact]
    public void IssueFormatter_FormatsBodyWithLearningReferences()
    {
        var decision = CreateDecision("Insecure Direct Object Reference", "Security", "P1", 4.2, "Backlog", TriageStatus.AcceptedToBacklog);
        decision.Finding.References.Add("[OWASP Top 10 Broken Access Control](https://owasp.org/Top10/A01_2021-Broken_Access_Control/)");
        decision.Finding.References.Add("[Stack Overflow: ASP.NET Core Resource Authorization](https://stackoverflow.com/questions/tagged/asp.net-core)");

        var body = IssueFormatter.FormatIssueBody(decision);

        body.Should().Contain("## 📚 Learning & Research References");
        body.Should().Contain("- [OWASP Top 10 Broken Access Control](https://owasp.org/Top10/A01_2021-Broken_Access_Control/)");
        body.Should().Contain("- [Stack Overflow: ASP.NET Core Resource Authorization](https://stackoverflow.com/questions/tagged/asp.net-core)");
    }

    [Fact]
    public void IssueFormatter_ParsesMetadataCommentSuccessfully()
    {
        var rawComment = """
        ## Summary
        Some description

        <!-- gitgavel-metadata
        fingerprint: abc123def456
        category: Security
        jev_score: 4.5
        jev_confidence: 0.95
        agent: SecurityAuditor
        created_at: 2026-10-14T10:00:00Z
        -->
        """;

        var meta = IssueFormatter.ParseMetadata(rawComment);

        meta.Should().NotBeNull();
        meta!.Fingerprint.Should().Be("abc123def456");
        meta.Category.Should().Be("Security");
        meta.JevScore.Should().Be(4.5);
        meta.JevConfidence.Should().Be(0.95);
        meta.Agent.Should().Be("SecurityAuditor");
    }

    [Fact]
    public async Task GitHubService_CreatesIssueAndAssignsToProjectsV2()
    {
        var mock = new MockGitHubService();
        var config = new GitGavelConfig();

        var decision = CreateDecision("Fix Auth Bypass", "Security", "P0", 5.0, "Backlog", TriageStatus.AcceptedToBacklog);

        var created = await mock.CreateOrUpdateIssueAsync("my-org", "my-repo", decision, config);
        created.IssueNumber.Should().BeGreaterThan(0);
        created.HtmlUrl.Should().Contain("my-repo/issues/");

        await mock.AssignToProjectV2Async("my-org", "my-repo", created, decision, config);

        mock.ProjectAssignments.Should().HaveCount(1);
        mock.ProjectAssignments[0].Column.Should().Be("Backlog");
    }

    [Fact]
    public void ResolveRepoCoordinates_WithExplicitRepo_ReturnsParsedCoordinates()
    {
        var (owner, repo) = GitHubService.ResolveRepoCoordinates(".", "my-org/my-project");
        owner.Should().Be("my-org");
        repo.Should().Be("my-project");
    }

    [Fact]
    public void ResolveRepoCoordinates_WithoutExplicitOrRemote_ReturnsLocalPlaceholder()
    {
        var prevEnv = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", null);
            var (owner, repo) = GitHubService.ResolveRepoCoordinates(tempDir, null);
            owner.Should().Be("local-owner");
            repo.Should().Be("GitGavel");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", prevEnv);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void ResolveRepoCoordinates_WithEnvVar_ReturnsEnvCoordinates()
    {
        var prevEnv = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", "ci-owner/ci-repo");
            var (owner, repo) = GitHubService.ResolveRepoCoordinates(tempDir, null);
            owner.Should().Be("ci-owner");
            repo.Should().Be("ci-repo");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", prevEnv);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
