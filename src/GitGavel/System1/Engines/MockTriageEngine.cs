using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System1.Abstractions;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.System1.Engines;

public class MockTriageEngine : ITriageEngine
{
    private readonly Func<FindingCandidate, ExistingIssue?, (bool isDuplicate, double prob, double score, string choice)>? _customEvaluator;
    private readonly Func<RepoContext, IReadOnlyList<string>, GitGavelConfig, SubagentGatingResult>? _customGatingEvaluator;

    public MockTriageEngine(
        Func<FindingCandidate, ExistingIssue?, (bool isDuplicate, double prob, double score, string choice)>? customEvaluator = null,
        Func<RepoContext, IReadOnlyList<string>, GitGavelConfig, SubagentGatingResult>? customGatingEvaluator = null)
    {
        _customEvaluator = customEvaluator;
        _customGatingEvaluator = customGatingEvaluator;
    }

    public Task<SubagentGatingResult> EvaluateSubagentGatingAsync(
        RepoContext context,
        IReadOnlyList<string> candidateAgentNames,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        if (_customGatingEvaluator != null)
        {
            return Task.FromResult(_customGatingEvaluator(context, candidateAgentNames, config));
        }

        var gatingConfig = config.Triage.SubagentGating;
        var signals = GatingSignals.FromRepoContext(context);

        var bypassResult = TriageEngineHelper.TryEvaluateGlobalGatingBypass(signals, candidateAgentNames, gatingConfig);
        if (bypassResult != null)
        {
            return Task.FromResult(bypassResult);
        }

        var decisions = new List<SubagentGatingDecision>();

        foreach (var name in candidateAgentNames)
        {
            var overrideDecision = TriageEngineHelper.TryEvaluateDeterministicOverride(
                name,
                signals,
                gatingConfig,
                specOverrideConfidence: 0.95,
                complexityOverrideConfidence: 0.92);

            if (overrideDecision != null)
            {
                decisions.Add(overrideDecision);
                continue;
            }

            decisions.Add(EvaluateMockAgentHeuristic(name, signals));
        }

        return Task.FromResult(TriageEngineHelper.BuildGatingResult(decisions));
    }

    private static SubagentGatingDecision EvaluateMockAgentHeuristic(string agentName, GatingSignals signals)
    {
        bool shouldRun;
        double confidence;
        string reason;

        switch (agentName)
        {
            case "SpecDriftAgent":
                shouldRun = signals.HasSpecChanges;
                confidence = shouldRun ? 0.90 : 0.15;
                reason = shouldRun
                    ? (signals.IsPullRequestMode ? "Spec files or documentation modified." : "Spec files present in repository.")
                    : (signals.IsPullRequestMode ? "No specification files or API docs modified." : "No specification files found in repository scope.");
                break;

            case "SecurityAuditor":
                shouldRun = signals.HasSourceCodeChanges && (signals.HasSecuritySensitivePaths || signals.HasSarifAlerts);
                confidence = shouldRun ? 0.88 : 0.20;
                reason = shouldRun
                    ? "Security-sensitive paths or SARIF alerts detected."
                    : "No security-sensitive changes or alerts detected.";
                break;

            case "ReliabilityAgent":
                shouldRun = signals.HasSourceCodeChanges;
                confidence = shouldRun ? 0.85 : 0.10;
                reason = shouldRun
                    ? (signals.IsPullRequestMode ? "Production source code modified." : "Production source code present in repository.")
                    : (signals.IsPullRequestMode ? "No production source code modified in diff." : "No production source code found in repository.");
                break;

            case "RefactoringAgent":
                shouldRun = signals.HasSourceCodeChanges;
                confidence = shouldRun ? 0.75 : 0.20;
                reason = shouldRun
                    ? (signals.IsPullRequestMode ? "Source code files modified." : "Source code files present in repository.")
                    : (signals.IsPullRequestMode ? "No structural code churn detected." : "No source files found in repository.");
                break;

            case "TestSentinelAgent":
                shouldRun = signals.HasSourceCodeChanges || signals.HasTestChanges;
                confidence = shouldRun ? 0.90 : 0.15;
                reason = shouldRun
                    ? (signals.IsPullRequestMode ? "Source or test files modified." : "Source or test files present in repository.")
                    : (signals.IsPullRequestMode ? "No test or production code changes." : "No source or test files found in repository.");
                break;

            default:
                shouldRun = true;
                confidence = 0.80;
                reason = "Standard agent dispatch.";
                break;
        }

        return new SubagentGatingDecision(agentName, shouldRun, confidence, reason);
    }

    public Task<TriageDecision> EvaluateFindingAsync(
        FindingCandidate finding,
        IReadOnlyList<ExistingIssue> existingIssues,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        if (_customEvaluator != null)
        {
            var match = existingIssues.FirstOrDefault(i => i.Title.Contains(finding.Title, StringComparison.OrdinalIgnoreCase) ||
                                                           i.Body.Contains(finding.FilePath, StringComparison.OrdinalIgnoreCase));
            var (isDup, prob, score, choice) = _customEvaluator(finding, match);
            var prio = TriageEngineHelper.MapScoreToPriority(score);

            var stat = isDup ? TriageStatus.SuppressedDuplicate :
                choice == "Backlog" ? TriageStatus.AcceptedToBacklog :
                choice == "NeedsHumanReview" ? TriageStatus.RoutedToHumanReview :
                TriageStatus.DiscardedNoise;

            return Task.FromResult(new TriageDecision
            {
                Finding = finding,
                IsDuplicate = isDup,
                DuplicateOfIssueNumber = match?.Number,
                DuplicateProbability = prob,
                JevScore = score,
                PriorityLabel = prio,
                TargetColumn = choice,
                Confidence = 0.92,
                Status = stat,
                Reason = isDup ? $"Duplicate of #{match?.Number}" : $"Triage choice: {choice}"
            });
        }

        var duplicate = existingIssues.FirstOrDefault(i =>
            i.Title.Equals(finding.Title, StringComparison.OrdinalIgnoreCase) ||
            (i.Body.Contains(finding.FilePath, StringComparison.OrdinalIgnoreCase) && i.Title.Contains(finding.Category, StringComparison.OrdinalIgnoreCase)));

        if (duplicate != null)
        {
            return Task.FromResult(new TriageDecision
            {
                Finding = finding,
                IsDuplicate = true,
                DuplicateOfIssueNumber = duplicate.Number,
                DuplicateProbability = 0.94,
                JevScore = 1.0,
                PriorityLabel = "P3",
                TargetColumn = "Discard",
                Confidence = 0.94,
                Status = TriageStatus.SuppressedDuplicate,
                Reason = $"Duplicate of issue #{duplicate.Number} (P=94%)"
            });
        }

        var scoreVal = finding.Severity switch
        {
            "Critical" => 5.0,
            "High" => 4.0,
            "Medium" => 3.0,
            _ => 2.0
        };

        var priority = TriageEngineHelper.MapScoreToPriority(scoreVal);

        return Task.FromResult(new TriageDecision
        {
            Finding = finding,
            IsDuplicate = false,
            DuplicateOfIssueNumber = null,
            DuplicateProbability = 0.05,
            JevScore = scoreVal,
            PriorityLabel = priority,
            TargetColumn = "Backlog",
            Confidence = 0.95,
            Status = TriageStatus.AcceptedToBacklog,
            Reason = $"Simulated triage accepted finding as {priority} (Score: {scoreVal:F1}/5.0)"
        });
    }

    public async Task<List<TriageDecision>> TriageAllAsync(
        IReadOnlyList<FindingCandidate> findings,
        IReadOnlyList<ExistingIssue> existingIssues,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var decisions = new List<TriageDecision>();
        foreach (var finding in findings)
        {
            var d = await EvaluateFindingAsync(finding, existingIssues, config, ct);
            decisions.Add(d);
        }

        TriageEngineHelper.EnforceMaxIssuesPerRunCap(decisions, config.Triage.MaxIssuesPerRun);
        return decisions;
    }

    public Task<List<PrCommentTriageDecision>> TriagePrReviewCommentsAsync(
        IReadOnlyList<FindingCandidate> findings,
        IReadOnlyList<ExistingPrReviewComment> existingComments,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var decisions = new List<PrCommentTriageDecision>();

        foreach (var finding in findings)
        {
            var duplicate = config.Github.PullRequests.AuditExistingReviews
                ? existingComments.FirstOrDefault(e =>
                    e.FilePath.Equals(finding.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    (e.Line == finding.StartLine || e.Body.Contains(finding.Title, StringComparison.OrdinalIgnoreCase)))
                : null;

            if (duplicate != null)
            {
                decisions.Add(new PrCommentTriageDecision
                {
                    Finding = finding,
                    IsDuplicate = true,
                    DuplicateOfCommentId = duplicate.Id,
                    DuplicateProbability = 0.93,
                    JevScore = 1.0,
                    PriorityLabel = "P3",
                    Action = PrCommentAction.Discard,
                    Confidence = 0.93,
                    Reason = $"Suppressed duplicate of existing PR review comment #{duplicate.Id} (P=93%)"
                });
                continue;
            }

            var score = finding.Severity switch
            {
                "Critical" => 5.0,
                "High" => 4.0,
                "Medium" => 3.0,
                _ => 2.0
            };

            var prio = TriageEngineHelper.MapScoreToPriority(score);
            var action = score >= 2.5 ? PrCommentAction.PostInlineReview : PrCommentAction.Discard;

            decisions.Add(new PrCommentTriageDecision
            {
                Finding = finding,
                IsDuplicate = false,
                DuplicateProbability = 0.05,
                JevScore = score,
                PriorityLabel = prio,
                Action = action,
                Confidence = 0.94,
                Reason = $"Simulated PR review triage ({prio}, Score: {score:F1}/5.0)"
            });
        }

        TriageEngineHelper.EnforceMaxInlineCommentsCap(decisions, config.Github.PullRequests.MaxInlineCommentsPerPr);
        return Task.FromResult(decisions);
    }

    public Task<List<IssueResolutionDecision>> VerifyIssueResolutionsAsync(
        IReadOnlyList<IssueResolutionCandidate> candidates,
        IReadOnlyList<ExistingIssue> openIssues,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var decisions = new List<IssueResolutionDecision>();

        foreach (var candidate in candidates)
        {
            if (candidate.IsResolved && candidate.ResolvingCommitShas.Count > 0)
            {
                decisions.Add(new IssueResolutionDecision
                {
                    Candidate = candidate,
                    VerifiedResolved = true,
                    ResolutionProbability = 0.96,
                    Action = IssueResolutionAction.CloseResolved,
                    TargetColumn = config.Github.ProjectsV2.Columns.Done,
                    Reason = $"Verified resolved via simulated Jev Noul (P=96% >= {config.Triage.Jev.Thresholds.AutoCloseIssueConfidence:P0})"
                });
            }
            else
            {
                decisions.Add(new IssueResolutionDecision
                {
                    Candidate = candidate,
                    VerifiedResolved = false,
                    ResolutionProbability = 0.20,
                    Action = IssueResolutionAction.LeaveOpen,
                    TargetColumn = config.Github.ProjectsV2.Columns.Backlog,
                    Reason = "Candidate does not cite resolving commits or is marked unresolved."
                });
            }
        }

        return Task.FromResult(decisions);
    }
}
