using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.Git;
using GitGavel.GitHub;
using GitGavel.Reporting;
using GitGavel.Reporting.Reporters;
using GitGavel.System1.Models;
using GitGavel.System2;
using GitGavel.System2.Models;
using Moq;

namespace GitGavel.Tests;

public class ModelsConfigAndServiceCoverageTests
{
    private sealed class DictEnvironmentProvider(Dictionary<string, string?> vars) : IEnvironmentProvider
    {
        public string? GetEnvironmentVariable(string variable) => vars.TryGetValue(variable, out var v) ? v : null;
        public void SetEnvironmentVariable(string variable, string? value) => vars[variable] = value;
    }

    [Fact]
    public async Task GitHubService_ErrorPathsMissingTokenAndDispose_BehaveExpectedly()
    {
        using var emptyTokenService = new GitHubService(token: "");
        (await emptyTokenService.GetExistingIssuesAsync("org", "repo")).Should().BeEmpty();

        var decision = new TriageDecision
        {
            Finding = new FindingCandidate
            {
                Title = "T",
                Summary = "S",
                Category = "Security",
                Severity = "High",
                FilePath = "src/App.cs",
                AgentName = "SecurityAuditor",
                SuggestedRemediation = "Fix"
            }
        };
        var createAct = () => emptyTokenService.CreateOrUpdateIssueAsync("org", "repo", decision, new GitGavelConfig());
        await createAct.Should().ThrowAsync<InvalidOperationException>();

        var resDecision = new IssueResolutionDecision
        {
            Candidate = new IssueResolutionCandidate
            {
                IssueNumber = 1,
                IssueTitle = "T",
                IsResolved = true,
                HowItWasFixed = "Fixed"
            }
        };
        var closeAct = () => emptyTokenService.CloseResolvedIssueAsync("org", "repo", resDecision, new GitGavelConfig());
        await closeAct.Should().ThrowAsync<InvalidOperationException>();

        // Verify GetExistingIssuesAsync extracts Fingerprint from issue body and handles ApiException
        var mockClient = new Mock<Octokit.IGitHubClient>();
        var mockIssues = new Mock<Octokit.IIssuesClient>();
        mockClient.SetupGet(c => c.Issue).Returns(mockIssues.Object);
        mockIssues
            .Setup(i => i.GetAllForRepository("org", "repo", It.IsAny<Octokit.RepositoryIssueRequest>(), It.IsAny<Octokit.ApiOptions>()))
            .ThrowsAsync(new Octokit.ApiException("API failed", System.Net.HttpStatusCode.BadGateway));

        string? warning = null;
        var svc = new GitHubService("ghp_tok", githubClient: mockClient.Object, onWarning: w => warning = w);
        (await svc.GetExistingIssuesAsync("org", "repo")).Should().BeEmpty();
        warning.Should().Contain("API failed");
        svc.Dispose();
        svc.Dispose();
    }

    [Fact]
    public void ExistingIssue_GetEffectiveFingerprint_HandlesExplicitEmbeddedAndMissingFingerprints()
    {
        new ExistingIssue(1, "T", "body", Fingerprint: "explicit-fp")
            .GetEffectiveFingerprint().Should().Be("explicit-fp");

        var bodyWithComment = "Some text\n<!-- gitgavel-metadata\nfingerprint: embedded-fp-123\njev_score: 4.1\n-->";
        new ExistingIssue(2, "T", bodyWithComment)
            .GetEffectiveFingerprint().Should().Be("embedded-fp-123");

        new ExistingIssue(3, "T", "Plain body without metadata")
            .GetEffectiveFingerprint().Should().BeNull();

        new ExistingIssue(4, "T", "")
            .GetEffectiveFingerprint().Should().BeNull();
    }

    [Fact]
    public void GatingSignals_FromRepoContext_AggregatesPullRequestAndFullRepoSignals()
    {
        var prCtx = new PullRequestContext
        {
            PrNumber = 12,
            Title = "Security & Spec PR",
            Body = new string('x', 600),
            ChangedFiles = [new PullRequestChangedFile("src/Auth/Login.cs", "modified", 10, 2, "@@ -1,1 +1,1 @@")]
        };

        var repoCtx = new RepoContext
        {
            RootDirectory = "/repo",
            SpecFiles = [new ScannedFile("SPEC.md", "/repo/SPEC.md", 10, 100, "# Spec", true, false, "@@ -1,1 +1,1 @@")],
            SourceFiles = [new ScannedFile("src/Auth/Login.cs", "/repo/src/Auth/Login.cs", 20, 200, "class Login {}", false, false, "@@ -1,1 +1,1 @@")],
            TestFiles = [new ScannedFile("tests/LoginTests.cs", "/repo/tests/LoginTests.cs", 15, 150, "class LoginTests {}", false, true, "@@ -1,1 +1,1 @@")],
            SarifFindings = [new SarifFinding("SEC01", "Issue", "error", "src/Auth/Login.cs", 5, 5)],
            AstSummaries = [new FileAstSummary("src/Auth/Login.cs", [], 30, true)],
            ActivePullRequest = prCtx
        };

        var prSignals = GatingSignals.FromRepoContext(repoCtx);
        prSignals.IsPullRequestMode.Should().BeTrue();
        prSignals.PrTitle.Should().Be("Security & Spec PR");
        prSignals.PrDescription.Should().HaveLength(500);
        prSignals.HasSpecChanges.Should().BeTrue();
        prSignals.HasSourceCodeChanges.Should().BeTrue();
        prSignals.HasTestChanges.Should().BeTrue();
        prSignals.HasSarifAlerts.Should().BeTrue();
        prSignals.HasComplexityHotspots.Should().BeTrue();
        prSignals.MaxCyclomaticComplexity.Should().Be(30);
        prSignals.HasSecuritySensitivePaths.Should().BeTrue();

        var fullSignals = GatingSignals.FromRepoContext(repoCtx with { ActivePullRequest = null });
        fullSignals.IsPullRequestMode.Should().BeFalse();
        fullSignals.HasSpecChanges.Should().BeTrue();
        fullSignals.HasSourceCodeChanges.Should().BeTrue();
        fullSignals.HasTestChanges.Should().BeTrue();
    }

    [Fact]
    public void KeyResolver_Resolve_WithEnvironmentProvider_ResolvesPrimaryThenFallbacks()
    {
        var env = new DictEnvironmentProvider(new Dictionary<string, string?>
        {
            ["FALLBACK_2"] = "secret-from-fb2"
        });

        KeyResolver.Resolve(env, "PRIMARY_MISSING", "FALLBACK_1", "FALLBACK_2").Should().Be("secret-from-fb2");
        KeyResolver.Resolve(env, null, "", "   ").Should().BeNull();

        env.SetEnvironmentVariable("PRIMARY_KEY", "primary-secret");
        KeyResolver.Resolve(env, "PRIMARY_KEY", "FALLBACK_2").Should().Be("primary-secret");
    }

    [Fact]
    public void DotEnvLoader_Load_HandlesExportPrefixMalformedLinesAndWarningCallback()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dotenv_cov_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, ".env"), "export API_TOKEN=\"tok_123\"\nMALFORMED_LINE\n");
            var store = new Dictionary<string, string?>();
            var env = new DictEnvironmentProvider(store);
            string? warning = null;

            DotEnvLoader.Load(tempDir, allowExternalPaths: false, environment: env, onWarning: w => warning = w);
            store["API_TOKEN"].Should().Be("tok_123");

            // Symlink .env pointing outside tempDir with allowExternalPaths: false triggers SecurityException -> invokes onWarning
            var externalFile = Path.Combine(Path.GetTempPath(), "ext_env_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(externalFile, "EXT_KEY=1");
            try
            {
                File.Delete(Path.Combine(tempDir, ".env"));
                File.CreateSymbolicLink(Path.Combine(tempDir, ".env"), externalFile);
                DotEnvLoader.Load(tempDir, allowExternalPaths: false, environment: env, onWarning: w => warning = w);
                warning.Should().Contain("Unable to load .env file");
            }
            finally
            {
                if (File.Exists(externalFile)) File.Delete(externalFile);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task StepSummaryWriter_WriteAsync_UsesEnvironmentProviderAndInvokesOnWarningOnFailure()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "summary_" + Guid.NewGuid().ToString("N") + ".md");
        var tempDirAsFile = Path.Combine(Path.GetTempPath(), "summary_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirAsFile);

        try
        {
            var reportCtx = new ReportContext
            {
                Config = new GitGavelConfig(),
                RepoContext = new RepoContext { RootDirectory = "/repo", SpecFiles = [], SourceFiles = [], SarifFindings = [] },
                Orchestration = new OrchestrationResult(),
                TriageDecisions = []
            };

            var envOk = new DictEnvironmentProvider(new Dictionary<string, string?> { ["GITHUB_STEP_SUMMARY"] = tempFile });
            await StepSummaryWriter.WriteAsync(reportCtx, null, envOk);
            File.ReadAllText(tempFile).Should().Contain("GitGavel Audit Summary");

            string? warning = null;
            var envFail = new DictEnvironmentProvider(new Dictionary<string, string?> { ["GITHUB_STEP_SUMMARY"] = tempDirAsFile });
            await StepSummaryWriter.WriteAsync(reportCtx, null, envFail, onWarning: w => warning = w);
            warning.Should().Contain("Failed to write GitHub step summary");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (Directory.Exists(tempDirAsFile)) Directory.Delete(tempDirAsFile, true);
        }
    }

    [Fact]
    public void GitService_DetectActivePullRequestNumber_WhenEventPayloadExceedsMaxSize_LogsWarningAndReturnsNull()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "large_event_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(10 * 1024 * 1024 + 1024); // > 10 MB
            }

            string? warning = null;
            var env = new DictEnvironmentProvider(new Dictionary<string, string?> { ["GITHUB_EVENT_PATH"] = tempFile });
            var gitService = new GitService(environment: env, onWarning: w => warning = w);

            gitService.ResolvePullRequestNumber().Should().BeNull();
            warning.Should().Contain("exceeds maximum limit");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
