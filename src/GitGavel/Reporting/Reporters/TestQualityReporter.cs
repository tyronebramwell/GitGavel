using System.Text;
using GitGavel.Configuration;
using GitGavel.System2.Models;

namespace GitGavel.Reporting.Reporters;

public class TestQualityReporter : BaseMarkdownReporter
{
    public override string ReportName => "TestQuality";
    protected override string FileName => "5_test-quality-report.md";
    protected override bool IsEnabled(ReportsSelection reports) => reports.TestQuality;
    protected override string GenerateReportMarkdown(ReportContext context) => Generate(context);

    public static string Generate(ReportContext ctx)
    {
        var sb = new StringBuilder();
        AppendReportMetadataHeader(sb, "GitGavel Test Quality & Coverage Report", ctx);

        var testFindings = ctx.Orchestration.AllFindings
            .Where(f => string.Equals(f.Category, "Testing", StringComparison.OrdinalIgnoreCase) ||
                        (f.AgentName != null && f.AgentName.Contains("TestSentinel", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var unitCount = testFindings.Count(f => string.Equals(f.TestTier, "Unit", StringComparison.OrdinalIgnoreCase));
        var integrationCount = testFindings.Count(f => string.Equals(f.TestTier, "Integration", StringComparison.OrdinalIgnoreCase));
        var e2eCount = testFindings.Count(f => string.Equals(f.TestTier, "E2E", StringComparison.OrdinalIgnoreCase));
        var unclassifiedCount = testFindings.Count - (unitCount + integrationCount + e2eCount);

        // 1. Test Pyramid Health Overview
        sb.AppendLine("## 1. Test Pyramid Health Overview");
        sb.AppendLine();
        sb.AppendLine("| Metric / Tier | Count | Notes |");
        sb.AppendLine("| :--- | :--- | :--- |");
        sb.AppendLine($"| **Discovered Test Files** | {ctx.RepoContext.TestFiles.Count} files | `{ctx.RepoContext.TotalTestLinesOfCode:N0}` test LOC |");
        sb.AppendLine($"| **Unit Tier Findings** | {unitCount + unclassifiedCount} | Fast, isolated class & method verification |");
        sb.AppendLine($"| **Integration Tier Findings** | {integrationCount} | Service boundary, database, and contract verification |");
        sb.AppendLine($"| **End-to-End (E2E) Tier Findings** | {e2eCount} | Full pipeline / CLI / user journey verification |");
        sb.AppendLine($"| **Total Test Sentinel Findings** | **{testFindings.Count}** | Audited across configured tiers: `{string.Join(", ", ctx.Config.Subagents.Tests.Tiers)}` |");
        sb.AppendLine();

        // Partition findings into Missing Tests, Extra Scenarios, and Test Quality
        var missingTests = testFindings
            .Where(f => string.Equals(f.TestDefectType, "MissingTest", StringComparison.OrdinalIgnoreCase) ||
                        (string.IsNullOrWhiteSpace(f.TestDefectType) && f.Title.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var extraScenarios = testFindings
            .Where(f => string.Equals(f.TestDefectType, "ExtraScenario", StringComparison.OrdinalIgnoreCase) ||
                        (string.IsNullOrWhiteSpace(f.TestDefectType) && !missingTests.Contains(f) &&
                         (f.Title.Contains("Scenario", StringComparison.OrdinalIgnoreCase) || f.Title.Contains("Boundary", StringComparison.OrdinalIgnoreCase) || f.Title.Contains("Edge", StringComparison.OrdinalIgnoreCase))))
            .ToList();

        var qualityFindings = testFindings
            .Where(f => !missingTests.Contains(f) && !extraScenarios.Contains(f))
            .ToList();

        // 2. Missing Tests Catalog
        sb.AppendLine($"## 2. Missing Tests Catalog ({missingTests.Count} items)");
        sb.AppendLine();
        if (missingTests.Count == 0)
        {
            sb.AppendLine("✅ **No critical missing test suites detected.**");
            sb.AppendLine();
        }
        else
        {
            AppendFindingsList(sb, "2", missingTests);
        }

        // 3. Extra Scenario Blueprint
        sb.AppendLine($"## 3. Extra Scenario & Boundary Blueprint ({extraScenarios.Count} items)");
        sb.AppendLine();
        if (extraScenarios.Count == 0)
        {
            sb.AppendLine("✅ **No omitted boundary or edge-case scenarios flagged.**");
            sb.AppendLine();
        }
        else
        {
            AppendFindingsList(sb, "3", extraScenarios);
        }

        // 4. Test Suite Quality & Flakiness
        sb.AppendLine($"## 4. Test Suite Quality & Reliability ({qualityFindings.Count} items)");
        sb.AppendLine();
        if (qualityFindings.Count == 0)
        {
            sb.AppendLine("✅ **No test flakiness or maintainability defects flagged.**");
            sb.AppendLine();
        }
        else
        {
            AppendFindingsList(sb, "4", qualityFindings);
        }

        return sb.ToString();
    }

    private static void AppendFindingsList(StringBuilder sb, string sectionPrefix, List<FindingCandidate> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var f = items[i];
            var tierBadge = string.IsNullOrWhiteSpace(f.TestTier) ? "Unit" : f.TestTier;
            sb.AppendLine($"### {sectionPrefix}.{i + 1} [{f.Severity} | {tierBadge}] {f.Title}");
            sb.AppendLine($"- **Target File**: `{f.FilePath}` (lines {f.StartLine}–{f.EndLine})");
            sb.AppendLine($"- **Test Tier**: `{tierBadge}`");
            sb.AppendLine();
            sb.AppendLine($"**Gap / Defect Summary**: {f.Summary}");
            if (!string.IsNullOrWhiteSpace(f.Details))
            {
                sb.AppendLine();
                sb.AppendLine($"**Context**: {f.Details}");
            }
            sb.AppendLine();
            sb.AppendLine("**Test Implementation Blueprint:**");
            sb.AppendLine("```csharp");
            sb.AppendLine(f.SuggestedRemediation);
            sb.AppendLine("```");
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
}
