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
    [GeneratedRegex(@"<!--\s*gitgavel-metadata\s*([\s\S]*?)-->", RegexOptions.Compiled)]
    private static partial Regex MetadataCommentRegex();

    [GeneratedRegex(@"<!--\s*gitgavel-resolution\s*([\s\S]*?)-->", RegexOptions.Compiled)]
    private static partial Regex ResolutionCommentRegex();

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

    public static string FormatIssueResolutionComment(IssueResolutionDecision decision)
    {
        var c = decision.Candidate;
        var sb = new StringBuilder();

        sb.AppendLine("## ✅ Issue Resolved");
        sb.AppendLine();
        sb.AppendLine("GitGavel verified that this issue has been completed in the codebase and is automatically closing it.");
        sb.AppendLine();
        sb.AppendLine("### 🛠️ How It Was Fixed");
        sb.AppendLine(c.HowItWasFixed);
        sb.AppendLine();

        sb.AppendLine("### 🔗 Resolving Commits");
        if (c.ResolvingCommitSummaries.Count > 0)
        {
            foreach (var summary in c.ResolvingCommitSummaries)
            {
                sb.AppendLine($"- {summary}");
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
        sb.AppendLine($"jev_resolution_confidence: {decision.ResolutionProbability:F2}");
        sb.AppendLine($"agent: {c.AgentName}");
        sb.AppendLine($"closed_at: {DateTime.UtcNow:O}");
        sb.AppendLine("-->");

        return sb.ToString();
    }

    public static GitGavelResolutionMetadata? ParseResolutionMetadata(string commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody)) return null;

        var match = ResolutionCommentRegex().Match(commentBody);
        if (!match.Success) return null;

        var yaml = match.Groups[1].Value;
        var lines = yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var commits = new List<string>();
        var confidence = 0.90;
        string? agent = null;
        var closedAt = DateTime.UtcNow;

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            var key = parts[0].ToLowerInvariant();
            var val = parts[1];

            switch (key)
            {
                case "resolved_by_commits":
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<string>>(val);
                        if (parsed != null) commits = parsed;
                    }
                    catch
                    {
                        // Ignore malformed list
                    }
                    break;
                case "jev_resolution_confidence":
                    if (double.TryParse(val, out var conf)) confidence = conf;
                    break;
                case "agent":
                    agent = val;
                    break;
                case "closed_at":
                    if (DateTime.TryParse(val, out var dt)) closedAt = dt;
                    break;
            }
        }

        return new GitGavelResolutionMetadata(
            commits,
            confidence,
            agent ?? "IssueResolutionAuditor",
            closedAt);
    }

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
        sb.AppendLine($"reviewed_at: {DateTime.UtcNow:O}");
        sb.AppendLine("-->");

        return sb.ToString();
    }

    public static string? ExtractStickyCommentMarker(string? commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody)) return null;
        const string prefix = "<!-- gitgavel-sticky-comment:";
        var idx = commentBody.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var endIdx = commentBody.IndexOf("-->", idx, StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0) return null;
        return commentBody[idx..(endIdx + 3)].Trim();
    }
}
