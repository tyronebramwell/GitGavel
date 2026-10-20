using FluentAssertions;
using GitGavel.Git;

namespace GitGavel.Tests;

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
}
