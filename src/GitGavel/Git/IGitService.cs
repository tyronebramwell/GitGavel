namespace GitGavel.Git;

public interface IGitService
{
    (string commitSha, string branch) GetGitMetadata(string workingDirectory);
    (string owner, string repo) ResolveRepoCoordinates(string workingDirectory, string? explicitRepo = null);
    string? RunGitCommand(string workingDirectory, string args);
}
