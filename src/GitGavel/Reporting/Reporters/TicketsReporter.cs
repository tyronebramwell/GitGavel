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

        // 5. Completed & Closed Issues (Phase 7)
        var closedResolutions = ctx.IssueResolutionDecisions
            .Where(d => d.Action == IssueResolutionAction.CloseResolved)
            .ToList();

        sb.AppendLine($"## 5. Completed & Automatically Closed Issues ({closedResolutions.Count})");
        sb.AppendLine();
        if (closedResolutions.Count == 0)
        {
            sb.AppendLine("_No open issues were verified as resolved during this audit run._");
        }
        else
        {
            sb.AppendLine("| Issue # | Title | How It Was Fixed | Resolving Commits | Jev Confidence | Target Column |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- |");
            foreach (var r in closedResolutions)
            {
                var title = EscapeCell(r.Candidate.IssueTitle);
                var how = EscapeCell(r.Candidate.HowItWasFixed);
                var commits = r.Candidate.ResolvingCommitSummaries.Count > 0
                    ? EscapeCell(string.Join("; ", r.Candidate.ResolvingCommitSummaries))
                    : EscapeCell(string.Join(", ", r.Candidate.ResolvingCommitShas.Select(s => s.Length > 7 ? $"`{s[..7]}`" : $"`{s}`")));
                sb.AppendLine($"| #{r.Candidate.IssueNumber} | {title} | {how} | {commits} | **{r.ResolutionProbability:P0}** | `{r.TargetColumn}` |");
            }
        }
        sb.AppendLine();

        // 6. Pull Request Review Actions (Phase 6)
        if (ctx.Orchestration.PrReviewAudit != null || ctx.PrCommentDecisions.Count > 0)
        {
            var prNum = ctx.ActivePullRequestNumber ?? ctx.Orchestration.PrReviewAudit?.PrNumber ?? 0;
            sb.AppendLine($"## 6. Pull Request Review Actions (PR #{prNum})");
            sb.AppendLine();
            if (ctx.PrCommentDecisions.Count == 0)
            {
                sb.AppendLine("_No inline PR review comments generated._");
            }
            else
            {
                sb.AppendLine("| # | Priority | Category | File & Line | Title | Action | Jev Score |");
                sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
                for (var i = 0; i < ctx.PrCommentDecisions.Count; i++)
                {
                    var p = ctx.PrCommentDecisions[i];
                    var cat = EscapeCell(p.Candidate.Category);
                    var title = EscapeCell(p.Candidate.Title);
                    var file = EscapeCell(p.Candidate.FilePath);
                    sb.AppendLine($"| {i + 1} | **{p.PriorityLabel}** | `{cat}` | `{file}:{p.Candidate.Line}` | {title} | `{p.Action}` | {p.JevScore:F1}/5.0 |");
                }
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
            activePullRequest = ctx.ActivePullRequestNumber,
            durationSeconds = ctx.Duration.TotalSeconds,
            statistics = new
            {
                totalEvaluated = ctx.TriageDecisions.Count,
                acceptedToBacklog = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.AcceptedToBacklog),
                routedToReview = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.RoutedToHumanReview),
                suppressedDuplicates = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.SuppressedDuplicate),
                discardedNoise = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.DiscardedNoise),
                closedResolvedIssues = ctx.IssueResolutionDecisions.Count(d => d.Action == IssueResolutionAction.CloseResolved),
                prInlineComments = ctx.PrCommentDecisions.Count(d => d.Action == PrCommentAction.PostInlineReview)
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
                    suggestedRemediation = d.Finding.SuggestedRemediation,
                    references = d.Finding.References
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
                    duplicateProbability = d.DuplicateProbability,
                    references = d.Finding.References
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
                }),
            closedIssues = ctx.IssueResolutionDecisions
                .Where(d => d.Action == IssueResolutionAction.CloseResolved)
                .Select(d => new
                {
                    issueNumber = d.Candidate.IssueNumber,
                    title = d.Candidate.IssueTitle,
                    howItWasFixed = d.Candidate.HowItWasFixed,
                    resolvingCommitShas = d.Candidate.ResolvingCommitShas,
                    resolvingCommitSummaries = d.Candidate.ResolvingCommitSummaries,
                    jevResolutionConfidence = d.ResolutionProbability,
                    targetColumn = d.TargetColumn
                }),
            prReviewComments = ctx.PrCommentDecisions
                .Select(d => new
                {
                    filePath = d.Candidate.FilePath,
                    line = d.Candidate.Line,
                    category = d.Candidate.Category,
                    priority = d.PriorityLabel,
                    title = d.Candidate.Title,
                    body = d.Candidate.Body,
                    action = d.Action.ToString(),
                    jevScore = d.JevScore
                })
        };

        return JsonSerializer.Serialize(jsonObj, JsonOpts);
    }
}
