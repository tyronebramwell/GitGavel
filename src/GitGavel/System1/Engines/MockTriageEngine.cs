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
        var decisions = new List<SubagentGatingDecision>();

        if (!gatingConfig.Enabled || (gatingConfig.BypassInFullRepoMode && !context.IsPullRequestMode))
        {
            foreach (var name in candidateAgentNames)
            {
                decisions.Add(new SubagentGatingDecision(
                    name,
                    ShouldRun: true,
                    Confidence: 1.0,
                    Reason: !gatingConfig.Enabled ? "Subagent gating disabled in configuration." : "Subagent gating bypassed in full repository audit mode.",
                    IsDeterministicOverride: true));
            }
            return Task.FromResult(new SubagentGatingResult(decisions, EstimatedTokensSaved: 0));
        }

        var hasSpecChanges = context.IsPullRequestMode
            ? context.SpecFiles.Any(f => !string.IsNullOrEmpty(f.DiffPatch)) || context.SourceFiles.Any(f => f.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(f.DiffPatch))
            : context.SpecFiles.Count > 0 || context.SourceFiles.Any(f => f.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        var hasSourceCodeChanges = context.IsPullRequestMode
            ? context.SourceFiles.Any(f => !f.IsTest && !string.IsNullOrEmpty(f.DiffPatch))
            : context.SourceFiles.Any(f => !f.IsTest);
        var hasTestChanges = context.IsPullRequestMode
            ? context.TestFiles.Any(f => !string.IsNullOrEmpty(f.DiffPatch))
            : context.TestFiles.Count > 0;
        var hasSarif = context.SarifFindings.Count > 0;
        var hasComplexity = context.AstSummaries.Any(a => a.TotalComplexityScore >= 15 || a.IsComplexityHotspot);

        foreach (var name in candidateAgentNames)
        {
            bool shouldRun;
            double confidence;
            string reason;
            bool isOverride = false;

            switch (name)
            {
                case "SpecDriftAgent":
                    if (gatingConfig.AlwaysRunSpecDriftOnSpecChanges && hasSpecChanges)
                    {
                        shouldRun = true;
                        confidence = 0.95;
                        reason = context.IsPullRequestMode
                            ? "Specification documents modified in diff."
                            : "Specification documents present in repository.";
                        isOverride = true;
                    }
                    else
                    {
                        shouldRun = hasSpecChanges;
                        confidence = shouldRun ? 0.90 : 0.15;
                        reason = shouldRun
                            ? (context.IsPullRequestMode ? "Spec files or documentation modified." : "Spec files present in repository.")
                            : (context.IsPullRequestMode ? "No specification files or API docs modified." : "No specification files found in repository scope.");
                    }
                    break;

                case "SecurityAuditor":
                    if (gatingConfig.AlwaysRunSecurityOnSarifAlerts && hasSarif)
                    {
                        shouldRun = true;
                        confidence = 1.0;
                        reason = $"Deterministic override: {context.SarifFindings.Count} SARIF static alerts loaded.";
                        isOverride = true;
                    }
                    else
                    {
                        // In mock mode, check if code files have security-relevant paths or auth
                        var securityPaths = context.SourceFiles.Any(f => f.RelativePath.Contains("Auth", StringComparison.OrdinalIgnoreCase) ||
                                                                        f.RelativePath.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
                                                                        f.RelativePath.Contains("Key", StringComparison.OrdinalIgnoreCase));
                        shouldRun = hasSourceCodeChanges && (securityPaths || hasSarif);
                        confidence = shouldRun ? 0.88 : 0.20;
                        reason = shouldRun
                            ? "Security-sensitive paths or SARIF alerts detected."
                            : "No security-sensitive changes or alerts detected.";
                    }
                    break;

                case "ReliabilityAgent":
                    shouldRun = hasSourceCodeChanges;
                    confidence = shouldRun ? 0.85 : 0.10;
                    reason = shouldRun
                        ? (context.IsPullRequestMode ? "Production source code modified." : "Production source code present in repository.")
                        : (context.IsPullRequestMode ? "No production source code modified in diff." : "No production source code found in repository.");
                    break;

                case "RefactoringAgent":
                    if (hasComplexity)
                    {
                        shouldRun = true;
                        confidence = 0.92;
                        reason = "Deterministic override: Roslyn cyclomatic complexity >= 15 detected.";
                        isOverride = true;
                    }
                    else
                    {
                        shouldRun = hasSourceCodeChanges;
                        confidence = shouldRun ? 0.75 : 0.20;
                        reason = shouldRun
                            ? (context.IsPullRequestMode ? "Source code files modified." : "Source code files present in repository.")
                            : (context.IsPullRequestMode ? "No structural code churn detected." : "No source files found in repository.");
                    }
                    break;

                case "TestSentinelAgent":
                    shouldRun = hasSourceCodeChanges || hasTestChanges;
                    confidence = shouldRun ? 0.90 : 0.15;
                    reason = shouldRun
                        ? (context.IsPullRequestMode ? "Source or test files modified." : "Source or test files present in repository.")
                        : (context.IsPullRequestMode ? "No test or production code changes." : "No source or test files found in repository.");
                    break;

                default:
                    shouldRun = true;
                    confidence = 0.80;
                    reason = "Standard agent dispatch.";
                    break;
            }

            decisions.Add(new SubagentGatingDecision(name, shouldRun, confidence, reason, isOverride));
        }

        var skippedCount = decisions.Count(d => !d.ShouldRun);
        var tokensSaved = skippedCount * 5000L;
        return Task.FromResult(new SubagentGatingResult(decisions, tokensSaved));
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

            var prio = score switch
            {
                >= 4.5 => "P0",
                >= 3.5 => "P1",
                >= 2.5 => "P2",
                _ => "P3"
            };

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

        // Default heuristic simulation
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

        var priority = scoreVal >= 4.5 ? "P0" : scoreVal >= 3.5 ? "P1" : scoreVal >= 2.5 ? "P2" : "P3";

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

        var cappedCandidates = decisions
            .Where(d => d.Status is TriageStatus.AcceptedToBacklog or TriageStatus.RoutedToHumanReview &&
                        d.PriorityLabel == "P3")
            .ToList();

        if (cappedCandidates.Count > config.Triage.MaxIssuesPerRun)
        {
            var topAllowed = cappedCandidates
                .OrderByDescending(d => d.JevScore)
                .ThenByDescending(d => d.Confidence)
                .Take(config.Triage.MaxIssuesPerRun)
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
                        Reason = $"Deferred: maxIssuesPerRun cap ({config.Triage.MaxIssuesPerRun}) reached for P3 findings (P0–P2 exempt)."
                    };
                }
            }
        }

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

            var prio = score >= 4.5 ? "P0" : score >= 3.5 ? "P1" : score >= 2.5 ? "P2" : "P3";
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

        var maxInline = config.Github.PullRequests.MaxInlineCommentsPerPr;
        var inlineAccepted = decisions.Where(d => d.Action == PrCommentAction.PostInlineReview).ToList();
        if (maxInline > 0 && inlineAccepted.Count > maxInline)
        {
            var topInline = inlineAccepted.OrderByDescending(d => d.JevScore).Take(maxInline).ToHashSet();
            for (var i = 0; i < decisions.Count; i++)
            {
                var d = decisions[i];
                if (d.Action == PrCommentAction.PostInlineReview && !topInline.Contains(d))
                {
                    decisions[i] = d with
                    {
                        Action = PrCommentAction.SummaryCommentOnly,
                        Reason = $"Moved to PR summary: maxInlineCommentsPerPr cap ({maxInline}) reached."
                    };
                }
            }
        }

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
