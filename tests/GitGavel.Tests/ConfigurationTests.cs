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
}
