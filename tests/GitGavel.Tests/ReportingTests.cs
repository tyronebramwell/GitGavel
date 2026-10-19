using System.Text.Json;
using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.Reporting;
using GitGavel.System1.Models;
using GitGavel.System2;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.Tests;

public class ReportingTests
{
    private static ReportContext CreateSampleContext(string tempOutputDir)
    {
        var config = new GitGavelConfig();
        config.Reporting.OutputDir = tempOutputDir;

        var repoContext = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [new ScannedFile("specs/api.md", "/mock/specs/api.md", 50, 1200, "# Spec", true)],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 100, 3400, "public class App {}", false)],
            SarifFindings = [new SarifFinding("csharp.sql.injection", "SQL injection alert", "error", "src/App.cs", 42, 45)]
        };

        var finding1 = new FindingCandidate
        {
            Title = "Hardcoded Secret Token",
            Summary = "API token is hardcoded in controller.",
            Category = "Security",
            Severity = "Critical",
            FilePath = "src/App.cs",
            StartLine = 20,
            EndLine = 22,
            AgentName = "SecurityAuditor",
            OwaspCategory = "A07: Identification and Authentication Failures",
            AttackVector = "Extract token from binary or source",
            SuggestedRemediation = "Use environment variables or key vault."
        };

        var finding2 = new FindingCandidate
        {
            Title = "Missing Paginated Endpoint",
            Summary = "Endpoint /items lacks pagination described in spec.",
            Category = "SpecDrift",
            Severity = "Medium",
            FilePath = "src/App.cs",
            StartLine = 50,
            EndLine = 60,
            AgentName = "SpecDriftAgent",
            SuggestedRemediation = "Add page and pageSize parameters."
        };

        var orchestration = new OrchestrationResult
        {
            AllFindings = [finding1, finding2],
            SubagentResults =
            [
                new SubagentResult { AgentName = "SecurityAuditor", Findings = [finding1], PromptTokens = 100, CompletionTokens = 50, DurationMs = 120 },
                new SubagentResult { AgentName = "SpecDriftAgent", Findings = [finding2], PromptTokens = 80, CompletionTokens = 40, DurationMs = 90 }
            ],
            TotalDurationMs = 210
        };

        var triageDecisions = new List<TriageDecision>
        {
            new()
            {
                Finding = finding1,
                IsDuplicate = false,
                JevScore = 5.0,
                PriorityLabel = "P0",
                TargetColumn = "Backlog",
                Confidence = 0.98,
                Status = TriageStatus.AcceptedToBacklog,
                Reason = "Critical vulnerability"
            },
            new()
            {
                Finding = finding2,
                IsDuplicate = false,
                JevScore = 3.0,
                PriorityLabel = "P2",
                TargetColumn = "NeedsHumanReview",
                Confidence = 0.65,
                Status = TriageStatus.RoutedToHumanReview,
                Reason = "Spec drift requires review"
            }
        };

        return new ReportContext
        {
            Config = config,
            RepoContext = repoContext,
            Orchestration = orchestration,
            TriageDecisions = triageDecisions,
            GitCommitSha = "c0ffee1",
            GitBranch = "feature/test"
        };
    }

    [Fact]
    public async Task ReportingEngine_GeneratesAllConfiguredReportsAndJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_reports_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var ctx = CreateSampleContext(tempDir);
            var engine = new ReportingEngine();

            var generatedFiles = await engine.GenerateReportsAsync(ctx);

            generatedFiles.Should().HaveCount(5); // 1_audit-run.md, 2_tickets-report.md, 2_tickets-report.json, 3_security-report.md, 4_code-quality-report.md

            var auditRun = await File.ReadAllTextAsync(Path.Combine(tempDir, "1_audit-run.md"));
            auditRun.Should().Contain("c0ffee1");
            auditRun.Should().Contain("feature/test");
            auditRun.Should().Contain("SecurityAuditor");

            var ticketsMd = await File.ReadAllTextAsync(Path.Combine(tempDir, "2_tickets-report.md"));
            ticketsMd.Should().Contain("Hardcoded Secret Token");
            ticketsMd.Should().Contain("P0");

            var ticketsJson = await File.ReadAllTextAsync(Path.Combine(tempDir, "2_tickets-report.json"));
            using var doc = JsonDocument.Parse(ticketsJson);
            doc.RootElement.GetProperty("commitSha").GetString().Should().Be("c0ffee1");
            doc.RootElement.GetProperty("statistics").GetProperty("acceptedToBacklog").GetInt32().Should().Be(1);

            var secMd = await File.ReadAllTextAsync(Path.Combine(tempDir, "3_security-report.md"));
            secMd.Should().Contain("A07: Identification and Authentication Failures");
            secMd.Should().Contain("Attack / Exploit Vector");

            var qualityMd = await File.ReadAllTextAsync(Path.Combine(tempDir, "4_code-quality-report.md"));
            qualityMd.Should().Contain("Missing Paginated Endpoint");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task StepSummaryWriter_AppendsToGitHubStepSummary_WhenEnvSet()
    {
        var tempSummaryFile = Path.GetTempFileName();
        Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", tempSummaryFile);

        try
        {
            var ctx = CreateSampleContext(Path.GetTempPath());
            var engine = new ReportingEngine();

            await engine.GenerateReportsAsync(ctx);

            var summary = await File.ReadAllTextAsync(tempSummaryFile);
            summary.Should().Contain("GitGavel Audit Summary");
            summary.Should().Contain("Top Priority Backlog Tickets");
            summary.Should().Contain("Hardcoded Secret Token");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);
            if (File.Exists(tempSummaryFile)) File.Delete(tempSummaryFile);
        }
    }

    [Fact]
    public async Task ReportingEngine_WithCustomReporter_InvokesCustomReporterSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_custom_reporter_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var ctx = CreateSampleContext(tempDir);
            var customReporter = new TestCustomReporter();
            var engine = new ReportingEngine([customReporter]);

            var generated = await engine.GenerateReportsAsync(ctx);

            generated.Should().ContainSingle();
            generated[0].Should().Be(Path.Combine(tempDir, "custom-report.txt"));
            File.Exists(generated[0]).Should().BeTrue();
            (await File.ReadAllTextAsync(generated[0])).Should().Be("custom content");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private sealed class TestCustomReporter : IReporter
    {
        public string ReportName => "CustomTest";

        public async Task<IEnumerable<string>> GenerateReportsAsync(ReportContext ctx, string outputDirectory, CancellationToken ct = default)
        {
            var file = Path.Combine(outputDirectory, "custom-report.txt");
            await File.WriteAllTextAsync(file, "custom content", ct);
            return [file];
        }
    }
}
