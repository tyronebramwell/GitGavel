using FluentAssertions;
using GitGavel.Configuration;

namespace GitGavel.Tests;

public class ConfigurationTests
{
    [Fact]
    public void LoadFromString_WithDefaultContent_ReturnsValidConfig()
    {
        var yaml = """
        version: 1
        triage:
          engine: "jev"
          maxIssuesPerRun: 5
          jev:
            thresholds:
              autoBacklogConfidence: 0.80
              humanReviewConfidence: 0.40
        """;

        var config = ConfigLoader.LoadFromString(yaml);

        config.Should().NotBeNull();
        config.Version.Should().Be(1);
        config.Triage.Engine.Should().Be("jev");
        config.Triage.MaxIssuesPerRun.Should().Be(5);
        config.Triage.Jev.Thresholds.AutoBacklogConfidence.Should().Be(0.80);
        config.Triage.Jev.Thresholds.HumanReviewConfidence.Should().Be(0.40);
        config.Subagents.SpecDrift.Enabled.Should().BeTrue();
    }

    [Fact]
    public void LoadFromString_WithInvalidThresholds_ThrowsInvalidOperationException()
    {
        var invalidYaml = """
        version: 1
        triage:
          jev:
            thresholds:
              autoBacklogConfidence: 0.40
              humanReviewConfidence: 0.80 # HumanReview higher than AutoBacklog
        """;

        var act = () => ConfigLoader.LoadFromString(invalidYaml);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot be greater than*");
    }

    [Fact]
    public void LoadFromString_WithZeroMaxIssues_ThrowsInvalidOperationException()
    {
        var invalidYaml = """
        version: 1
        triage:
          maxIssuesPerRun: 0
        """;

        var act = () => ConfigLoader.LoadFromString(invalidYaml);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be greater than 0*");
    }

    [Fact]
    public void LoadFromString_WithEmptyString_ReturnsDefaults()
    {
        var config = ConfigLoader.LoadFromString("");

        config.Should().NotBeNull();
        config.Triage.MaxIssuesPerRun.Should().Be(4);
        config.Reporting.OutputDir.Should().Be("./reports");
        config.Subagents.Tests.Enabled.Should().BeTrue();
        config.Github.PullRequests.Enabled.Should().BeTrue();
        config.Github.PullRequests.OnlyChangedFiles.Should().BeTrue();
        config.Subagents.IssueResolution.Enabled.Should().BeTrue();
        config.Triage.Jev.Thresholds.AutoCloseIssueConfidence.Should().Be(0.90);
        config.Reporting.Reports.TestQuality.Should().BeTrue();
        config.Github.ProjectsV2.Columns.Done.Should().Be("Done");
        config.Subagents.Reliability.Should().NotBeNull();
        config.Subagents.Reliability.Enabled.Should().BeTrue();
        config.Subagents.Reliability.Model.Should().Be("gemini-3.1-pro-preview");
        config.Github.PullRequests.PostInlineComments.Should().BeFalse();
        config.Github.PullRequests.PostSummaryComment.Should().BeTrue();
        config.Triage.SubagentGating.Should().NotBeNull();
        config.Triage.SubagentGating.Enabled.Should().BeTrue();
        config.Triage.SubagentGating.MinConfidence.Should().Be(0.50);
        config.Triage.SubagentGating.AlwaysRunSecurityOnSarifAlerts.Should().BeTrue();
        config.Triage.SubagentGating.AlwaysRunSpecDriftOnSpecChanges.Should().BeTrue();
        config.Triage.SubagentGating.BypassInFullRepoMode.Should().BeFalse();
    }

    [Fact]
    public void LoadFromString_WithSubagentGatingSettings_ParsesCorrectly()
    {
        var yaml = """
        version: 1
        triage:
          subagentGating:
            enabled: false
            minConfidence: 0.75
            alwaysRunSecurityOnSarifAlerts: false
            alwaysRunSpecDriftOnSpecChanges: false
            bypassInFullRepoMode: true
        """;

        var config = ConfigLoader.LoadFromString(yaml);

        config.Triage.SubagentGating.Enabled.Should().BeFalse();
        config.Triage.SubagentGating.MinConfidence.Should().Be(0.75);
        config.Triage.SubagentGating.AlwaysRunSecurityOnSarifAlerts.Should().BeFalse();
        config.Triage.SubagentGating.AlwaysRunSpecDriftOnSpecChanges.Should().BeFalse();
        config.Triage.SubagentGating.BypassInFullRepoMode.Should().BeTrue();
    }

    [Fact]
    public void LoadFromString_WithReliabilitySettings_ParsesCorrectly()
    {
        var yaml = """
        version: 1
        subagents:
          reliability:
            enabled: true
            provider: "gemini"
            model: "gemini-3.1-pro-preview"
        """;

        var config = ConfigLoader.LoadFromString(yaml);
        config.Subagents.Reliability.Enabled.Should().BeTrue();
        config.Subagents.Reliability.Model.Should().Be("gemini-3.1-pro-preview");
    }

    [Fact]
    public void LoadFromString_WithPrReviewAndIssueResolutionSettings_ParsesCorrectly()
    {
        var yaml = """
        version: 1
        subagents:
          tests:
            enabled: true
            tiers: ["unit", "e2e"]
          issueResolution:
            enabled: true
            autoCloseCompleted: true
            commitLookbackCount: 25
        triage:
          jev:
            thresholds:
              autoCloseIssueConfidence: 0.92
        github:
          pullRequests:
            enabled: true
            onlyChangedFiles: true
            auditExistingReviews: true
            maxInlineCommentsPerPr: 5
            defaultReviewEvent: "REQUEST_CHANGES"
          issueClosure:
            enabled: true
            stateReason: "completed"
          projectsV2:
            columns:
              done: "Completed"
        """;

        var config = ConfigLoader.LoadFromString(yaml);

        config.Subagents.Tests.Tiers.Should().BeEquivalentTo(["unit", "e2e"]);
        config.Github.PullRequests.MaxInlineCommentsPerPr.Should().Be(5);
        config.Github.PullRequests.OnlyChangedFiles.Should().BeTrue();
        config.Subagents.IssueResolution.CommitLookbackCount.Should().Be(25);
        config.Triage.Jev.Thresholds.AutoCloseIssueConfidence.Should().Be(0.92);
        config.Github.PullRequests.DefaultReviewEvent.Should().Be("REQUEST_CHANGES");
        config.Github.ProjectsV2.Columns.Done.Should().Be("Completed");
    }

    [Fact]
    public void LoadFromString_DefaultTimeout_Is300Seconds()
    {
        var config = ConfigLoader.LoadFromString("");
        config.Subagents.TimeoutSeconds.Should().Be(300);
        config.Subagents.Tests.TimeoutSeconds.Should().BeNull();
    }

    [Fact]
    public void LoadFromString_WithCustomGlobalAndSubagentTimeout_ParsesCorrectly()
    {
        var yaml = """
        version: 1
        subagents:
          timeoutSeconds: 450
          tests:
            enabled: true
            timeoutSeconds: 600
        """;

        var config = ConfigLoader.LoadFromString(yaml);
        config.Subagents.TimeoutSeconds.Should().Be(450);
        config.Subagents.Tests.TimeoutSeconds.Should().Be(600);
        config.Subagents.Security.TimeoutSeconds.Should().BeNull();
    }

    [Fact]
    public void DefaultConfig_SpecsInclude_ContainsReadmeMd()
    {
        var config = new GitGavelConfig();
        config.Scope.Specs.Include.Should().Contain("README.md");
        config.Scope.Specs.Include.Should().Contain("SPECIFICATION.md");
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, 0.0)]
    [InlineData(1.0, 1.0, 1.0, 1.0)]
    [InlineData(0.85, 0.50, 0.90, 0.50)]
    public void Validate_WithValidBoundaryConfidenceValues_DoesNotThrow(
        double autoBacklog,
        double humanReview,
        double autoClose,
        double gatingMin)
    {
        var config = new GitGavelConfig();
        config.Triage.Jev.Thresholds.AutoBacklogConfidence = autoBacklog;
        config.Triage.Jev.Thresholds.HumanReviewConfidence = humanReview;
        config.Triage.Jev.Thresholds.AutoCloseIssueConfidence = autoClose;
        config.Triage.SubagentGating.MinConfidence = gatingMin;

        var act = () => ConfigLoader.Validate(config);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(-0.01, 0.50, 0.90, 0.50)]
    [InlineData(1.01, 0.50, 0.90, 0.50)]
    [InlineData(0.85, -0.01, 0.90, 0.50)]
    [InlineData(0.85, 1.01, 0.90, 0.50)]
    [InlineData(0.85, 0.50, -0.1, 0.50)]
    [InlineData(0.85, 0.50, 1.1, 0.50)]
    [InlineData(0.85, 0.50, 0.90, -0.05)]
    [InlineData(0.85, 0.50, 0.90, 1.05)]
    public void Validate_WithOutOfRangeConfidenceValues_ThrowsInvalidOperationException(
        double autoBacklog,
        double humanReview,
        double autoClose,
        double gatingMin)
    {
        var config = new GitGavelConfig();
        config.Triage.Jev.Thresholds.AutoBacklogConfidence = autoBacklog;
        config.Triage.Jev.Thresholds.HumanReviewConfidence = humanReview;
        config.Triage.Jev.Thresholds.AutoCloseIssueConfidence = autoClose;
        config.Triage.SubagentGating.MinConfidence = gatingMin;

        var act = () => ConfigLoader.Validate(config);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_WithInvalidTimeoutsProviderOrOutputDir_ThrowsInvalidOperationException()
    {
        var cfg1 = new GitGavelConfig();
        cfg1.Subagents.TimeoutSeconds = 0;
        Action act1 = () => ConfigLoader.Validate(cfg1);
        act1.Should().Throw<InvalidOperationException>().WithMessage("*timeoutSeconds*");

        var cfg2 = new GitGavelConfig();
        cfg2.Subagents.Security.TimeoutSeconds = -10;
        Action act2 = () => ConfigLoader.Validate(cfg2);
        act2.Should().Throw<InvalidOperationException>().WithMessage("*timeoutSeconds*");

        var cfg3 = new GitGavelConfig();
        cfg3.Subagents.SpecDrift.Provider = "unsupported-provider";
        Action act3 = () => ConfigLoader.Validate(cfg3);
        act3.Should().Throw<InvalidOperationException>().WithMessage("*provider*");

        var cfg4 = new GitGavelConfig();
        cfg4.Subagents.IssueResolution.CommitLookbackCount = 0;
        Action act4 = () => ConfigLoader.Validate(cfg4);
        act4.Should().Throw<InvalidOperationException>().WithMessage("*commitLookbackCount*");

        var cfg5 = new GitGavelConfig();
        cfg5.Reporting.OutputDir = "   ";
        Action act5 = () => ConfigLoader.Validate(cfg5);
        act5.Should().Throw<InvalidOperationException>().WithMessage("*outputDir*");

        var cfg6 = new GitGavelConfig();
        cfg6.Scope.MaxFileSizeBytes = 0;
        Action act6 = () => ConfigLoader.Validate(cfg6);
        act6.Should().Throw<InvalidOperationException>().WithMessage("*maxFileSizeBytes*");
    }
}
