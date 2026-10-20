using GitGavel.Configuration;
using GitGavel.Git;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace GitGavel.Discovery;

public class FileScanner
{
    private readonly IGitService _gitService;

    public FileScanner(IGitService? gitService = null)
    {
        _gitService = gitService ?? new GitService();
    }

    public async Task<RepoContext> ScanAsync(string rootDirectory, GitGavelConfig config, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(config);

        var dirInfo = new DirectoryInfo(rootDirectory);
        if (!dirInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Root directory not found: '{rootDirectory}'");
        }

        var dirWrapper = new DirectoryInfoWrapper(dirInfo);

        // 1. Scan Spec Files
        var specMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in config.Scope.Specs.Include)
        {
            specMatcher.AddInclude(pattern);
        }

        var specMatches = specMatcher.Execute(dirWrapper);
        var specFiles = new List<ScannedFile>();

        foreach (var file in specMatches.Files)
        {
            var absPath = Path.Combine(rootDirectory, file.Path);
            if (!File.Exists(absPath)) continue;

            var content = await File.ReadAllTextAsync(absPath, ct);
            var lineCount = CountLines(content);
            var fileInfo = new FileInfo(absPath);

            specFiles.Add(new ScannedFile(
                RelativePath: file.Path,
                AbsolutePath: absPath,
                LineCount: lineCount,
                SizeBytes: fileInfo.Length,
                Content: content,
                IsSpec: true));
        }

        // 2. Scan Source Files
        var sourceMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var inc in config.Scope.Source.Include)
        {
            sourceMatcher.AddInclude(inc);
        }
        foreach (var exc in config.Scope.Source.Exclude)
        {
            sourceMatcher.AddExclude(exc);
        }

        var sourceMatches = sourceMatcher.Execute(dirWrapper);
        var sourceFiles = new List<ScannedFile>();

        foreach (var file in sourceMatches.Files)
        {
            var absPath = Path.Combine(rootDirectory, file.Path);
            if (!File.Exists(absPath)) continue;

            var content = await File.ReadAllTextAsync(absPath, ct);
            var lineCount = CountLines(content);
            var fileInfo = new FileInfo(absPath);

            sourceFiles.Add(new ScannedFile(
                RelativePath: file.Path,
                AbsolutePath: absPath,
                LineCount: lineCount,
                SizeBytes: fileInfo.Length,
                Content: content,
                IsSpec: false));
        }

        // 3. Scan Test Files
        var testMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var inc in config.Scope.Tests.Include)
        {
            testMatcher.AddInclude(inc);
        }
        foreach (var exc in config.Scope.Tests.Exclude)
        {
            testMatcher.AddExclude(exc);
        }

        var testMatches = testMatcher.Execute(dirWrapper);
        var testFiles = new List<ScannedFile>();

        foreach (var file in testMatches.Files)
        {
            var absPath = Path.Combine(rootDirectory, file.Path);
            if (!File.Exists(absPath)) continue;

            var content = await File.ReadAllTextAsync(absPath, ct);
            var lineCount = CountLines(content);
            var fileInfo = new FileInfo(absPath);

            testFiles.Add(new ScannedFile(
                RelativePath: file.Path,
                AbsolutePath: absPath,
                LineCount: lineCount,
                SizeBytes: fileInfo.Length,
                Content: content,
                IsSpec: false,
                IsTest: true));
        }

        // 4. Scan SARIF Reports if configured
        var sarifFindings = new List<SarifFinding>();
        if (config.Subagents.Security.Enabled)
        {
            foreach (var sarifPath in config.Subagents.Security.SarifReports)
            {
                var fullSarifPath = Path.IsPathRooted(sarifPath)
                    ? sarifPath
                    : Path.Combine(rootDirectory, sarifPath);

                if (File.Exists(fullSarifPath))
                {
                    var findings = await SarifParser.ParseFileAsync(fullSarifPath, ct);
                    sarifFindings.AddRange(findings);
                }
            }
        }

        // 5. Precompute AST complexity analysis for source files
        var astSummaries = AstAnalyzer.AnalyzeAll(sourceFiles);

        // 6. Retrieve recent git commit history for issue resolution tracking
        var lookback = config.Subagents.IssueResolution.CommitLookbackCount > 0
            ? config.Subagents.IssueResolution.CommitLookbackCount
            : 50;
        var recentCommits = _gitService.GetRecentCommits(rootDirectory, lookback);

        return new RepoContext
        {
            RootDirectory = rootDirectory,
            SpecFiles = specFiles,
            SourceFiles = sourceFiles,
            TestFiles = testFiles,
            SarifFindings = sarifFindings,
            AstSummaries = astSummaries,
            RecentCommits = recentCommits
        };
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') count++;
        }
        return count;
    }
}
