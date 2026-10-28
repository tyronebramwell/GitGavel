using GitGavel.Configuration;
using GitGavel.Git;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace GitGavel.Discovery;

public class FileScanner
{
    private const long DefaultMaxFileSizeBytes = 1024 * 1024; // 1 MB
    private const int MaxConcurrentFileReads = 16;

    private readonly IGitService _gitService;

    public FileScanner(IGitService? gitService = null)
    {
        _gitService = gitService ?? new GitService();
    }

    public async Task<RepoContext> ScanAsync(
        string rootDirectory,
        GitGavelConfig config,
        bool allowExternalPaths = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(config);

        PathSecurity.ValidateRootDirectory(rootDirectory);

        var dirInfo = new DirectoryInfo(rootDirectory);
        if (!dirInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Root directory not found: '{rootDirectory}'");
        }

        var dirWrapper = new DirectoryInfoWrapper(dirInfo);
        var maxFileSizeBytes = config.Scope?.MaxFileSizeBytes > 0
            ? config.Scope.MaxFileSizeBytes
            : DefaultMaxFileSizeBytes;

        var specFiles = await ScanSpecFilesAsync(rootDirectory, dirWrapper, config, maxFileSizeBytes, allowExternalPaths, ct);
        var sourceFiles = await ScanSourceFilesAsync(rootDirectory, dirWrapper, config, maxFileSizeBytes, allowExternalPaths, ct);
        var testFiles = await ScanTestFilesAsync(rootDirectory, dirWrapper, config, maxFileSizeBytes, allowExternalPaths, ct);
        var sarifFindings = await LoadSarifReportsAsync(rootDirectory, config, allowExternalPaths, ct);

        var astSummaries = AstAnalyzer.AnalyzeAll(sourceFiles);

        var lookback = config.Subagents?.IssueResolution?.CommitLookbackCount > 0
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

    private static Task<List<ScannedFile>> ScanSpecFilesAsync(
        string rootDirectory,
        DirectoryInfoBase dirWrapper,
        GitGavelConfig config,
        long maxFileSizeBytes,
        bool allowExternalPaths,
        CancellationToken ct) =>
        ScanFilesWithMatcherAsync(
            rootDirectory,
            dirWrapper,
            includes: config.Scope?.Specs?.Include,
            excludes: null,
            isSpec: true,
            isTest: false,
            maxFileSizeBytes: maxFileSizeBytes,
            allowExternalPaths: allowExternalPaths,
            ct: ct);

    private static Task<List<ScannedFile>> ScanSourceFilesAsync(
        string rootDirectory,
        DirectoryInfoBase dirWrapper,
        GitGavelConfig config,
        long maxFileSizeBytes,
        bool allowExternalPaths,
        CancellationToken ct) =>
        ScanFilesWithMatcherAsync(
            rootDirectory,
            dirWrapper,
            includes: config.Scope?.Source?.Include,
            excludes: config.Scope?.Source?.Exclude,
            isSpec: false,
            isTest: false,
            maxFileSizeBytes: maxFileSizeBytes,
            allowExternalPaths: allowExternalPaths,
            ct: ct);

    private static Task<List<ScannedFile>> ScanTestFilesAsync(
        string rootDirectory,
        DirectoryInfoBase dirWrapper,
        GitGavelConfig config,
        long maxFileSizeBytes,
        bool allowExternalPaths,
        CancellationToken ct) =>
        ScanFilesWithMatcherAsync(
            rootDirectory,
            dirWrapper,
            includes: config.Scope?.Tests?.Include,
            excludes: config.Scope?.Tests?.Exclude,
            isSpec: false,
            isTest: true,
            maxFileSizeBytes: maxFileSizeBytes,
            allowExternalPaths: allowExternalPaths,
            ct: ct);

    private static async Task<List<ScannedFile>> ScanFilesWithMatcherAsync(
        string rootDirectory,
        DirectoryInfoBase dirWrapper,
        IEnumerable<string>? includes,
        IEnumerable<string>? excludes,
        bool isSpec,
        bool isTest,
        long maxFileSizeBytes,
        bool allowExternalPaths,
        CancellationToken ct)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var hasIncludes = false;

        foreach (var pattern in includes ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            matcher.AddInclude(pattern);
            hasIncludes = true;
        }

        if (!hasIncludes)
        {
            return [];
        }

        foreach (var pattern in excludes ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            matcher.AddExclude(pattern);
        }

        var matchResult = matcher.Execute(dirWrapper);
        var matchedFiles = matchResult.Files.ToList();
        if (matchedFiles.Count == 0)
        {
            return [];
        }

        using var throttle = new SemaphoreSlim(MaxConcurrentFileReads);
        var tasks = matchedFiles.Select(async file =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var absPath = Path.Combine(rootDirectory, file.Path);
                if (!File.Exists(absPath)) return null;

                var verifiedPath = PathSecurity.ResolveAndVerifyWithinRoot(absPath, rootDirectory, allowExternalPaths);
                var fileInfo = new FileInfo(verifiedPath);
                if (!fileInfo.Exists || fileInfo.Length > maxFileSizeBytes)
                {
                    return null;
                }

                var content = await File.ReadAllTextAsync(verifiedPath, ct);
                var lineCount = CountLines(content);

                return new ScannedFile(
                    RelativePath: file.Path,
                    AbsolutePath: verifiedPath,
                    LineCount: lineCount,
                    SizeBytes: fileInfo.Length,
                    Content: content,
                    IsSpec: isSpec,
                    IsTest: isTest);
            }
            finally
            {
                throttle.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(f => f != null).Select(f => f!).ToList();
    }

    private static async Task<List<SarifFinding>> LoadSarifReportsAsync(
        string rootDirectory,
        GitGavelConfig config,
        bool allowExternalPaths,
        CancellationToken ct)
    {
        var sarifFindings = new List<SarifFinding>();
        if (config.Subagents?.Security?.Enabled != true)
        {
            return sarifFindings;
        }

        foreach (var sarifPath in config.Subagents.Security.SarifReports ?? [])
        {
            if (string.IsNullOrWhiteSpace(sarifPath)) continue;

            var verifiedSarifPath = PathSecurity.ResolveAndVerifyWithinRoot(sarifPath, rootDirectory, allowExternalPaths);
            if (File.Exists(verifiedSarifPath))
            {
                var findings = await SarifParser.ParseFileAsync(verifiedSarifPath, ct);
                sarifFindings.AddRange(findings);
            }
        }

        return sarifFindings;
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
