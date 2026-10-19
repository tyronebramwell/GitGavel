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
        var gitService = new GitService();
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_nongit_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var (sha, branch) = gitService.GetGitMetadata(tempDir);
            var (owner, repo) = gitService.ResolveRepoCoordinates(tempDir);

            sha.Should().Be("local-dev");
            branch.Should().Be("main");
            owner.Should().Be("local-owner");
            repo.Should().Be("GitGavel");
        }
        finally
        {
            Directory.Delete(tempDir, true);
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
