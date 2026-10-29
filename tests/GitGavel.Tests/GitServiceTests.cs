using FluentAssertions;
using GitGavel.Git;

namespace GitGavel.Tests;

[Collection("EnvironmentVariables")]
public class GitServiceTests
{
    [Fact]
    public void GitService_InCurrentRepo_ReturnsValidMetadata()
    {
        var gitService = new GitService();
        var repoRoot = Directory.GetCurrentDirectory();

        var (sha, branch) = gitService.GetGitMetadata(repoRoot);

        sha.Should().NotBeNullOrWhiteSpace();
        branch.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GitService_InNonGitDirectory_ReturnsFallbackGracefully()
    {
        var prevSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        var prevBranch = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
        var prevRepo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");

        var gitService = new GitService();
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_nongit_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            Environment.SetEnvironmentVariable("GITHUB_SHA", null);
            Environment.SetEnvironmentVariable("GITHUB_REF_NAME", null);
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", null);

            var (sha, branch) = gitService.GetGitMetadata(tempDir);
            var (owner, repo) = gitService.ResolveRepoCoordinates(tempDir);

            sha.Should().Be("local-dev");
            branch.Should().Be("main");
            owner.Should().Be("local-owner");
            repo.Should().Be("GitGavel");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_SHA", prevSha);
            Environment.SetEnvironmentVariable("GITHUB_REF_NAME", prevBranch);
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", prevRepo);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GitService_InCiEnvironment_UsesCiEnvironmentVariables()
    {
        var prevSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        var prevBranch = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
        var prevRepo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");

        var gitService = new GitService();
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_ci_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            Environment.SetEnvironmentVariable("GITHUB_SHA", "1234567890abcdef");
            Environment.SetEnvironmentVariable("GITHUB_REF_NAME", "feature-branch");
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", "ci-org/ci-repo");

            var (sha, branch) = gitService.GetGitMetadata(tempDir);
            var (owner, repo) = gitService.ResolveRepoCoordinates(tempDir);

            sha.Should().Be("1234567");
            branch.Should().Be("feature-branch");
            owner.Should().Be("ci-org");
            repo.Should().Be("ci-repo");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_SHA", prevSha);
            Environment.SetEnvironmentVariable("GITHUB_REF_NAME", prevBranch);
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", prevRepo);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GitService_WithExplicitCoordinates_ResolvesCorrectly()
    {
        var gitService = new GitService();
        var (owner, repo) = gitService.ResolveRepoCoordinates(Directory.GetCurrentDirectory(), "myorg/mycustomrepo");

        owner.Should().Be("myorg");
        repo.Should().Be("mycustomrepo");
    }

    [Fact]
    public void GitService_WithInvalidCommand_ReturnsNullWithoutThrowing()
    {
        var gitService = new GitService();
        var result = gitService.RunGitCommand(Directory.GetCurrentDirectory(), "invalid-subcommand-12345");
        result.Should().BeNull();
    }

    [Fact]
    public void GitService_GetRecentCommits_InCurrentRepo_ReturnsCommitHistory()
    {
        var gitService = new GitService();
        var commits = gitService.GetRecentCommits(Directory.GetCurrentDirectory(), 5);

        commits.Should().NotBeEmpty();
        commits[0].Sha.Should().NotBeNullOrWhiteSpace();
        commits[0].ShortSha.Should().NotBeNullOrWhiteSpace();
        commits[0].Subject.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GitService_ResolvePullRequestNumber_ResolvesFromExplicitEnvAndGithubRef()
    {
        var prevInputPr = Environment.GetEnvironmentVariable("INPUT_PR_NUMBER");
        var prevRef = Environment.GetEnvironmentVariable("GITHUB_REF");
        var gitService = new GitService();

        try
        {
            Environment.SetEnvironmentVariable("INPUT_PR_NUMBER", null);
            Environment.SetEnvironmentVariable("GITHUB_REF", null);

            gitService.ResolvePullRequestNumber(42).Should().Be(42);

            Environment.SetEnvironmentVariable("GITHUB_REF", "refs/pull/88/merge");
            gitService.ResolvePullRequestNumber(null).Should().Be(88);

            Environment.SetEnvironmentVariable("INPUT_PR_NUMBER", "99");
            gitService.ResolvePullRequestNumber(null).Should().Be(99);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INPUT_PR_NUMBER", prevInputPr);
            Environment.SetEnvironmentVariable("GITHUB_REF", prevRef);
        }
    }

    [Fact]
    public void GitService_RunGitCommand_AppliesSafeDirectoryConfigurationFlag()
    {
        var gitService = new GitService();
        var output = gitService.RunGitCommand(Directory.GetCurrentDirectory(), "config --get-all safe.directory");

        output.Should().NotBeNullOrWhiteSpace();
        output!.Split('\n', StringSplitOptions.TrimEntries).Should().Contain("*");
    }

    [Theory]
    [InlineData("status && touch /tmp/injected_command.txt")]
    [InlineData("log; echo hacked")]
    [InlineData("rev-parse | whoami")]
    [InlineData("status `echo test`")]
    [InlineData("status $(echo test)")]
    public void GitService_RunGitCommand_TreatsShellMetacharactersLiterally_PreventingCommandInjection(string maliciousArg)
    {
        var gitService = new GitService();
        var canaryFile = Path.Combine(Path.GetTempPath(), $"canary_{Guid.NewGuid():N}.txt");

        try
        {
            var injectionCommand = $"status && touch \"{canaryFile}\"";
            gitService.RunGitCommand(Directory.GetCurrentDirectory(), injectionCommand);

            // Injected shell command must NOT execute because ArgumentList bypasses shell evaluation
            File.Exists(canaryFile).Should().BeFalse();

            // When passed with arbitrary metacharacters, git treats them as literal parameters
            gitService.RunGitCommand(Directory.GetCurrentDirectory(), maliciousArg);
        }
        finally
        {
            if (File.Exists(canaryFile)) File.Delete(canaryFile);
        }
    }

    [Fact]
    public void GitService_TokenizeArguments_PreservesQuotedStringsWithSpaces()
    {
        var tokens = GitService.TokenizeArguments("log -n 5 --pretty=format:\"COMMIT_START|%H|%h|%s\" --name-only");
        tokens.Should().BeEquivalentTo([
            "log",
            "-n",
            "5",
            "--pretty=format:COMMIT_START|%H|%h|%s",
            "--name-only"
        ], options => options.WithStrictOrdering());
    }

    [Fact]
    public void GitService_TokenizeArguments_HandlesEscapedQuotes_CorrectlyPreservingNestedQuotes()
    {
        var tokens = GitService.TokenizeArguments("commit -m \"Fix \\\"bug\\\" in parser\" --author=\"Jane Doe <jane@example.com>\"");
        tokens.Should().BeEquivalentTo([
            "commit",
            "-m",
            "Fix \"bug\" in parser",
            "--author=Jane Doe <jane@example.com>"
        ], options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData("https://github.com/octocat/Hello-World.git", true, "octocat", "Hello-World")]
    [InlineData("https://github.com/octocat/Hello-World/", true, "octocat", "Hello-World")]
    [InlineData("git@github.com:octocat/Hello-World.git", true, "octocat", "Hello-World")]
    [InlineData("ssh://git@github.com/octocat/Hello-World.git", true, "octocat", "Hello-World")]
    [InlineData("https://github.com/", false, "", "")]
    [InlineData("https://github.com/only-owner", false, "", "")]
    [InlineData("git@github.com:", false, "", "")]
    [InlineData("git@github.com:only-owner", false, "", "")]
    [InlineData("not-a-valid-url", false, "", "")]
    [InlineData("///", false, "", "")]
    [InlineData("", false, "", "")]
    public void GitService_TryParseGitRemoteUrl_HandlesValidAndMalformedUrls(
        string remoteUrl,
        bool expectedSuccess,
        string expectedOwner,
        string expectedRepo)
    {
        var ok = GitService.TryParseGitRemoteUrl(remoteUrl, out var coords);
        ok.Should().Be(expectedSuccess);
        if (expectedSuccess)
        {
            coords.owner.Should().Be(expectedOwner);
            coords.repo.Should().Be(expectedRepo);
        }
    }

    [Fact]
    public void GitService_ResolvePullRequestNumber_ParsesGitHubEventPayloadFile()
    {
        var tempEventFile = Path.GetTempFileName();
        try
        {
            // 1. pull_request.number payload
            File.WriteAllText(tempEventFile, """{ "pull_request": { "number": 123 } }""");
            var env = new DictEnvProvider(new Dictionary<string, string?>
            {
                ["GITHUB_EVENT_PATH"] = tempEventFile
            });
            var gitService = new GitService(environment: env);
            gitService.ResolvePullRequestNumber(null).Should().Be(123);

            // 2. issue_comment on PR payload (issue.number + issue.pull_request)
            File.WriteAllText(tempEventFile, """{ "issue": { "number": 456, "pull_request": {} } }""");
            gitService.ResolvePullRequestNumber(null).Should().Be(456);
        }
        finally
        {
            if (File.Exists(tempEventFile)) File.Delete(tempEventFile);
        }
    }

    [Fact]
    public void ProcessRunner_Run_WhenProcessTimesOut_KillsProcessReturnsNullAndInvokesOnError()
    {
        var runner = new ProcessRunner();
        string? capturedError = null;

        var result = runner.Run(
            "sleep",
            Directory.GetCurrentDirectory(),
            ["3"],
            timeoutMs: 80,
            onError: msg => capturedError = msg);

        result.Should().BeNull();
        capturedError.Should().NotBeNullOrWhiteSpace();
        capturedError.Should().Contain("timed out after 80ms");
    }

    [Fact]
    public void ProcessRunner_Run_WhenProcessExitsNonZeroOrBinaryMissing_ReturnsNullAndInvokesOnError()
    {
        var runner = new ProcessRunner();
        string? exitError = null;

        var nonZeroResult = runner.Run(
            "git",
            Directory.GetCurrentDirectory(),
            ["invalid-git-subcommand-xyz"],
            timeoutMs: 5000,
            onError: msg => exitError = msg);

        nonZeroResult.Should().BeNull();
        exitError.Should().NotBeNullOrWhiteSpace();
        exitError.Should().Contain("exited with code");

        string? missingBinError = null;
        var missingResult = runner.Run(
            "nonexistent-executable-binary-xyz-999",
            Directory.GetCurrentDirectory(),
            ["--version"],
            timeoutMs: 5000,
            onError: msg => missingBinError = msg);

        missingResult.Should().BeNull();
        missingBinError.Should().NotBeNullOrWhiteSpace();
        missingBinError.Should().Contain("Failed to execute");
    }

    private sealed class DictEnvProvider(Dictionary<string, string?> vars) : GitGavel.Configuration.IEnvironmentProvider
    {
        public string? GetEnvironmentVariable(string variable) => vars.TryGetValue(variable, out var v) ? v : null;
        public void SetEnvironmentVariable(string variable, string? value) => vars[variable] = value;
    }
}
