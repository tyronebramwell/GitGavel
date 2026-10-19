using System.Text;
using System.Text.RegularExpressions;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.GitHub;

public record GitGavelIssueMetadata(
    string Fingerprint,
    string Category,
    double JevScore,
    double JevConfidence,
    string Agent,
    DateTime CreatedAtUtc);

public static partial class IssueFormatter
{
    [GeneratedRegex(@"<!--\s*gitgavel-metadata\s*([\s\S]*?)-->", RegexOptions.Compiled)]
    private static partial Regex MetadataCommentRegex();

    public static string FormatIssueBody(TriageDecision decision)
    {
        var f = decision.Finding;
        var sb = new StringBuilder();

        sb.AppendLine("## ⚠️ Summary");
        sb.AppendLine(f.Summary);
        sb.AppendLine();

        sb.AppendLine("## 📍 Details");
        sb.AppendLine($"- **File**: `{f.FilePath}` (lines {f.StartLine}–{f.EndLine})");
        sb.AppendLine($"- **Category**: {f.Category}");
        sb.AppendLine($"- **Severity**: {f.Severity}");
        sb.AppendLine($"- **Jev Urgency Score**: {decision.JevScore:F1} / 5.0 ({decision.PriorityLabel})");
        sb.AppendLine($"- **Triage Confidence**: {decision.Confidence:P0}");
        if (!string.IsNullOrWhiteSpace(f.OwaspCategory))
        {
            sb.AppendLine($"- **OWASP Classification**: `{f.OwaspCategory}`");
        }
        if (!string.IsNullOrWhiteSpace(f.AttackVector))
        {
            sb.AppendLine($"- **Attack Vector**: {f.AttackVector}");
        }
        sb.AppendLine();

        sb.AppendLine("## 💡 Suggested Remediation");
        sb.AppendLine("```csharp");
        sb.AppendLine(f.SuggestedRemediation);
        sb.AppendLine("```");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(f.Details))
        {
            sb.AppendLine("## 🔍 Context & Details");
            sb.AppendLine(f.Details);
            sb.AppendLine();
        }

        if (f.References.Count > 0)
        {
            sb.AppendLine("## 📚 Learning & Research References");
            foreach (var r in f.References)
            {
                sb.AppendLine($"- {r}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("<!-- gitgavel-metadata");
        sb.AppendLine($"fingerprint: {f.Fingerprint}");
        sb.AppendLine($"category: {f.Category}");
        sb.AppendLine($"jev_score: {decision.JevScore:F1}");
        sb.AppendLine($"jev_confidence: {decision.Confidence:F2}");
        sb.AppendLine($"agent: {f.AgentName}");
        sb.AppendLine($"created_at: {DateTime.UtcNow:O}");
        sb.AppendLine("-->");

        return sb.ToString();
    }

    public static GitGavelIssueMetadata? ParseMetadata(string issueBody)
    {
        if (string.IsNullOrWhiteSpace(issueBody)) return null;

        var match = MetadataCommentRegex().Match(issueBody);
        if (!match.Success) return null;

        var yaml = match.Groups[1].Value;
        var lines = yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? fingerprint = null;
        string? category = null;
        var score = 3.0;
        var confidence = 0.85;
        string? agent = null;
        var createdAt = DateTime.UtcNow;

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            var key = parts[0].ToLowerInvariant();
            var val = parts[1];

            switch (key)
            {
                case "fingerprint":
                    fingerprint = val;
                    break;
                case "category":
                    category = val;
                    break;
                case "jev_score":
                    if (double.TryParse(val, out var s)) score = s;
                    break;
                case "jev_confidence":
                    if (double.TryParse(val, out var c)) confidence = c;
                    break;
                case "agent":
                    agent = val;
                    break;
                case "created_at":
                    if (DateTime.TryParse(val, out var dt)) createdAt = dt;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        return new GitGavelIssueMetadata(
            fingerprint,
            category,
            score,
            confidence,
            agent ?? "Unknown",
            createdAt);
    }
}
