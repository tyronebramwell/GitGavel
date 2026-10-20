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

    public RepoContext ScopeToPullRequest(PullRequestContext prContext)
    {
        ArgumentNullException.ThrowIfNull(prContext);

        var changedMap = prContext.ChangedFiles
            .GroupBy(f => NormalizePath(f.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var scopedSources = new List<ScannedFile>();
        var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in SourceFiles)
        {
            var norm = NormalizePath(src.RelativePath);
            if (changedMap.TryGetValue(norm, out var cf))
            {
                scopedSources.Add(src with { DiffPatch = cf.Patch });
                matchedPaths.Add(norm);
            }
        }

        var scopedTests = new List<ScannedFile>();
        foreach (var tst in TestFiles)
        {
            var norm = NormalizePath(tst.RelativePath);
            if (changedMap.TryGetValue(norm, out var cf))
            {
                scopedTests.Add(tst with { DiffPatch = cf.Patch });
                matchedPaths.Add(norm);
            }
        }

        // For any changed file in the PR not already matched in SourceFiles/TestFiles, synthesize a ScannedFile from its diff patch
        foreach (var (normPath, cf) in changedMap)
        {
            if (matchedPaths.Contains(normPath) || string.Equals(cf.Status, "removed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isTestFile = normPath.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                             normPath.Contains("spec.", StringComparison.OrdinalIgnoreCase);
            var isSpecFile = SpecFiles.Any(s => string.Equals(NormalizePath(s.RelativePath), normPath, StringComparison.OrdinalIgnoreCase));
            if (isSpecFile) continue;

            var patchContent = cf.Patch ?? "";
            var synth = new ScannedFile(
                RelativePath: cf.FilePath,
                AbsolutePath: Path.Combine(RootDirectory, cf.FilePath),
                LineCount: Math.Max(1, cf.Additions + cf.Deletions),
                SizeBytes: patchContent.Length,
                Content: patchContent,
                IsSpec: false,
                IsTest: isTestFile,
                DiffPatch: patchContent);

            if (isTestFile)
                scopedTests.Add(synth);
            else
                scopedSources.Add(synth);
        }

        var scopedAst = AstSummaries
            .Where(a => changedMap.ContainsKey(NormalizePath(a.RelativePath)))
            .ToList();

        var scopedSarif = SarifFindings
            .Where(s => changedMap.ContainsKey(NormalizePath(s.FilePath)))
            .ToList();

        return new RepoContext
        {
            RootDirectory = RootDirectory,
            SpecFiles = SpecFiles,
            SourceFiles = scopedSources,
            TestFiles = scopedTests,
            SarifFindings = scopedSarif,
            AstSummaries = scopedAst.Count > 0 ? scopedAst : AstAnalyzer.AnalyzeAll(scopedSources),
            RecentCommits = RecentCommits,
            ActivePullRequest = prContext
        };
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');
}
