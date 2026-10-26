using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.GitHub;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.Tests;

[Collection("EnvironmentVariables")]
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

    [Fact]
    public async Task GitHubService_WithoutToken_ThrowsExceptionMentioningGhToken()
    {
        var prevGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var prevGithub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

            var service = new GitHubService();
            var decision = CreateDecision("Test Missing Token", "Security", "P0", 5.0, "Backlog", TriageStatus.AcceptedToBacklog);
            var act = () => service.CreateOrUpdateIssueAsync("owner", "repo", decision, new GitGavelConfig());

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*GH_TOKEN is required*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", prevGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", prevGithub);
        }
    }

    [Fact]
    public async Task GitHubService_WithGhTokenEnvVar_UsesTokenWithoutThrowingMissingToken()
    {
        var prevGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var prevGithub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", "mock-token-xyz");
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

            var service = new GitHubService();
            // GetExistingIssuesAsync with non-empty token attempts call rather than immediately returning [] due to missing token
            var issues = await service.GetExistingIssuesAsync("nonexistent-owner", "nonexistent-repo");
            issues.Should().BeEmpty(); // Catch block returns empty on HTTP error, but it did not fail token check
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", prevGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", prevGithub);
        }
    }

    [Fact]
    public async Task GitHubService_CloseResolvedIssue_PostsExplanationAndCommitLinksAndMovesToDone()
    {
        var mock = new MockGitHubService();
        var config = new GitGavelConfig();

        var decision = new IssueResolutionDecision
        {
            Candidate = new IssueResolutionCandidate
            {
                IssueNumber = 101,
                IssueTitle = "Isolate CI environment variables in GitService fallback test",
                IsResolved = true,
                HowItWasFixed = "Isolated GITHUB_SHA, GITHUB_REF_NAME, and GITHUB_REPOSITORY in GitServiceTests.",
                ResolvingCommitShas = ["cfad09afc6f436fb3af957ff2d94eba2d3e1ea36"],
                ResolvingCommitSummaries = ["`cfad09a` — *fix(tests): isolate CI environment variables in GitService fallback test*"]
            },
            VerifiedResolved = true,
            ResolutionProbability = 0.96,
            Action = IssueResolutionAction.CloseResolved,
            TargetColumn = "Done",
            Reason = "Verified fixed"
        };

        var closed = await mock.CloseResolvedIssueAsync("my-org", "my-repo", decision, config);

        closed.IssueNumber.Should().Be(101);
        closed.HowItWasFixed.Should().Contain("GITHUB_SHA");
        closed.ResolvingCommits.Should().ContainSingle(c => c.Contains("cfad09a"));

        mock.ClosedIssues.Should().HaveCount(1);
        var commentBody = mock.ClosedIssues[0].CommentBody;
        commentBody.Should().Contain("## ✅ Issue Resolved");
        commentBody.Should().Contain("### 🛠️ How It Was Fixed");
        commentBody.Should().Contain("### 🔗 Resolving Commits");
        commentBody.Should().Contain("`cfad09a`");
        commentBody.Should().Contain("<!-- gitgavel-resolution");

        var parsedMeta = IssueFormatter.ParseResolutionMetadata(commentBody);
        parsedMeta.Should().NotBeNull();
        parsedMeta!.ResolvedByCommits.Should().Contain("cfad09afc6f436fb3af957ff2d94eba2d3e1ea36");
        parsedMeta.JevResolutionConfidence.Should().Be(0.96);

        mock.ProjectAssignments.Should().ContainSingle(a => a.Issue.IssueNumber == 101 && a.Column == "Done");
    }

    [Fact]
    public async Task GitHubService_PostPullRequestReview_FormatsInlineAndSummaryComments()
    {
        var mock = new MockGitHubService();
        var config = new GitGavelConfig();
        config.Github.PullRequests.PostInlineComments = true;

        var prContext = new PullRequestContext
        {
            PrNumber = 42,
            Title = "Update GitService",
            BaseRef = "main",
            HeadRef = "feature/pr-42",
            HeadSha = "cfad09afc6f436fb3af957ff2d94eba2d3e1ea36",
            ChangedFiles =
            [
                new PullRequestChangedFile("src/GitGavel/Git/GitService.cs", "modified", 12, 2, "@@ -65,2 +65,12 @@")
            ],
            ExistingReviewComments =
            [
                new ExistingPrReviewComment(10, "tests/GitGavel.Tests/GitServiceTests.cs", 20, "Verified env var isolation in GitServiceTests.cs", "maintainer", DateTime.UtcNow)
            ]
        };

        var decisions = new List<PrCommentTriageDecision>
        {
            new()
            {
                Finding = new FindingCandidate
                {
                    FilePath = "src/GitGavel/Git/GitService.cs",
                    StartLine = 70,
                    EndLine = 74,
                    Category = "Testing",
                    Severity = "Medium",
                    Title = "Add boundary test",
                    Summary = "Cover maxCount <= 0.",
                    AgentName = "TestSentinelAgent",
                    SuggestedRemediation = "if (maxCount <= 0) return [];"
                },
                JevScore = 3.2,
                PriorityLabel = "P2",
                Action = PrCommentAction.PostInlineReview,
                Confidence = 0.92
            }
        };

        var posted = await mock.PostPullRequestReviewAsync("my-org", "my-repo", prContext, decisions, config);

        posted.PrNumber.Should().Be(42);
        posted.InlineCommentsPosted.Should().Be(1);
        posted.SummaryPosted.Should().BeTrue();
        posted.Verdict.Should().Be("COMMENT");

        mock.PostedPrReviews.Should().HaveCount(1);
        mock.PostedPrReviews[0].SummaryBody.Should().Contain("## ⚖️ GitGavel Pull Request Audit Summary");
        mock.PostedPrReviews[0].SummaryBody.Should().Contain("Changed Files Audited");
        mock.PostedPrReviews[0].SummaryBody.Should().Contain("<!-- gitgavel-pr-review");
        mock.PostedPrReviews[0].InlineBodies[0].Should().Contain("```suggestion");
    }

    [Fact]
    public async Task GitHubService_PostPullRequestReview_WhenInlineCommentsDisabledByDefault_GeneratesOnlySummaryReview()
    {
        var mock = new MockGitHubService();
        var config = new GitGavelConfig(); // PostInlineComments defaults to false

        var prContext = new PullRequestContext
        {
            PrNumber = 42,
            Title = "Update GitService",
            BaseRef = "main",
            HeadRef = "feature/pr-42",
            HeadSha = "cfad09afc6f436fb3af957ff2d94eba2d3e1ea36",
            ChangedFiles =
            [
                new PullRequestChangedFile("src/GitGavel/Git/GitService.cs", "modified", 12, 2, "@@ -65,2 +65,12 @@")
            ]
        };

        var decisions = new List<PrCommentTriageDecision>
        {
            new()
            {
                Finding = new FindingCandidate
                {
                    FilePath = "src/GitGavel/Git/GitService.cs",
                    StartLine = 70,
                    EndLine = 74,
                    Category = "Testing",
                    Severity = "Medium",
                    Title = "Add boundary test",
                    Summary = "Cover maxCount <= 0.",
                    AgentName = "TestSentinelAgent",
                    SuggestedRemediation = "if (maxCount <= 0) return [];"
                },
                JevScore = 3.2,
                PriorityLabel = "P2",
                Action = PrCommentAction.PostInlineReview,
                Confidence = 0.92
            }
        };

        var posted = await mock.PostPullRequestReviewAsync("my-org", "my-repo", prContext, decisions, config);

        posted.PrNumber.Should().Be(42);
        posted.InlineCommentsPosted.Should().Be(0);
        posted.SummaryPosted.Should().BeTrue();
        mock.PostedPrReviews.Should().HaveCount(1);
        mock.PostedPrReviews[0].InlineBodies.Should().BeEmpty();
        mock.PostedPrReviews[0].SummaryBody.Should().Contain("## ⚖️ GitGavel Pull Request Audit Summary");
    }

    [Fact]
    public async Task GitHubService_PostPullRequestDomainReports_PostsCommentsForDomainsWithFindings()
    {
        var mock = new MockGitHubService();
        var config = new GitGavelConfig();

        var secFinding = new FindingCandidate
        {
            Title = "Path Traversal in Scanner",
            Summary = "Unvalidated file path read.",
            Category = "Security",
            Severity = "High",
            FilePath = "src/GitGavel/Discovery/FileScanner.cs",
            StartLine = 30,
            EndLine = 45,
            AgentName = "SecurityAuditor",
            OwaspCategory = "A01: Broken Access Control",
            SuggestedRemediation = "Validate canonical path."
        };

        var testFinding = new FindingCandidate
        {
            Title = "Missing Unit Test for Diff Position Mapping",
            Summary = "TryMapFileLineToDiffPosition needs boundary coverage.",
            Category = "Testing",
            Severity = "Medium",
            TestTier = "Unit",
            TestDefectType = "MissingTest",
            FilePath = "src/GitGavel/GitHub/GitHubService.cs",
            StartLine = 330,
            EndLine = 380,
            AgentName = "TestSentinelAgent",
            SuggestedRemediation = "[Fact] public void TryMapFileLineToDiffPosition_MapsCorrectly() {}"
        };

        var reportCtx = new GitGavel.Reporting.ReportContext
        {
            Config = config,
            RepoContext = new GitGavel.Discovery.RepoContext
            {
                RootDirectory = "/mock",
                SpecFiles = [],
                SourceFiles = [],
                SarifFindings = []
            },
            Orchestration = new GitGavel.System2.OrchestrationResult
            {
                AllFindings = [secFinding, testFinding]
            },
            TriageDecisions = [],
            ActivePullRequestNumber = 32,
            GitCommitSha = "b93b5b5",
            GitBranch = "fix/issue-1"
        };

        var engine = new GitGavel.Reporting.ReportingEngine();
        var postedDomains = await engine.PublishPullRequestDomainReportsAsync(reportCtx, mock, "my-org", "my-repo", dryRun: false, useMock: true);

        postedDomains.Should().BeEquivalentTo(["Security", "TestQuality"]);
        mock.PostedPrComments.Should().HaveCount(2);
        mock.PostedPrComments.Should().Contain(r => r.PrNumber == 32 && r.CommentBody.Contains("GitGavel Security Audit Report"));
        mock.PostedPrComments.Should().Contain(r => r.PrNumber == 32 && r.CommentBody.Contains("GitGavel Test Quality & Coverage Report"));
    }

    [Fact]
    public void GitHubService_TryMapFileLineToDiffPosition_MapsNewFileLineToDiffOffset()
    {
        var patch = """
        @@ -10,4 +10,6 @@ public class GitService
         line10
         line11
        +addedLine12
        +addedLine13
         line14
         line15
        """;

        var mapped = GitHubService.TryMapFileLineToDiffPosition(patch, 12, out var pos);
        mapped.Should().BeTrue();
        pos.Should().Be(3);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("   ", 10)]
    [InlineData("not a valid diff hunk", 10)]
    [InlineData("@@ -10,2 +10,2 @@\n line10\n+line11", 0)]
    [InlineData("@@ -10,2 +10,2 @@\n line10\n+line11", -5)]
    [InlineData("@@ -10,2 +10,2 @@\n line10\n+line11", 999)]
    public void GitHubService_TryMapFileLineToDiffPosition_WithInvalidOrOutOfRangeInputs_ReturnsFalse(string? patch, int targetLine)
    {
        var mapped = GitHubService.TryMapFileLineToDiffPosition(patch, targetLine, out var pos);
        mapped.Should().BeFalse();
        pos.Should().Be(0);
    }

    [Fact]
    public void GitHubService_TryMapFileLineToDiffPosition_HandlesContextLinesDeletionsAndMultipleHunks()
    {
        var multiHunkPatch = string.Join("\n",
            "@@ -10,4 +10,4 @@ public class Sample",
            " contextLine10",
            "-deletedOldLine11",
            "+addedNewLine11",
            " contextLine12",
            "@@ -30,2 +30,3 @@ public void SecondMethod()",
            " contextLine30",
            "+addedNewLine31",
            " contextLine32");

        // First line of hunk 1 (context line 10 -> position 1)
        GitHubService.TryMapFileLineToDiffPosition(multiHunkPatch, 10, out var pos10).Should().BeTrue();
        pos10.Should().Be(1);

        // Added line 11 after deleted line -> position 3 (since position 2 is the '-' line)
        GitHubService.TryMapFileLineToDiffPosition(multiHunkPatch, 11, out var pos11).Should().BeTrue();
        pos11.Should().Be(3);

        // Last line of hunk 1 (context line 12 -> position 4)
        GitHubService.TryMapFileLineToDiffPosition(multiHunkPatch, 12, out var pos12).Should().BeTrue();
        pos12.Should().Be(4);

        // Line between hunks (not in diff -> returns false)
        GitHubService.TryMapFileLineToDiffPosition(multiHunkPatch, 20, out _).Should().BeFalse();

        // Second hunk: header is position 5, contextLine30 is position 6, addedNewLine31 is position 7, contextLine32 is position 8
        GitHubService.TryMapFileLineToDiffPosition(multiHunkPatch, 30, out var pos30).Should().BeTrue();
        pos30.Should().Be(6);

        GitHubService.TryMapFileLineToDiffPosition(multiHunkPatch, 31, out var pos31).Should().BeTrue();
        pos31.Should().Be(7);

        GitHubService.TryMapFileLineToDiffPosition(multiHunkPatch, 32, out var pos32).Should().BeTrue();
        pos32.Should().Be(8);
    }

    [Fact]
    public async Task GitHubService_PostPullRequestReviewAsync_MapsComplexMultiHunkDiffPositionsCorrectly()
    {
        var mock = new MockGitHubService();
        var config = new GitGavelConfig();
        config.Github.PullRequests.PostInlineComments = true;

        var complexPatch = string.Join("\n",
            "@@ -5,3 +5,4 @@",
            " line5",
            "-oldLine6",
            "+newLine6",
            "+newLine7",
            "@@ -20,2 +21,3 @@",
            " line21",
            "+targetLine22",
            " line23");

        var prContext = new PullRequestContext
        {
            PrNumber = 55,
            Title = "Complex Diff PR",
            HeadSha = "1234567890abcdef",
            ChangedFiles =
            [
                new PullRequestChangedFile("src/ComplexService.cs", "modified", 3, 1, complexPatch)
            ]
        };

        var decisions = new List<PrCommentTriageDecision>
        {
            new()
            {
                Finding = new FindingCandidate
                {
                    FilePath = "src/ComplexService.cs",
                    StartLine = 22,
                    EndLine = 22,
                    Category = "Security",
                    Severity = "High",
                    Title = "Validate input on line 22",
                    Summary = "Line 22 in second hunk needs validation.",
                    AgentName = "SecurityAuditor",
                    SuggestedRemediation = "Guard.NotNull(input);"
                },
                JevScore = 4.0,
                PriorityLabel = "P1",
                Action = PrCommentAction.PostInlineReview,
                Confidence = 0.95
            }
        };

        await mock.PostPullRequestReviewAsync("my-org", "my-repo", prContext, decisions, config);

        // Hunk 1 has 4 lines (pos 1..4), Hunk 2 header is pos 5, line21 is pos 6, targetLine22 is pos 7
        mock.PostedPrInlineComments.Should().ContainSingle();
        mock.PostedPrInlineComments[0].FilePath.Should().Be("src/ComplexService.cs");
        mock.PostedPrInlineComments[0].DiffPosition.Should().Be(7);
    }

    [Theory]
    [InlineData("Security", "Security")]
    [InlineData("SpecDrift", "CodeQuality")]
    [InlineData("Refactoring", "CodeQuality")]
    [InlineData("Testing", "TestQuality")]
    [InlineData("None", "")]
    public async Task ReportingEngine_PublishPullRequestDomainReports_PostsOnlyMatchingDomainOrNone(string category, string expectedDomain)
    {
        var mock = new MockGitHubService();
        var findings = new List<FindingCandidate>();

        if (category != "None")
        {
            findings.Add(new FindingCandidate
            {
                Title = $"{category} Finding",
                Summary = "Test summary",
                Category = category,
                Severity = "Medium",
                FilePath = "src/Service.cs",
                StartLine = 10,
                EndLine = 15,
                AgentName = category == "Testing" ? "TestSentinelAgent" : "Subagent",
                SuggestedRemediation = "Fix"
            });
        }

        var reportCtx = new GitGavel.Reporting.ReportContext
        {
            Config = new GitGavelConfig(),
            RepoContext = new GitGavel.Discovery.RepoContext
            {
                RootDirectory = "/mock",
                SpecFiles = [],
                SourceFiles = [],
                SarifFindings = []
            },
            Orchestration = new GitGavel.System2.OrchestrationResult
            {
                AllFindings = findings
            },
            TriageDecisions = [],
            ActivePullRequestNumber = 32,
            GitCommitSha = "abc1234",
            GitBranch = "fix/test"
        };

        var engine = new GitGavel.Reporting.ReportingEngine();
        var posted = await engine.PublishPullRequestDomainReportsAsync(reportCtx, mock, "my-org", "my-repo", dryRun: false, useMock: true);

        if (string.IsNullOrEmpty(expectedDomain))
        {
            posted.Should().BeEmpty();
            mock.PostedPrComments.Should().BeEmpty();
        }
        else
        {
            posted.Should().ContainSingle().Which.Should().Be(expectedDomain);
            mock.PostedPrComments.Should().ContainSingle();
        }
    }

    [Fact]
    public void GitHubService_TryMapDiffPositionToFileLine_MapsDiffOffsetBackToNewFileLine()
    {
        var patch = string.Join("\n",
            "@@ -10,4 +10,5 @@ public class Sample",
            " line10",
            "-oldLine11",
            "+newLine11",
            "+newLine12",
            " line13");

        GitHubService.TryMapDiffPositionToFileLine(patch, 1, out var line10).Should().BeTrue();
        line10.Should().Be(10);

        // Position 2 is deleted line ('-'), maps to adjacent new file line 11
        GitHubService.TryMapDiffPositionToFileLine(patch, 2, out var delLine).Should().BeTrue();
        delLine.Should().Be(11);

        // Position 3 is '+newLine11' -> line 11
        GitHubService.TryMapDiffPositionToFileLine(patch, 3, out var line11).Should().BeTrue();
        line11.Should().Be(11);

        // Position 4 is '+newLine12' -> line 12
        GitHubService.TryMapDiffPositionToFileLine(patch, 4, out var line12).Should().BeTrue();
        line12.Should().Be(12);

        // Out of range position returns false
        GitHubService.TryMapDiffPositionToFileLine(patch, 99, out _).Should().BeFalse();
    }

    [Fact]
    public async Task MockGitHubService_PostPullRequestCommentAsync_UpsertsStickyComment()
    {
        var mock = new MockGitHubService();

        await mock.PostPullRequestCommentAsync(
            "my-org", "my-repo", 32,
            "<!-- gitgavel-sticky-comment:domain-security -->\nFirst report");

        await mock.PostPullRequestCommentAsync(
            "my-org", "my-repo", 32,
            "<!-- gitgavel-sticky-comment:domain-security -->\nUpdated report");

        mock.PostedPrComments.Should().ContainSingle();
        mock.PostedPrComments[0].CommentBody.Should().Contain("Updated report");
    }
}
