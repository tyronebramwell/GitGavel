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
    }
}
