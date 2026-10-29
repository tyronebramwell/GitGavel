using GitGavel.Configuration;
using GitGavel.System1.Models;

namespace GitGavel.System1.Engines;

public static class TriageEngineHelper
{
    public const long EstimatedTokensPerSkippedAgent = 5000L;

    public static string BuildIssueSummary(string? body, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "";
        }

        return body.Length > maxLength
            ? string.Concat(body.AsSpan(0, maxLength), "...")
            : body;
    }

    public static string MapScoreToPriority(double score) =>
        score switch
        {
            >= 4.5 => "P0",
            >= 3.5 => "P1",
            >= 2.5 => "P2",
            _ => "P3"
        };

    public static SubagentGatingResult? TryEvaluateGlobalGatingBypass(
        GatingSignals signals,
        IReadOnlyList<string> candidateAgentNames,
        SubagentGatingConfig gatingConfig)
    {
        if (!gatingConfig.Enabled || (gatingConfig.BypassInFullRepoMode && !signals.IsPullRequestMode))
        {
            var reason = !gatingConfig.Enabled
                ? "Subagent gating disabled in configuration."
                : "Subagent gating bypassed in full repository audit mode.";

            var decisions = candidateAgentNames
                .Select(name => new SubagentGatingDecision(
                    name,
                    ShouldRun: true,
                    Confidence: 1.0,
                    Reason: reason,
                    IsDeterministicOverride: true))
                .ToList();

            return new SubagentGatingResult(decisions, EstimatedTokensSaved: 0);
        }

        return null;
    }

    public static SubagentGatingDecision? TryEvaluateDeterministicOverride(
        string agentName,
        GatingSignals signals,
        SubagentGatingConfig gatingConfig,
        double specOverrideConfidence = 1.0,
        double complexityOverrideConfidence = 1.0)
    {
        if (agentName == "SecurityAuditor" && gatingConfig.AlwaysRunSecurityOnSarifAlerts && signals.HasSarifAlerts)
        {
            return new SubagentGatingDecision(
                agentName,
                ShouldRun: true,
                Confidence: 1.0,
                Reason: $"Deterministic override: {signals.SarifFindingsCount} SARIF static alerts loaded.",
                IsDeterministicOverride: true);
        }

        if (agentName == "SpecDriftAgent" && gatingConfig.AlwaysRunSpecDriftOnSpecChanges && signals.HasSpecChanges)
        {
            return new SubagentGatingDecision(
                agentName,
                ShouldRun: true,
                Confidence: specOverrideConfidence,
                Reason: signals.IsPullRequestMode
                    ? "Deterministic override: specification documents modified in diff."
                    : "Deterministic override: specification documents present in repository.",
                IsDeterministicOverride: true);
        }

        if (agentName == "RefactoringAgent" && signals.HasComplexityHotspots)
        {
            return new SubagentGatingDecision(
                agentName,
                ShouldRun: true,
                Confidence: complexityOverrideConfidence,
                Reason: "Deterministic override: Roslyn cyclomatic complexity >= 15 detected.",
                IsDeterministicOverride: true);
        }

        return null;
    }

    public static SubagentGatingResult BuildGatingResult(IReadOnlyList<SubagentGatingDecision> decisions)
    {
        var skipped = decisions.Count(d => !d.ShouldRun);
        return new SubagentGatingResult(decisions, skipped * EstimatedTokensPerSkippedAgent);
    }

    public static void EnforceMaxIssuesPerRunCap(List<TriageDecision> decisions, int maxIssuesPerRun)
    {
        var cappedCandidates = decisions
            .Where(d => d.Status is TriageStatus.AcceptedToBacklog or TriageStatus.RoutedToHumanReview &&
                        d.PriorityLabel == "P3")
            .ToList();

        if (cappedCandidates.Count <= maxIssuesPerRun)
        {
            return;
        }

        var topAllowed = cappedCandidates
            .OrderByDescending(d => d.JevScore)
            .ThenByDescending(d => d.Confidence)
            .Take(maxIssuesPerRun)
            .ToHashSet();

        for (var i = 0; i < decisions.Count; i++)
        {
            var d = decisions[i];
            if (d.Status is TriageStatus.AcceptedToBacklog or TriageStatus.RoutedToHumanReview &&
                d.PriorityLabel == "P3" &&
                !topAllowed.Contains(d))
            {
                decisions[i] = d with
                {
                    Status = TriageStatus.DiscardedNoise,
                    TargetColumn = "Discard",
                    Reason = $"Deferred: maxIssuesPerRun cap ({maxIssuesPerRun}) reached for P3 findings (P0–P2 exempt)."
                };
            }
        }
    }

    public static void EnforceMaxInlineCommentsCap(List<PrCommentTriageDecision> decisions, int maxInlineCommentsPerPr, int existingCommentsCount = 0)
    {
        if (maxInlineCommentsPerPr <= 0) return;

        var remainingInlineBudget = Math.Max(0, maxInlineCommentsPerPr - existingCommentsCount);
        var inlineAccepted = decisions.Where(d => d.Action == PrCommentAction.PostInlineReview).ToList();
        if (inlineAccepted.Count <= remainingInlineBudget)
        {
            return;
        }

        var topInline = inlineAccepted.OrderByDescending(d => d.JevScore).Take(remainingInlineBudget).ToHashSet();
        for (var i = 0; i < decisions.Count; i++)
        {
            var d = decisions[i];
            if (d.Action == PrCommentAction.PostInlineReview && !topInline.Contains(d))
            {
                decisions[i] = d with
                {
                    Action = PrCommentAction.SummaryCommentOnly,
                    Reason = $"Moved to PR summary: maxInlineCommentsPerPr cap ({maxInlineCommentsPerPr}) reached."
                };
            }
        }
    }
}
