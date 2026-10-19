using System.Text;
using GitGavel.System1.Models;

namespace GitGavel.Reporting.Reporters;

public static class StepSummaryWriter
{
    public static async Task WriteAsync(ReportContext ctx, string? customSummaryPath = null, CancellationToken ct = default)
    {
        var summaryPath = customSummaryPath ?? Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(summaryPath)) return;

        var sb = new StringBuilder();

        sb.AppendLine("# ⚖️ GitGavel Audit Summary");
        sb.AppendLine();
        sb.AppendLine($"> Audit completed in **{ctx.Duration.TotalSeconds:F2}s** for commit `{ctx.GitCommitSha}` on `{ctx.GitBranch}`");
        sb.AppendLine();

        // Triage Funnel
        var accepted = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.AcceptedToBacklog);
        var review = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.RoutedToHumanReview);
        var duplicates = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.SuppressedDuplicate);

        sb.AppendLine("### 📊 Triage Funnel Overview");
        sb.AppendLine();
        sb.AppendLine("| Backlog Items | Review Items | Suppressed Duplicates | Scanned Files | Total Tokens |");
        sb.AppendLine("| :---: | :---: | :---: | :---: | :---: |");
        sb.AppendLine($"| **{accepted}** | **{review}** | **{duplicates}** | {ctx.RepoContext.TotalFilesScanned} | {ctx.Orchestration.TotalTokens:N0} |");
        sb.AppendLine();

        // Top Accepted Backlog Tickets
        var acceptedItems = ctx.TriageDecisions.Where(d => d.Status == TriageStatus.AcceptedToBacklog).ToList();
        if (acceptedItems.Count > 0)
        {
            sb.AppendLine("### 🎯 Top Priority Backlog Tickets");
            sb.AppendLine();
            sb.AppendLine("| Priority | Category | Title | File | Jev Score |");
            sb.AppendLine("| :---: | :--- | :--- | :--- | :---: |");

            foreach (var item in acceptedItems)
            {
                var title = EscapeCell(item.Finding.Title);
                var cat = EscapeCell(item.Finding.Category);
                var file = EscapeCell(item.Finding.FilePath);
                sb.AppendLine($"| **{item.PriorityLabel}** | `{cat}` | {title} | `{file}:{item.Finding.StartLine}` | {item.JevScore:F1}/5.0 |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("> 💡 _All four detailed audit reports (`1_audit-run.md`, `2_tickets-report.md`, `3_security-report.md`, `4_code-quality-report.md`) are archived in the workflow artifacts._");
        sb.AppendLine();

        try
        {
            await File.AppendAllTextAsync(summaryPath, sb.ToString(), ct);
        }
        catch
        {
            // Do not crash the run if writing to step summary fails
        }
    }

    private static string EscapeCell(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
