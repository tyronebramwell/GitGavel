using System.Text;
using GitGavel.Discovery;

using GitGavel.Configuration;

namespace GitGavel.Reporting.Reporters;

public class CodeQualityReporter : BaseMarkdownReporter
{
    public override string ReportName => "CodeQuality";
    protected override string FileName => "4_code-quality-report.md";
    protected override bool IsEnabled(ReportsSelection reports) => reports.CodeQuality;
    protected override string GenerateReportMarkdown(ReportContext context) => Generate(context);

    public static string Generate(ReportContext ctx)
    {
        var sb = new StringBuilder();
        AppendReportMetadataHeader(sb, "GitGavel Code Quality & Spec Report", ctx);

        // 1. Spec Drift Analysis
        var specDriftFindings = ctx.Orchestration.AllFindings
            .Where(f => string.Equals(f.Category, "SpecDrift", StringComparison.OrdinalIgnoreCase))
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
                if (f.References.Count > 0)
                {
                    sb.AppendLine("**Learning References:**");
                    foreach (var r in f.References)
                    {
                        sb.AppendLine($"- {r}");
                    }
                    sb.AppendLine();
                }
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        // 2. Reliability, Bugs & Performance Analysis
        var reliabilityFindings = ctx.Orchestration.AllFindings
            .Where(f => string.Equals(f.Category, "Reliability", StringComparison.OrdinalIgnoreCase))
            .ToList();

        sb.AppendLine($"## 2. Reliability, Bugs & Performance Analysis ({reliabilityFindings.Count} items)");
        sb.AppendLine();

        if (reliabilityFindings.Count == 0)
        {
            sb.AppendLine("✅ **No functional bugs, crash paths, concurrency hazards, or resource leaks detected.**");
        }
        else
        {
            for (var i = 0; i < reliabilityFindings.Count; i++)
            {
                var f = reliabilityFindings[i];
                sb.AppendLine($"### 2.{i + 1} [{f.Severity}] {f.Title}");
                sb.AppendLine($"- **File**: `{f.FilePath}:{f.StartLine}`");
                sb.AppendLine($"- **Severity**: `{f.Severity}`");
                if (!string.IsNullOrWhiteSpace(f.DefectType))
                {
                    sb.AppendLine($"- **Defect Type**: `{f.DefectType}`");
                }
                sb.AppendLine();
                sb.AppendLine($"**Root Cause & Risk**: {f.Summary}");
                if (!string.IsNullOrWhiteSpace(f.Details))
                {
                    sb.AppendLine();
                    sb.AppendLine($"**Context & Details**: {f.Details}");
                }
                sb.AppendLine();
                sb.AppendLine("**Remediation:**");
                sb.AppendLine(f.SuggestedRemediation);
                sb.AppendLine();
                if (f.References.Count > 0)
                {
                    sb.AppendLine("**Learning References:**");
                    foreach (var r in f.References)
                    {
                        sb.AppendLine($"- {r}");
                    }
                    sb.AppendLine();
                }
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        // 3. Architectural Complexity Hotspots
        var astSummaries = ctx.RepoContext.AstSummaries;
        var hotspots = astSummaries.Where(s => s.IsComplexityHotspot).OrderByDescending(s => s.TotalComplexityScore).ToList();

        sb.AppendLine($"## 3. Architectural Hotspots ({hotspots.Count} flagged)");
        sb.AppendLine();

        if (hotspots.Count == 0)
        {
            sb.AppendLine("✅ **No high-complexity hotspots flagged.** All files maintain healthy cyclomatic branching metrics.");
        }
        else
        {
            sb.AppendLine("| File Path | Complexity Score | Declared Symbols | Status |");
            sb.AppendLine("| :--- | :--- | :--- | :--- |");

            foreach (var h in hotspots)
            {
                sb.AppendLine($"| `{h.RelativePath}` | **{h.TotalComplexityScore}** | {h.Symbols.Count} symbols | ⚠️ High Branching Density |");
            }
        }
        sb.AppendLine();

        // 4. Refactoring Catalog
        var refactoringFindings = ctx.Orchestration.AllFindings
            .Where(f => string.Equals(f.Category, "Refactoring", StringComparison.OrdinalIgnoreCase))
            .ToList();

        sb.AppendLine($"## 4. Refactoring & Maintainability Catalog ({refactoringFindings.Count} recommendations)");
        sb.AppendLine();

        if (refactoringFindings.Count == 0)
        {
            sb.AppendLine("✅ **No urgent refactoring debt flagged.**");
        }
        else
        {
            for (var i = 0; i < refactoringFindings.Count; i++)
            {
                var f = refactoringFindings[i];
                sb.AppendLine($"### 4.{i + 1} [{f.Severity}] {f.Title}");
                sb.AppendLine($"- **Target**: `{f.FilePath}` (lines {f.StartLine}–{f.EndLine})");
                sb.AppendLine();
                sb.AppendLine($"**Problem**: {f.Summary}");
                sb.AppendLine();
                sb.AppendLine("**Proposed Refactoring Pattern:**");
                sb.AppendLine(f.SuggestedRemediation);
                sb.AppendLine();
                if (f.References.Count > 0)
                {
                    sb.AppendLine("**Learning References:**");
                    foreach (var r in f.References)
                    {
                        sb.AppendLine($"- {r}");
                    }
                    sb.AppendLine();
                }
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
