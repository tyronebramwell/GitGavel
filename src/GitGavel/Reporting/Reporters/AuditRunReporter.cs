using System.Text;
using GitGavel.System1.Models;

using GitGavel.Configuration;

namespace GitGavel.Reporting.Reporters;

public class AuditRunReporter : BaseMarkdownReporter
{
    public override string ReportName => "AuditRun";
    protected override string FileName => "1_audit-run.md";
    protected override bool IsEnabled(ReportsSelection reports) => reports.AuditRun;
    protected override string GenerateReportMarkdown(ReportContext context) => Generate(context);

    public static string Generate(ReportContext ctx)
    {
        var sb = new StringBuilder();
        AppendReportMetadataHeader(sb, "GitGavel Audit Run Log", ctx);

        // 1. Execution Metrics
        sb.AppendLine("## 1. Execution Metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("| :--- | :--- |");
        sb.AppendLine($"| **Start Time (UTC)** | `{ctx.StartTimeUtc:yyyy-MM-dd HH:mm:ss}` |");
        sb.AppendLine($"| **End Time (UTC)** | `{ctx.EndTimeUtc:yyyy-MM-dd HH:mm:ss}` |");
        sb.AppendLine($"| **Total Duration** | `{ctx.Duration.TotalSeconds:F2} seconds` |");
        sb.AppendLine($"| **Git Commit SHA** | `{ctx.GitCommitSha}` |");
        sb.AppendLine($"| **Git Branch** | `{ctx.GitBranch}` |");
        if (ctx.ActivePullRequestNumber.HasValue)
        {
            sb.AppendLine($"| **Active Pull Request** | `#{ctx.ActivePullRequestNumber}` |");
        }
        sb.AppendLine();

        // 2. Scope Scanned
        sb.AppendLine("## 2. Scope Scanned");
        sb.AppendLine();
        sb.AppendLine("| Scope Category | Count / Volume |");
        sb.AppendLine("| :--- | :--- |");
        sb.AppendLine($"| **Specification Files** | {ctx.RepoContext.SpecFiles.Count} files |");
        sb.AppendLine($"| **Source Code Files** | {ctx.RepoContext.SourceFiles.Count} files |");
        sb.AppendLine($"| **Automated Test Files** | {ctx.RepoContext.TestFiles.Count} files ({ctx.RepoContext.TotalTestLinesOfCode:N0} LOC) |");
        sb.AppendLine($"| **Total Lines of Code** | {ctx.RepoContext.TotalLinesOfCode:N0} LOC |");
        sb.AppendLine($"| **Git Commits Inspected** | {ctx.RepoContext.RecentCommits.Count} commits |");
        sb.AppendLine($"| **SARIF Reports Ingested** | {ctx.RepoContext.SarifFindings.Count} findings |");
        sb.AppendLine();

        // 3. System 2 Agent Diagnostics
        sb.AppendLine("## 3. System 2 Generative Subagent Diagnostics");
        sb.AppendLine();
        sb.AppendLine("| Agent Name | Findings | Prompt Tokens | Completion Tokens | Total Tokens | Latency |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- |");

        foreach (var agent in ctx.Orchestration.SubagentResults)
        {
            var status = agent.IsSuccess ? $"{agent.Findings.Count} findings" : "❌ Error: " + agent.ErrorMessage;
            sb.AppendLine($"| **{agent.AgentName}** | {status} | {agent.PromptTokens:N0} | {agent.CompletionTokens:N0} | {agent.TotalTokens:N0} | {agent.DurationMs}ms |");
        }

        if (ctx.Orchestration.IssueResolution != null)
        {
            var ir = ctx.Orchestration.IssueResolution;
            var status = ir.IsSuccess ? $"{ir.Candidates.Count} resolved candidates" : "❌ Error: " + ir.ErrorMessage;
            sb.AppendLine($"| **IssueResolutionAuditor** | {status} | {ir.PromptTokens:N0} | {ir.CompletionTokens:N0} | {ir.TotalTokens:N0} | {ir.DurationMs}ms |");
        }

        sb.AppendLine($"| **TOTAL** | **{ctx.Orchestration.AllFindings.Count} findings** | **{ctx.Orchestration.TotalPromptTokens:N0}** | **{ctx.Orchestration.TotalCompletionTokens:N0}** | **{ctx.Orchestration.TotalTokens:N0}** | **{ctx.Orchestration.TotalDurationMs}ms** |");
        sb.AppendLine();

        // 4. System 1 Jev Triage Statistics
        var accepted = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.AcceptedToBacklog);
        var humanReview = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.RoutedToHumanReview);
        var duplicates = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.SuppressedDuplicate);
        var noise = ctx.TriageDecisions.Count(d => d.Status == TriageStatus.DiscardedNoise);
        var closedCount = ctx.IssueResolutionDecisions.Count(d => d.Action == IssueResolutionAction.CloseResolved);
        var prInlineCount = ctx.PrCommentDecisions.Count(d => d.Action == PrCommentAction.PostInlineReview);

        sb.AppendLine("## 4. System 1 Jev Triage Funnel Statistics");
        sb.AppendLine();
        sb.AppendLine("| Triage Outcome | Count | Description |");
        sb.AppendLine("| :--- | :--- | :--- |");
        sb.AppendLine($"| **Evaluated Candidates** | {ctx.TriageDecisions.Count} | Total findings analyzed through Jev |");
        sb.AppendLine($"| **Accepted to Backlog** | **{accepted}** | High-confidence novel findings queued for GitHub Projects Backlog |");
        sb.AppendLine($"| **Routed to AI Triage Review** | **{humanReview}** | Borderline findings or potential duplicate matches |");
        sb.AppendLine($"| **Completed Issues Verified & Closed** | **{closedCount}** | Open issues verified fixed via Jev `Noul` and closed with commit links |");
        sb.AppendLine($"| **PR Inline Review Comments** | **{prInlineCount}** | High-signal PR diff comments gated through Jev |");
        sb.AppendLine($"| **Suppressed Duplicates** | **{duplicates}** | Pairwise duplicates rejected via Jev `Noul` |");
        sb.AppendLine($"| **Suppressed Noise / Hallucinations** | **{noise}** | Low-confidence or subjective items discarded via Jev `Choice` |");
        sb.AppendLine();

        return sb.ToString();
    }
}
