using GitGavel.Discovery;

namespace GitGavel.System1.Models;

public record SubagentGatingDecision(
    string AgentName,
    bool ShouldRun,
    double Confidence,
    string Reason,
    bool IsDeterministicOverride = false);

public record SubagentGatingResult(
    IReadOnlyList<SubagentGatingDecision> Decisions,
    long EstimatedTokensSaved = 0)
{
    public IReadOnlyList<string> ActiveAgentNames =>
        Decisions.Where(d => d.ShouldRun).Select(d => d.AgentName).ToList();

    public IReadOnlyList<string> SkippedAgentNames =>
        Decisions.Where(d => !d.ShouldRun).Select(d => d.AgentName).ToList();

    public bool HasSkippedAgents => SkippedAgentNames.Count > 0;
}

public record GatingSignals
{
    public bool IsPullRequestMode { get; init; }
    public string? PrTitle { get; init; }
    public string? PrDescription { get; init; }
    public bool HasSpecChanges { get; init; }
    public bool HasSourceCodeChanges { get; init; }
    public bool HasTestChanges { get; init; }
    public bool HasSarifAlerts { get; init; }
    public int SarifFindingsCount { get; init; }
    public bool HasComplexityHotspots { get; init; }
    public int MaxCyclomaticComplexity { get; init; }
    public bool HasSecuritySensitivePaths { get; init; }
    public int SpecFilesCount { get; init; }
    public int SourceFilesCount { get; init; }
    public int TestFilesCount { get; init; }
    public IReadOnlyList<object> ChangedFilesSummary { get; init; } = [];

    public static GatingSignals FromRepoContext(RepoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var specFiles = context.SpecFiles ?? [];
        var sourceFiles = context.SourceFiles ?? [];
        var testFiles = context.TestFiles ?? [];
        var sarifFindings = context.SarifFindings ?? [];
        var astSummaries = context.AstSummaries ?? [];

        var hasSpecChanges = context.IsPullRequestMode
            ? specFiles.Any(f => !string.IsNullOrEmpty(f.DiffPatch)) ||
              sourceFiles.Any(f => f.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(f.DiffPatch))
            : specFiles.Count > 0 ||
              sourceFiles.Any(f => f.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

        var hasSourceCodeChanges = context.IsPullRequestMode
            ? sourceFiles.Any(f => !f.IsTest && !string.IsNullOrEmpty(f.DiffPatch))
            : sourceFiles.Any(f => !f.IsTest);

        var hasTestChanges = context.IsPullRequestMode
            ? testFiles.Any(f => !string.IsNullOrEmpty(f.DiffPatch))
            : testFiles.Count > 0;

        var hasSarif = sarifFindings.Count > 0;
        var hasComplexity = astSummaries.Any(a => a.TotalComplexityScore >= 15 || a.IsComplexityHotspot);
        var maxComplexity = astSummaries.Count > 0 ? astSummaries.Max(a => a.TotalComplexityScore) : 0;

        var hasSecurityPaths = sourceFiles.Any(f =>
            f.RelativePath.Contains("Auth", StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.Contains("Key", StringComparison.OrdinalIgnoreCase));

        var changedFilesList = context.ActivePullRequest?.ChangedFiles?.Select(f => (object)new { f.FilePath, f.Additions, f.Deletions }).ToList() ??
                               sourceFiles.Where(f => !string.IsNullOrEmpty(f.DiffPatch)).Select(f => (object)new { FilePath = f.RelativePath, Additions = f.LineCount, Deletions = 0 }).ToList();

        if (changedFilesList.Count == 0 && sourceFiles.Count > 0)
        {
            changedFilesList = sourceFiles.Take(25).Select(f => (object)new { FilePath = f.RelativePath, Additions = f.LineCount, Deletions = 0 }).ToList();
        }

        var prBody = context.ActivePullRequest?.Body;
        if (prBody != null && prBody.Length > 500)
        {
            prBody = prBody[..500];
        }

        return new GatingSignals
        {
            IsPullRequestMode = context.IsPullRequestMode,
            PrTitle = context.ActivePullRequest?.Title,
            PrDescription = prBody,
            HasSpecChanges = hasSpecChanges,
            HasSourceCodeChanges = hasSourceCodeChanges,
            HasTestChanges = hasTestChanges,
            HasSarifAlerts = hasSarif,
            SarifFindingsCount = sarifFindings.Count,
            HasComplexityHotspots = hasComplexity,
            MaxCyclomaticComplexity = maxComplexity,
            HasSecuritySensitivePaths = hasSecurityPaths,
            SpecFilesCount = specFiles.Count,
            SourceFilesCount = sourceFiles.Count,
            TestFilesCount = testFiles.Count,
            ChangedFilesSummary = changedFilesList
        };
    }
}
