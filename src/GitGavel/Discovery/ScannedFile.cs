using GitGavel.Git;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.Discovery;

public record ScannedFile(
    string RelativePath,
    string AbsolutePath,
    int LineCount,
    long SizeBytes,
    string Content,
    bool IsSpec,
    bool IsTest = false,
    string? DiffPatch = null);

public record SarifFinding(
    string RuleId,
    string Message,
    string Level,
    string FilePath,
    int StartLine,
    int EndLine);

public record RepoContext
{
    public required string RootDirectory { get; init; }
    public required IReadOnlyList<ScannedFile> SpecFiles { get; init; }
    public required IReadOnlyList<ScannedFile> SourceFiles { get; init; }
    public IReadOnlyList<ScannedFile> TestFiles { get; init; } = [];
    public required IReadOnlyList<SarifFinding> SarifFindings { get; init; }
    public IReadOnlyList<FileAstSummary> AstSummaries { get; init; } = [];
    public IReadOnlyList<GitCommitRecord> RecentCommits { get; init; } = [];
    public PullRequestContext? ActivePullRequest { get; init; }
    public bool IsPullRequestMode => ActivePullRequest != null;
    public long TotalLinesOfCode => SourceFiles.Sum(s => (long)s.LineCount);
    public long TotalTestLinesOfCode => TestFiles.Sum(t => (long)t.LineCount);
    public int TotalFilesScanned => SpecFiles.Count + SourceFiles.Count + TestFiles.Count;

    public RepoContext ScopeToPullRequest(PullRequestContext prContext) =>
        PullRequestScopeService.ScopeToPullRequest(this, prContext);
}
