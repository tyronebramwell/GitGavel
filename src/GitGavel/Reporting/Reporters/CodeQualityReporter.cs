using System.Text;
using GitGavel.Discovery;

namespace GitGavel.Reporting.Reporters;

public static class CodeQualityReporter
{
    public static string Generate(ReportContext ctx)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# GitGavel Code Quality & Spec Report");
        sb.AppendLine();
        sb.AppendLine($"> Generated at **{ctx.EndTimeUtc:yyyy-MM-dd HH:mm:ss} UTC** | Commit: `{ctx.GitCommitSha}`");
        sb.AppendLine();

        // 1. Spec Drift Analysis
        var specDriftFindings = ctx.Orchestration.AllFindings
            .Where(f => f.Category.Equals("SpecDrift", StringComparison.OrdinalIgnoreCase))
            .ToList();

        sb.AppendLine($"## 1. Specification Drift Analysis ({specDriftFindings.Count} items)");
        sb.AppendLine();

        if (specDriftFindings.Count == 0)
        {
            sb.AppendLine("✅ **No spec drift detected between specification documents and source code.**");
        }
        else
        {
            for (var i = 0; i < specDriftFindings.Count; i++)
            {
                var f = specDriftFindings[i];
                sb.AppendLine($"### 1.{i + 1} {f.Title}");
                sb.AppendLine($"- **File**: `{f.FilePath}:{f.StartLine}`");
                sb.AppendLine($"- **Severity**: `{f.Severity}`");
                sb.AppendLine();
                sb.AppendLine($"**Drift Details**: {f.Summary}");
                if (!string.IsNullOrWhiteSpace(f.Details))
                {
                    sb.AppendLine();
                    sb.AppendLine($"**Context**: {f.Details}");
                }
                sb.AppendLine();
                sb.AppendLine("**Remediation:**");
                sb.AppendLine(f.SuggestedRemediation);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        // 2. Architectural Complexity Hotspots
        var astSummaries = AstAnalyzer.AnalyzeAll(ctx.RepoContext.SourceFiles);
        var hotspots = astSummaries.Where(s => s.IsComplexityHotspot).OrderByDescending(s => s.TotalComplexityScore).ToList();

        sb.AppendLine($"## 2. Architectural Hotspots ({hotspots.Count} flagged)");
        sb.AppendLine();

        if (hotspots.Count == 0)
        {
            sb.AppendLine("✅ **No severe cyclomatic complexity hotspots detected.**");
        }
        else
        {
            sb.AppendLine("| File Path | Complexity Score | Declared Symbols | Status |");
            sb.AppendLine("| :--- | :--- | :--- | :--- |");
            foreach (var spot in hotspots)
            {
                sb.AppendLine($"| `{spot.RelativePath}` | **{spot.TotalComplexityScore}** | {spot.Symbols.Count} symbols | ⚠️ High Branching Density |");
            }
            sb.AppendLine();
        }

        // 3. Refactoring Catalog
        var refactorFindings = ctx.Orchestration.AllFindings
            .Where(f => f.Category.Equals("Refactoring", StringComparison.OrdinalIgnoreCase))
            .ToList();

        sb.AppendLine($"## 3. Refactoring & Maintainability Catalog ({refactorFindings.Count} recommendations)");
        sb.AppendLine();

        if (refactorFindings.Count == 0)
        {
            sb.AppendLine("✅ **No urgent refactoring debt flagged.**");
        }
        else
        {
            for (var i = 0; i < refactorFindings.Count; i++)
            {
                var f = refactorFindings[i];
                sb.AppendLine($"### 3.{i + 1} [{f.Severity}] {f.Title}");
                sb.AppendLine($"- **Target**: `{f.FilePath}` (lines {f.StartLine}–{f.EndLine})");
                sb.AppendLine();
                sb.AppendLine($"**Problem**: {f.Summary}");
                sb.AppendLine();
                sb.AppendLine("**Proposed Refactoring Pattern:**");
                sb.AppendLine(f.SuggestedRemediation);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
