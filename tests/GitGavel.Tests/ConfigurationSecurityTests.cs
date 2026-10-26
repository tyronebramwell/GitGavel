using System.Security;
using FluentAssertions;
using GitGavel.Configuration;

namespace GitGavel.Tests;

public class ConfigurationSecurityTests
{
    [Fact]
    public void LoadFromString_WithTraversalInSpecInclude_ThrowsSecurityException()
    {
        var yaml = """
        version: 1
        scope:
          specs:
            include: ["../../etc/passwd"]
        """;

        var act = () => ConfigLoader.LoadFromString(yaml);
        act.Should().Throw<SecurityException>()
            .WithMessage("*Malicious scope glob pattern detected*");
    }

    [Fact]
    public void LoadFromString_WithRootedSpecInclude_ThrowsSecurityException()
    {
        var yaml = """
        version: 1
        scope:
          specs:
            include: ["/etc/shadow"]
        """;

        var act = () => ConfigLoader.LoadFromString(yaml);
        act.Should().Throw<SecurityException>()
            .WithMessage("*Malicious scope glob pattern detected*");
    }

    [Fact]
    public void LoadFromString_WithTraversalInSourceInclude_ThrowsSecurityException()
    {
        var yaml = """
        version: 1
        scope:
          source:
            include: ["../outside/**/*.cs"]
        """;

        var act = () => ConfigLoader.LoadFromString(yaml);
        act.Should().Throw<SecurityException>()
            .WithMessage("*Malicious scope glob pattern detected in scope.source.include*");
    }

    [Fact]
    public void LoadFromString_WithTraversalInTestsInclude_ThrowsSecurityException()
    {
        var yaml = """
        version: 1
        scope:
          tests:
            include: ["tests/../../evil/**/*.cs"]
        """;

        var act = () => ConfigLoader.LoadFromString(yaml);
        act.Should().Throw<SecurityException>()
            .WithMessage("*Malicious scope glob pattern detected in scope.tests.include*");
    }

    [Fact]
    public void LoadFromString_WithTraversalInSarifReports_ThrowsSecurityException()
    {
        var yaml = """
        version: 1
        subagents:
          security:
            sarifReports: ["../../reports/outside.sarif"]
        """;

        var act = () => ConfigLoader.LoadFromString(yaml);
        act.Should().Throw<SecurityException>()
            .WithMessage("*Unsafe SARIF report path detected*");
    }

    [Fact]
    public void LoadFromString_WithRootedSarifReport_WithoutAllowExternal_ThrowsSecurityException()
    {
        var yaml = """
        version: 1
        subagents:
          security:
            sarifReports: ["/var/log/system.sarif"]
        """;

        var act = () => ConfigLoader.LoadFromString(yaml, allowExternalPaths: false);
        act.Should().Throw<SecurityException>()
            .WithMessage("*Unsafe SARIF report path detected*");
    }

    [Fact]
    public void LoadFromString_WithRootedSarifReport_WithAllowExternal_Succeeds()
    {
        var yaml = """
        version: 1
        subagents:
          security:
            sarifReports: ["/var/log/system.sarif"]
        """;

        var act = () => ConfigLoader.LoadFromString(yaml, allowExternalPaths: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void LoadFromString_WithTraversalInOutputDir_ThrowsSecurityException()
    {
        var yaml = """
        version: 1
        reporting:
          outputDir: "../../cron.d"
        """;

        var act = () => ConfigLoader.LoadFromString(yaml);
        act.Should().Throw<SecurityException>()
            .WithMessage("*Unsafe reporting output directory detected*");
    }

    [Fact]
    public void LoadFromString_WithRootedOutputDir_WithoutAllowExternal_ThrowsSecurityException()
    {
        var yaml = """
        version: 1
        reporting:
          outputDir: "/etc/cron.d"
        """;

        var act = () => ConfigLoader.LoadFromString(yaml, allowExternalPaths: false);
        act.Should().Throw<SecurityException>()
            .WithMessage("*Unsafe reporting output directory detected*");
    }

    [Fact]
    public void LoadFromString_WithRootedOutputDir_WithAllowExternal_Succeeds()
    {
        var yaml = """
        version: 1
        reporting:
          outputDir: "/tmp/gitgavel_reports"
        """;

        var act = () => ConfigLoader.LoadFromString(yaml, allowExternalPaths: true);
        act.Should().NotThrow();
    }
}
