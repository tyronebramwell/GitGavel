using System.Text;
using System.Text.Json;
using GitGavel.System1.Models;

using GitGavel.Configuration;

namespace GitGavel.Reporting.Reporters;

public class TicketsReporter : BaseMarkdownReporter
{
    public override string ReportName => "Tickets";
    protected override string FileName => "2_tickets-report.md";
    protected override bool IsEnabled(ReportsSelection reports) => reports.Tickets;
    protected override string GenerateReportMarkdown(ReportContext context) => Generate(context);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public override async Task<IEnumerable<string>> GenerateReportsAsync(ReportContext context, string outputDirectory, CancellationToken ct = default)
    {
        var files = (await base.GenerateReportsAsync(context, outputDirectory, ct)).ToList();

        var repConfig = context.Config.Reporting;
        if (IsEnabled(repConfig.Reports) && repConfig.Formats.Any(f => f.Equals("json", StringComparison.OrdinalIgnoreCase)))
        {
            var json = GenerateJson(context);
            var path = await WriteReportFileAsync(outputDirectory, "2_tickets-report.json", json, ct);
            files.Add(path);
        }

        return files;
    }

    public static string GenerateMarkdown(ReportContext ctx) => Generate(ctx);

    public static string Generate(ReportContext ctx)
    {
        var sb = new StringBuilder();
        AppendReportMetadataHeader(sb, "GitGavel Tickets Report", ctx);

        // 1. Created / Accepted Issues
        var accepted = ctx.TriageDecisions.Where(d => d.Status == TriageStatus.AcceptedToBacklog).ToList();
        sb.AppendLine($"## 1. Backlog Issues ({accepted.Count})");
        sb.AppendLine();

        if (accepted.Count == 0)
        {
            sb.AppendLine("_No new issues accepted directly to the project backlog in this audit run._");
        }
        else
        {
            sb.AppendLine("| # | Priority | Category | Title | File & Location | Jev Score | Target Column |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

            for (var i = 0; i < accepted.Count; i++)
            {
                var d = accepted[i];
                var cat = EscapeCell(d.Finding.Category);
                var title = EscapeCell(d.Finding.Title);
                var file = EscapeCell(d.Finding.FilePath);
                sb.AppendLine($"| {i + 1} | **{d.PriorityLabel}** | `{cat}` | {title} | `{file}:{d.Finding.StartLine}` | {d.JevScore:F1}/5.0 | `{d.TargetColumn}` |");
            }
        }
        sb.AppendLine();

        // 2. Issues Routed to Human Review / AI Triage
        var review = ctx.TriageDecisions.Where(d => d.Status == TriageStatus.RoutedToHumanReview).ToList();
        sb.AppendLine($"## 2. Issues Routed to AI Triage Review ({review.Count})");
        sb.AppendLine();

        if (review.Count == 0)
        {
            sb.AppendLine("_No borderline issues required maintainer escalation._");
        }
        else
        {
            sb.AppendLine("| # | Priority | Category | Title | File & Location | Reason | Potential Match |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

            for (var i = 0; i < review.Count; i++)
            {
                var d = review[i];
                var cat = EscapeCell(d.Finding.Category);
                var title = EscapeCell(d.Finding.Title);
                var file = EscapeCell(d.Finding.FilePath);
                var reason = EscapeCell(d.Reason ?? "");
                var match = d.DuplicateOfIssueNumber.HasValue ? $"#{d.DuplicateOfIssueNumber} (P={d.DuplicateProbability:P0})" : "N/A";
                sb.AppendLine($"| {i + 1} | **{d.PriorityLabel}** | `{cat}` | {title} | `{file}:{d.Finding.StartLine}` | {reason} | {match} |");
            }
        }
        sb.AppendLine();

        // 3. Suppressed Duplicate Findings (Jev Noul)
        var duplicates = ctx.TriageDecisions.Where(d => d.Status == TriageStatus.SuppressedDuplicate).ToList();
        sb.AppendLine($"## 3. Suppressed Duplicate Findings ({duplicates.Count})");
        sb.AppendLine();

        if (duplicates.Count == 0)
        {
            sb.AppendLine("_No duplicate findings suppressed during this run._");
        }
        else
        {
            sb.AppendLine("| # | Category | Candidate Title | Matched Issue | Noul Duplicate Probability | Action |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- |");

            for (var i = 0; i < duplicates.Count; i++)
            {
                var d = duplicates[i];
                var cat = EscapeCell(d.Finding.Category);
                var title = EscapeCell(d.Finding.Title);
                sb.AppendLine($"| {i + 1} | `{cat}` | {title} | #{d.DuplicateOfIssueNumber} | **{d.DuplicateProbability:P0}** | Suppressed (Creation Skipped) |");
            }
        }
        sb.AppendLine();

        // 4. Discarded Noise
        var noise = ctx.TriageDecisions.Where(d => d.Status == TriageStatus.DiscardedNoise).ToList();
        if (noise.Count > 0)
        {
            sb.AppendLine($"## 4. Discarded Low-Value Noise ({noise.Count})");
            sb.AppendLine();
            sb.AppendLine("| # | Category | Title | Reason |");
            sb.AppendLine("| :--- | :--- | :--- | :--- |");
            for (var i = 0; i < noise.Count; i++)
            {
                var d = noise[i];
                sb.AppendLine($"| {i + 1} | `{d.Finding.Category}` | {d.Finding.Title} | {d.Reason} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string GenerateJson(ReportContext ctx)
    {
        var jsonObj = new
        {
            timestamp = ctx.EndTimeUtc.ToString("o"),
            commitSha = ctx.GitCommitSha,
            branch = ctx.GitBranch,
            durationSeconds = ctx.Duration.TotalSeconds,
            statistics = new
            {
                totalEvaluated = ctx.TriageDecisions.Count,
                acceptedToBacklog = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.AcceptedToBacklog),
                routedToReview = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.RoutedToHumanReview),
                suppressedDuplicates = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.SuppressedDuplicate),
                discardedNoise = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.DiscardedNoise)
            },
            backlogIssues = ctx.TriageDecisions
                .Where(d => d.Status == TriageStatus.AcceptedToBacklog)
                .Select(d => new
                {
                    title = d.Finding.Title,
                    category = d.Finding.Category,
                    priority = d.PriorityLabel,
                    jevScore = d.JevScore,
                    filePath = d.Finding.FilePath,
                    startLine = d.Finding.StartLine,
                    endLine = d.Finding.EndLine,
                    targetColumn = d.TargetColumn,
                    fingerprint = d.Finding.Fingerprint,
                    suggestedRemediation = d.Finding.SuggestedRemediation
                }),
            reviewIssues = ctx.TriageDecisions
                .Where(d => d.Status == TriageStatus.RoutedToHumanReview)
                .Select(d => new
                {
                    title = d.Finding.Title,
                    category = d.Finding.Category,
                    priority = d.PriorityLabel,
                    jevScore = d.JevScore,
                    filePath = d.Finding.FilePath,
                    startLine = d.Finding.StartLine,
                    reason = d.Reason,
                    matchedIssueNumber = d.DuplicateOfIssueNumber,
                    duplicateProbability = d.DuplicateProbability
                }),
            suppressedDuplicates = ctx.TriageDecisions
                .Where(d => d.Status == TriageStatus.SuppressedDuplicate)
                .Select(d => new
                {
                    title = d.Finding.Title,
                    category = d.Finding.Category,
                    matchedIssueNumber = d.DuplicateOfIssueNumber,
                    noulProbability = d.DuplicateProbability,
                    filePath = d.Finding.FilePath
                })
        };

        return JsonSerializer.Serialize(jsonObj, JsonOpts);
    }
}
