using System.Globalization;
using System.Text;
using System.Text.Json;
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

public record GitGavelResolutionMetadata(
    List<string> ResolvedByCommits,
    double JevResolutionConfidence,
    string Agent,
    DateTime ClosedAtUtc);

public static partial class IssueFormatter
{
    [GeneratedRegex(@"<\s*(script|iframe|object|embed|form|input|button|svg|math|base|link|meta)\b[^>]*>[\s\S]*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DangerousHtmlBlockRegex();

    [GeneratedRegex(@"<\s*(script|iframe|object|embed|form|input|button|svg|math|base|link|meta)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DangerousHtmlSelfClosingRegex();

    [GeneratedRegex(@"\bon\w+\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DangerousAttributeRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex MarkdownLinkRegex();

    public static string SanitizeContent(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. Prevent comment delimiter breakout/spoofing of gitgavel metadata comment blocks
        var text = input.Replace("<!--", "&lt;!--").Replace("-->", "--&gt;");

        // 2. Strip dangerous script/iframe/object blocks and tags
        text = DangerousHtmlBlockRegex().Replace(text, string.Empty);
        text = DangerousHtmlSelfClosingRegex().Replace(text, string.Empty);

        // 3. Remove inline event handlers (onload, onerror, onclick, etc.)
        text = DangerousAttributeRegex().Replace(text, string.Empty);

        return text;
    }

    public static string SanitizeReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;

        var sanitized = SanitizeContent(reference);

        // Disallow dangerous pseudo-protocols like javascript:, data:, vbscript: while allowing relative and anchor links
        return MarkdownLinkRegex().Replace(sanitized, match =>
        {
            var title = match.Groups[1].Value;
            var url = match.Groups[2].Value.Trim();

            if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
            {
                return $"[{title}](#)";
            }

            return $"[{title}]({url})";
        });
    }

    public static string FormatIssueBody(TriageDecision decision)
    {
        var f = decision.Finding;
        var sb = new StringBuilder();

        sb.AppendLine("## ⚠️ Summary");
        sb.AppendLine(SanitizeContent(f.Summary));
        sb.AppendLine();

        sb.AppendLine("## 📍 Details");
        sb.AppendLine($"- **File**: `{SanitizeContent(f.FilePath)}` (lines {f.StartLine}–{f.EndLine})");
        sb.AppendLine($"- **Category**: {SanitizeContent(f.Category)}");
        sb.AppendLine($"- **Severity**: {SanitizeContent(f.Severity)}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Jev Urgency Score**: {decision.JevScore:F1} / 5.0 ({decision.PriorityLabel})"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Triage Confidence**: {decision.Confidence:P0}"));
        if (!string.IsNullOrWhiteSpace(f.OwaspCategory))
        {
            sb.AppendLine($"- **OWASP Classification**: `{SanitizeContent(f.OwaspCategory)}`");
        }
        if (!string.IsNullOrWhiteSpace(f.AttackVector))
        {
            sb.AppendLine($"- **Attack Vector**: {SanitizeContent(f.AttackVector)}");
        }
        sb.AppendLine();

        sb.AppendLine("## 💡 Suggested Remediation");
        sb.AppendLine("```csharp");
        sb.AppendLine(SanitizeContent(f.SuggestedRemediation));
        sb.AppendLine("```");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(f.Details))
        {
            sb.AppendLine("## 🔍 Context & Details");
            sb.AppendLine(SanitizeContent(f.Details));
            sb.AppendLine();
        }

        if (f.References.Count > 0)
        {
            sb.AppendLine("## 📚 Learning & Research References");
            foreach (var r in f.References)
            {
                sb.AppendLine($"- {SanitizeReference(r)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("<!-- gitgavel-metadata");
        sb.AppendLine($"fingerprint: {f.Fingerprint}");
        sb.AppendLine($"category: {f.Category}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"jev_score: {decision.JevScore:F1}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"jev_confidence: {decision.Confidence:F2}"));
        sb.AppendLine($"agent: {f.AgentName}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"created_at: {DateTime.UtcNow:O}"));
        sb.AppendLine("-->");

        return sb.ToString();
    }

    public static GitGavelIssueMetadata? ParseMetadata(string issueBody) =>
        IssueMetadataParser.ParseMetadata(issueBody);

    public static string FormatIssueResolutionComment(IssueResolutionDecision decision)
    {
        var c = decision.Candidate;
        var sb = new StringBuilder();

        sb.AppendLine("## ✅ Issue Resolved");
        sb.AppendLine();
        sb.AppendLine("GitGavel verified that this issue has been completed in the codebase and is automatically closing it.");
        sb.AppendLine();
        sb.AppendLine("### 🛠️ How It Was Fixed");
        sb.AppendLine(SanitizeContent(c.HowItWasFixed));
        sb.AppendLine();

        sb.AppendLine("### 🔗 Resolving Commits");
        if (c.ResolvingCommitSummaries.Count > 0)
        {
            foreach (var summary in c.ResolvingCommitSummaries)
            {
                sb.AppendLine($"- {SanitizeContent(summary)}");
            }
        }
        else if (c.ResolvingCommitShas.Count > 0)
        {
            foreach (var sha in c.ResolvingCommitShas)
            {
                var shortSha = sha.Length > 7 ? sha[..7] : sha;
                sb.AppendLine($"- `{shortSha}`");
            }
        }
        else
        {
            sb.AppendLine("- _Verified against current repository state_");
        }
        sb.AppendLine();

        var commitsJson = JsonSerializer.Serialize(c.ResolvingCommitShas);
        sb.AppendLine("---");
        sb.AppendLine("<!-- gitgavel-resolution");
        sb.AppendLine($"resolved_by_commits: {commitsJson}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"jev_resolution_confidence: {decision.ResolutionProbability:F2}"));
        sb.AppendLine($"agent: {c.AgentName}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"closed_at: {DateTime.UtcNow:O}"));
        sb.AppendLine("-->");

        return sb.ToString();
    }

    public static GitGavelResolutionMetadata? ParseResolutionMetadata(string commentBody) =>
        IssueMetadataParser.ParseResolutionMetadata(commentBody);

    public static string FormatInlinePrComment(PrCommentTriageDecision decision)
    {
        var f = decision.Finding;
        var sb = new StringBuilder();
        sb.AppendLine($"**[{decision.PriorityLabel} | {f.Category}] {f.Title}**");
        sb.AppendLine();
        sb.AppendLine(f.Summary);

        if (!string.IsNullOrWhiteSpace(f.SuggestedRemediation))
        {
            sb.AppendLine();
            sb.AppendLine("```suggestion");
            sb.AppendLine(f.SuggestedRemediation);
            sb.AppendLine("```");
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatPrReviewSummary(
        PullRequestContext prContext,
        IReadOnlyList<PrCommentTriageDecision> decisions)
    {
        var sb = new StringBuilder();
        var activeDecisions = decisions.Where(d => d.Action != PrCommentAction.Discard).ToList();
        var inlinePosted = decisions.Count(d => d.Action == PrCommentAction.PostInlineReview);
        var duplicatesSuppressed = decisions.Count(d => d.IsDuplicate);

        int CountDomain(string cat, bool inlineOnly) =>
            (inlineOnly ? decisions.Where(d => d.Action == PrCommentAction.PostInlineReview) : activeDecisions)
            .Count(d => string.Equals(d.Finding.Category, cat, StringComparison.OrdinalIgnoreCase));

        sb.AppendLine("## ⚖️ GitGavel Pull Request Audit Summary");
        sb.AppendLine();
        sb.AppendLine("| Domain | Findings | Inline Comments | Prior Threads Deduped |");
        sb.AppendLine("| :--- | :---: | :---: | :---: |");
        sb.AppendLine($"| 🛡️ Security | {CountDomain("Security", false)} | {CountDomain("Security", true)} | {(duplicatesSuppressed > 0 ? $"{duplicatesSuppressed} suppressed" : "—")} |");
        sb.AppendLine($"| 📋 Spec Alignment | {CountDomain("SpecDrift", false)} | {CountDomain("SpecDrift", true)} | — |");
        sb.AppendLine($"| 🐛 Reliability | {CountDomain("Reliability", false)} | {CountDomain("Reliability", true)} | — |");
        sb.AppendLine($"| ⚙️ Architecture | {CountDomain("Refactoring", false)} | {CountDomain("Refactoring", true)} | — |");
        sb.AppendLine($"| 🧪 Test Coverage | {CountDomain("Testing", false)} | {CountDomain("Testing", true)} | — |");
        sb.AppendLine();

        sb.AppendLine("### 🔍 Key Observations & Existing Review Audit");
        sb.AppendLine($"- **Changed Files Audited**: Inspected {prContext.ChangedFiles.Count} file(s) modified in PR #{prContext.PrNumber} (`{prContext.HeadRef}` → `{prContext.BaseRef}`).");

        if (prContext.ExistingReviewComments.Count > 0)
        {
            sb.AppendLine($"- **Existing Review Threads**: Audited {prContext.ExistingReviewComments.Count} existing review comment(s) ({duplicatesSuppressed} duplicate finding(s) suppressed).");
        }

        var summaryOnlyItems = decisions.Where(d => d.Action == PrCommentAction.SummaryCommentOnly).ToList();
        foreach (var item in summaryOnlyItems)
        {
            sb.AppendLine($"- **[{item.PriorityLabel} | {item.Finding.Category}] `{item.Finding.FilePath}:{item.Finding.StartLine}` — {item.Finding.Title}**: {item.Finding.Summary}");
        }

        if (inlinePosted > 0)
        {
            sb.AppendLine($"- **Inline Diff Comments**: Posted {inlinePosted} inline comment(s) directly on the Pull Request diff.");
        }
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine("<!-- gitgavel-sticky-comment: pr-summary -->");
        sb.AppendLine("<!-- gitgavel-pr-review");
        sb.AppendLine($"pr_number: {prContext.PrNumber}");
        sb.AppendLine($"head_sha: {prContext.HeadSha}");
        sb.AppendLine($"inline_comments_posted: {inlinePosted}");
        sb.AppendLine("agents: [SpecDrift, Security, Refactoring, TestSentinel]");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"reviewed_at: {DateTime.UtcNow:O}"));
        sb.AppendLine("-->");

        return sb.ToString();
    }

    public static string? ExtractStickyCommentMarker(string? commentBody) =>
        IssueMetadataParser.ExtractStickyCommentMarker(commentBody);
}
