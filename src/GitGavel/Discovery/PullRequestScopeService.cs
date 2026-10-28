using GitGavel.System2.Models;

namespace GitGavel.Discovery;

public static class PullRequestScopeService
{
    public static RepoContext ScopeToPullRequest(RepoContext context, PullRequestContext prContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prContext);

        var changedFiles = prContext.ChangedFiles ?? [];
        var changedMap = changedFiles
            .Where(f => !string.IsNullOrWhiteSpace(f.FilePath))
            .GroupBy(f => NormalizePath(f.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var scopedSources = new List<ScannedFile>();
        var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in context.SourceFiles ?? [])
        {
            var norm = NormalizePath(src.RelativePath);
            if (changedMap.TryGetValue(norm, out var cf))
            {
                if (string.Equals(cf.Status, "removed", StringComparison.OrdinalIgnoreCase))
                {
                    matchedPaths.Add(norm);
                    continue;
                }

                scopedSources.Add(src with { DiffPatch = cf.Patch });
                matchedPaths.Add(norm);
            }
        }

        var scopedTests = new List<ScannedFile>();
        foreach (var tst in context.TestFiles ?? [])
        {
            var norm = NormalizePath(tst.RelativePath);
            if (changedMap.TryGetValue(norm, out var cf))
            {
                if (string.Equals(cf.Status, "removed", StringComparison.OrdinalIgnoreCase))
                {
                    matchedPaths.Add(norm);
                    continue;
                }

                scopedTests.Add(tst with { DiffPatch = cf.Patch });
                matchedPaths.Add(norm);
            }
        }

        var scopedSpecs = new List<ScannedFile>();
        foreach (var spec in context.SpecFiles ?? [])
        {
            var norm = NormalizePath(spec.RelativePath);
            if (changedMap.TryGetValue(norm, out var cf))
            {
                scopedSpecs.Add(spec with { DiffPatch = cf.Patch });
                matchedPaths.Add(norm);
            }
            else
            {
                scopedSpecs.Add(spec);
            }
        }

        // For any changed file in the PR not already matched in SourceFiles/TestFiles/SpecFiles, synthesize a ScannedFile from its diff patch
        foreach (var (normPath, cf) in changedMap)
        {
            if (matchedPaths.Contains(normPath) || string.Equals(cf.Status, "removed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isTestFile = normPath.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                             normPath.Contains("spec.", StringComparison.OrdinalIgnoreCase);
            var isSpecFile = scopedSpecs.Any(s => string.Equals(NormalizePath(s.RelativePath), normPath, StringComparison.OrdinalIgnoreCase));
            if (isSpecFile) continue;

            var patchContent = cf.Patch ?? "";
            var synth = new ScannedFile(
                RelativePath: cf.FilePath,
                AbsolutePath: Path.Combine(context.RootDirectory, cf.FilePath),
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

        var scopedAst = (context.AstSummaries ?? [])
            .Where(a => changedMap.TryGetValue(NormalizePath(a.RelativePath), out var cf) &&
                        !string.Equals(cf.Status, "removed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var scopedSarif = (context.SarifFindings ?? [])
            .Where(s => changedMap.ContainsKey(NormalizePath(s.FilePath)))
            .ToList();

        return new RepoContext
        {
            RootDirectory = context.RootDirectory,
            SpecFiles = scopedSpecs,
            SourceFiles = scopedSources,
            TestFiles = scopedTests,
            SarifFindings = scopedSarif,
            AstSummaries = scopedAst.Count > 0 ? scopedAst : AstAnalyzer.AnalyzeAll(scopedSources),
            RecentCommits = context.RecentCommits ?? [],
            ActivePullRequest = prContext
        };
    }

    public static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').TrimStart('.', '/');
}
