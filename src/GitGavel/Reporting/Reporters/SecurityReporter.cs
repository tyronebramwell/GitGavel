using System.Text;

using GitGavel.Configuration;

namespace GitGavel.Reporting.Reporters;

public class SecurityReporter : BaseMarkdownReporter
{
    public override string ReportName => "Security";
    protected override string FileName => "3_security-report.md";
    protected override bool IsEnabled(ReportsSelection reports) => reports.Security;
    protected override string GenerateReportMarkdown(ReportContext context) => Generate(context);

    public static string Generate(ReportContext ctx)
    {
        var sb = new StringBuilder();
        AppendReportMetadataHeader(sb, "GitGavel Security Audit Report", ctx);

        // Filter security findings from all findings
        var securityFindings = ctx.Orchestration.AllFindings
            .Where(f => string.Equals(f.Category, "Security", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var criticalCount = securityFindings.Count(f => f.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        var highCount = securityFindings.Count(f => f.Severity.Equals("High", StringComparison.OrdinalIgnoreCase));
        var mediumCount = securityFindings.Count(f => f.Severity.Equals("Medium", StringComparison.OrdinalIgnoreCase));
        var lowCount = securityFindings.Count(f => f.Severity.Equals("Low", StringComparison.OrdinalIgnoreCase));

        // 1. Executive Summary
        sb.AppendLine("## 1. Executive Security Posture");
        sb.AppendLine();
        sb.AppendLine("| Severity | Count | Status |");
        sb.AppendLine("| :--- | :--- | :--- |");
        sb.AppendLine($"| 🔴 **Critical** | **{criticalCount}** | {(criticalCount > 0 ? "⚠️ Immediate Remediation Required" : "✅ Clear")} |");
        sb.AppendLine($"| 🟠 **High** | **{highCount}** | {(highCount > 0 ? "Action Required" : "✅ Clear")} |");
        sb.AppendLine($"| 🟡 **Medium** | **{mediumCount}** | Backlog Priority |");
        sb.AppendLine($"| ⚪ **Low** | **{lowCount}** | Informational |");
        sb.AppendLine($"| 🔍 **SARIF Alerts Ingested** | **{ctx.RepoContext.SarifFindings.Count}** | Static scanner correlations |");
        sb.AppendLine();

        // 2. Vulnerability Breakdown
        sb.AppendLine("## 2. Identified Vulnerabilities");
        sb.AppendLine();

        if (securityFindings.Count == 0)
        {
            sb.AppendLine("✅ **No security vulnerabilities identified in audited files.**");
            return sb.ToString();
        }

        for (var i = 0; i < securityFindings.Count; i++)
        {
            var f = securityFindings[i];
            var owasp = string.IsNullOrWhiteSpace(f.OwaspCategory) ? "General Security Flaw" : f.OwaspCategory;
            var attackVector = string.IsNullOrWhiteSpace(f.AttackVector) ? "Adversary exploiting unauthorized input/state" : f.AttackVector;

            sb.AppendLine($"### 2.{i + 1} [{f.Severity}] {f.Title}");
            sb.AppendLine();
            sb.AppendLine($"- **File**: `{f.FilePath}` (lines {f.StartLine}–{f.EndLine})");
            sb.AppendLine($"- **OWASP Classification**: `{owasp}`");
            sb.AppendLine($"- **Fingerprint**: `{f.Fingerprint}`");
            sb.AppendLine();
            sb.AppendLine("#### ⚠️ Problem Summary");
            sb.AppendLine(f.Summary);
            sb.AppendLine();
            sb.AppendLine("#### 🎯 Attack / Exploit Vector");
            sb.AppendLine(attackVector);
            sb.AppendLine();
            sb.AppendLine("#### 💡 Verified Remediation");
            sb.AppendLine("```csharp");
            sb.AppendLine(f.SuggestedRemediation);
            sb.AppendLine("```");
            sb.AppendLine();

            if (f.References.Count > 0)
            {
                sb.AppendLine("#### 📚 Learning & Research References");
                foreach (var r in f.References)
                {
                    sb.AppendLine($"- {r}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
