namespace GitGavel.Git;

public record GitCommitRecord(
    string Sha,
    string ShortSha,
    string Subject,
    string Author,
    string DateIso,
    List<string> ChangedFiles);

public interface IGitService
{
    (string commitSha, string branch) GetGitMetadata(string workingDirectory);
    (string owner, string repo) ResolveRepoCoordinates(string workingDirectory, string? explicitRepo = null);
    IReadOnlyList<GitCommitRecord> GetRecentCommits(string workingDirectory, int maxCount = 50);
    int? ResolvePullRequestNumber(int? explicitPrNumber = null);
    string? RunGitCommand(string workingDirectory, string args);
}
