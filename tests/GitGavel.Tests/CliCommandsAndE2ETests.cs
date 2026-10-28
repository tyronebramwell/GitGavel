using FluentAssertions;
using GitGavel.Commands;
using GitGavel.Configuration;
using GitGavel.GitHub;
using Moq;
using Spectre.Console.Cli;

namespace GitGavel.Tests;

[Collection("EnvironmentVariables")]
public class CliCommandsAndE2ETests
{
    private static CommandContext CreateCommandContext(string name)
    {
        var remaining = new Mock<IRemainingArguments>();
        return new CommandContext([], remaining.Object, name, null);
    }

    [Fact]
    public void InitCommand_Execute_CreatesConfigRefusesOverwriteWithoutForceAndOverwritesWithForce()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_init_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var cmd = new InitCommand();
            var ctx = CreateCommandContext("init");

            // 1. Creates .gitgavel.yml when none exists
            var code1 = cmd.Execute(ctx, new InitCommand.Settings { TargetDirectory = tempDir, Force = false }, CancellationToken.None);
            code1.Should().Be(0);

            var configFile = Path.Combine(tempDir, ".gitgavel.yml");
            File.Exists(configFile).Should().BeTrue();
            var content = File.ReadAllText(configFile);
            content.Should().Contain("triage:");

            // Modify file to verify overwrite behavior
            File.WriteAllText(configFile, "# custom marker");

            // 2. Refuses overwrite when Force = false
            var code2 = cmd.Execute(ctx, new InitCommand.Settings { TargetDirectory = tempDir, Force = false }, CancellationToken.None);
            code2.Should().Be(1);
            File.ReadAllText(configFile).Should().Be("# custom marker");

            // 3. Overwrites when Force = true
            var code3 = cmd.Execute(ctx, new InitCommand.Settings { TargetDirectory = tempDir, Force = true }, CancellationToken.None);
            code3.Should().Be(0);
            File.ReadAllText(configFile).Should().Contain("triage:");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ValidateCommand_ExecuteAsync_ValidatesValidInvalidAndMissingConfigFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_validate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var cmd = new ValidateCommand();
            var ctx = CreateCommandContext("validate");
            var configPath = Path.Combine(tempDir, ".gitgavel.yml");

            // 1. Missing config file returns exit code 1
            var missingCode = await cmd.ExecuteAsync(ctx, new ValidateCommand.Settings
            {
                ConfigPath = configPath,
                AllowExternalPaths = true
            }, CancellationToken.None);
            missingCode.Should().Be(1);

            // 2. Valid config file returns exit code 0
            await File.WriteAllTextAsync(configPath, ConfigLoader.Serialize(new GitGavelConfig()));
            var validCode = await cmd.ExecuteAsync(ctx, new ValidateCommand.Settings
            {
                ConfigPath = configPath,
                AllowExternalPaths = true
            }, CancellationToken.None);
            validCode.Should().Be(0);

            // 3. Invalid config file (maxIssuesPerRun: 0) returns exit code 1
            await File.WriteAllTextAsync(configPath, "version: 1\ntriage:\n  maxIssuesPerRun: 0\n");
            var invalidCode = await cmd.ExecuteAsync(ctx, new ValidateCommand.Settings
            {
                ConfigPath = configPath,
                AllowExternalPaths = true
            }, CancellationToken.None);
            invalidCode.Should().Be(1);

            // 4. External path without AllowExternalPaths returns exit code 1
            var externalBlockedCode = await cmd.ExecuteAsync(ctx, new ValidateCommand.Settings
            {
                ConfigPath = configPath,
                AllowExternalPaths = false
            }, CancellationToken.None);
            externalBlockedCode.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_E2E_DryRunMock_GeneratesAllFiveMarkdownReportsAndTicketsJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_e2e_full_" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(tempDir, "src");
        var testsDir = Path.Combine(tempDir, "tests");
        var reportsDir = Path.Combine(tempDir, "reports");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testsDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "SPECIFICATION.md"), "# Specification\nMust validate user input.");
            await File.WriteAllTextAsync(Path.Combine(srcDir, "OrderService.cs"), "public class OrderService { public void Process() {} }");
            await File.WriteAllTextAsync(Path.Combine(testsDir, "OrderServiceTests.cs"), "public class OrderServiceTests {}");

            var cmd = new AuditCommand();
            var ctx = CreateCommandContext("audit");
            var settings = new AuditCommand.Settings
            {
                RepoDirectory = tempDir,
                DryRun = true,
                UseMock = true,
                AllAgents = true,
                AllowExternalPaths = true
            };

            var exitCode = await cmd.ExecuteAsync(ctx, settings, CancellationToken.None);

            exitCode.Should().Be(0);
            File.Exists(Path.Combine(reportsDir, "1_audit-run.md")).Should().BeTrue();
            File.Exists(Path.Combine(reportsDir, "2_tickets-report.md")).Should().BeTrue();
            File.Exists(Path.Combine(reportsDir, "2_tickets-report.json")).Should().BeTrue();
            File.Exists(Path.Combine(reportsDir, "3_security-report.md")).Should().BeTrue();
            File.Exists(Path.Combine(reportsDir, "4_code-quality-report.md")).Should().BeTrue();
            File.Exists(Path.Combine(reportsDir, "5_test-quality-report.md")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_E2E_LiveMockWithPr_CreatesIssuesClosesResolvedAndPostsPrReview()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_e2e_pr_" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(tempDir, "src", "GitGavel", "Git");
        var reportsDir = Path.Combine(tempDir, "reports");
        Directory.CreateDirectory(srcDir);

        var prevInputAllAgents = Environment.GetEnvironmentVariable("INPUT_ALL_AGENTS");
        try
        {
            Environment.SetEnvironmentVariable("INPUT_ALL_AGENTS", "true");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "SPECIFICATION.md"), "# Spec");
            await File.WriteAllTextAsync(Path.Combine(srcDir, "GitService.cs"), "public class GitService {}");

            var mockGitHub = new MockGitHubService();
            var cmd = new AuditCommand(gitHubService: mockGitHub);
            var ctx = CreateCommandContext("audit");
            var settings = new AuditCommand.Settings
            {
                RepoDirectory = tempDir,
                DryRun = false,
                UseMock = true,
                PullRequestNumber = 42,
                AllowExternalPaths = true
            };

            var exitCode = await cmd.ExecuteAsync(ctx, settings, CancellationToken.None);

            exitCode.Should().Be(0);
            mockGitHub.PostedPrReviews.Should().NotBeEmpty();
            mockGitHub.PostedPrComments.Should().NotBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("INPUT_ALL_AGENTS", prevInputAllAgents);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
