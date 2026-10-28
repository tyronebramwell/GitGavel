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

    [Fact]
    public void LoadFromString_WithDirectApiKeyInSubagents_ThrowsInvalidOperationException()
    {
        var yaml = """
        version: 1
        subagents:
          apiKey: "AIzaSyD-unauthorized-plain-text-key"
          apiKeyEnv: "AI_API_KEY"
        """;

        var act = () => ConfigLoader.LoadFromString(yaml);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Direct API keys in configuration files are forbidden*");
    }

    [Fact]
    public void LoadFromString_WithDirectApiKeyInJev_ThrowsInvalidOperationException()
    {
        var yaml = """
        version: 1
        triage:
          engine: "jev"
          jev:
            apiKey: "jev_sec_key_12345"
            apiKeyEnv: "JEV_API_KEY"
        """;

        var act = () => ConfigLoader.LoadFromString(yaml);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Direct API keys in configuration files are forbidden*");
    }

    [Fact]
    public void LoadFromString_WithApiKeyEnv_Succeeds()
    {
        var yaml = """
        version: 1
        subagents:
          apiKeyEnv: "CUSTOM_LLM_KEY"
        triage:
          engine: "jev"
          jev:
            apiKeyEnv: "CUSTOM_JEV_KEY"
        """;

        var config = ConfigLoader.LoadFromString(yaml);
        config.Subagents.ApiKeyEnv.Should().Be("CUSTOM_LLM_KEY");
        config.Triage.Jev.ApiKeyEnv.Should().Be("CUSTOM_JEV_KEY");
    }

    [Fact]
    public void LoadFromString_WithCommentedOutApiKey_Succeeds()
    {
        var yaml = """
        version: 1
        subagents:
          apiKeyEnv: "AI_API_KEY"
          # apiKey: "your-api-key-here"
          #   apiKey: "nested commented key"
          # api_key: "snake_case commented key"
        triage:
          engine: "jev"
          jev:
            apiKeyEnv: "JEV_API_KEY"
            # apiKey: "jev_key_here"
        """;

        var act = () => ConfigLoader.LoadFromString(yaml);
        act.Should().NotThrow();
    }
}
