using GitGavel.Configuration;
using GitGavel.System1.Abstractions;
using GitGavel.System1.Models;
using GitGavel.System2.Models;
using TypeSafe.Jev;

namespace GitGavel.System1.Engines;

public class TypeSafeJevEngine : ITriageEngine
{
    private readonly IJevClient _jevClient;

    public TypeSafeJevEngine(IJevClient jevClient)
    {
        _jevClient = jevClient;
    }

    public TypeSafeJevEngine(string? apiKey = null)
    {
        var key = apiKey ?? Environment.GetEnvironmentVariable("JEV_API_KEY") ?? "";
        _jevClient = new JevClient(new JevClientOptions
        {
            ApiKey = key
        });
    }

    public async Task<TriageDecision> EvaluateFindingAsync(
        FindingCandidate finding,
        IReadOnlyList<ExistingIssue> existingIssues,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var thresholds = config.Triage.Jev.Thresholds;

        // 1. Deduplication with Jev Noul (if enabled and existing issues are present)
        int? duplicateIssueNum = null;
        double maxDuplicateProb = 0.0;
        int? uncertainMatchIssueNum = null;

        if (config.Triage.Jev.DedupeWithNoul && existingIssues.Count > 0)
        {
            foreach (var issue in existingIssues)
            {
                var dedupeState = JsonContent.From(new
                {
                    candidate = new
                    {
                        title = finding.Title,
                        summary = finding.Summary,
                        filePath = finding.FilePath,
                        startLine = finding.StartLine,
                        category = finding.Category
                    },
                    existingIssue = new
                    {
                        number = issue.Number,
                        title = issue.Title,
                        body = issue.Body.Length > 1000 ? issue.Body[..1000] : issue.Body
                    }
                });

                var dedupeQuestions = new Dictionary<string, Question>
                {
                    ["is_duplicate"] = Question.Noul(
                        "Does candidate finding represent the same core problem or refactoring target as existing issue?",
                        JsonContent.From(new NoulCriteria
                        {
                            True = "The candidate finding and existing issue describe the same bug, vulnerability, or refactoring target in the same code area.",
                            False = "The candidate finding describes a distinct, novel, or separate issue."
                        })
                    )
                };

                var dedupeResp = await _jevClient.SystemOneAsync(dedupeState, dedupeQuestions, cancellationToken: ct);
                var noul = dedupeResp.Noul("is_duplicate");
                var prob = noul?.Noul ?? 0.0;

                if (prob > maxDuplicateProb)
                {
                    maxDuplicateProb = prob;
                }

                if (prob >= thresholds.AutoBacklogConfidence) // >= 0.85 -> Definite Duplicate
                {
                    duplicateIssueNum = issue.Number;
                    return new TriageDecision
                    {
                        Finding = finding,
                        IsDuplicate = true,
                        DuplicateOfIssueNumber = issue.Number,
                        DuplicateProbability = prob,
                        JevScore = 1.0,
                        PriorityLabel = "P3",
                        TargetColumn = "Discard",
                        Confidence = prob,
                        Status = TriageStatus.SuppressedDuplicate,
                        Reason = $"Duplicate of existing issue #{issue.Number} (P={prob:P0} >= {thresholds.AutoBacklogConfidence:P0})"
                    };
                }

                if (prob > (1.0 - thresholds.AutoBacklogConfidence) && prob < thresholds.AutoBacklogConfidence)
                {
                    // Between 0.15 and 0.85 -> Borderline / Uncertain match
                    uncertainMatchIssueNum = issue.Number;
                }
            }
        }

        // 2. Score and Column Routing via Jev Score and Choice
        var evalState = JsonContent.From(new
        {
            candidate = new
            {
                title = finding.Title,
                summary = finding.Summary,
                filePath = finding.FilePath,
                startLine = finding.StartLine,
                endLine = finding.EndLine,
                category = finding.Category,
                severity = finding.Severity,
                owasp = finding.OwaspCategory,
                remediation = finding.SuggestedRemediation,
                details = finding.Details
            }
        });

        var evalQuestions = new Dictionary<string, Question>
        {
            ["priority_score"] = Question.Score(
                "Rate the real-world blast radius, maintainability impact, and business urgency on a scale of 1 to 5.",
                [
                    "P3 - Minor suggestion or cosmetic improvement",
                    "P3 - Low priority backlog suggestion",
                    "P2 - Medium priority standard refactoring or spec drift",
                    "P1 - High priority bug, breaking spec divergence, or moderate security flaw",
                    "P0 - Critical immediate action required (exploitable vulnerability or catastrophic bug)"
                ]
            ),
            ["route_choice"] = Question.Choice(
                "Select the target triage destination for this finding.",
                new Dictionary<string, JsonContent>
                {
                    ["Backlog"] = JsonContent.From("Finding is novel, actionable, and meets severity threshold."),
                    ["NeedsHumanReview"] = JsonContent.From("Finding is borderline, uncertain duplicate, or requires maintainer verification."),
                    ["Discard"] = JsonContent.From("Finding is low-value, false positive, subjective, or probable hallucination.")
                }
            )
        };

        var evalResp = await _jevClient.SystemOneAsync(evalState, evalQuestions, cancellationToken: ct);

        var scoreAnswer = evalResp.Score("priority_score");
        var choiceAnswer = evalResp.Choice("route_choice");

        var rawScore = scoreAnswer?.Score ?? 3.0;
        var calibratedScore = Math.Clamp(Math.Round(rawScore, 1), 1.0, 5.0);
        var selectedChoice = choiceAnswer?.Choice ?? "Backlog";
        var choiceConfidence = choiceAnswer?.Confidence ?? 0.85;

        // Map calibrated score to Priority
        var priorityLabel = calibratedScore switch
        {
            >= 4.5 => "P0",
            >= 3.5 => "P1",
            >= 2.5 => "P2",
            _ => "P3"
        };

        // If deduplication found an uncertain match (0.15 < P < 0.85), route to human review
        if (uncertainMatchIssueNum.HasValue && selectedChoice != "Discard")
        {
            return new TriageDecision
            {
                Finding = finding,
                IsDuplicate = false,
                DuplicateOfIssueNumber = uncertainMatchIssueNum,
                DuplicateProbability = maxDuplicateProb,
                JevScore = calibratedScore,
                PriorityLabel = priorityLabel,
                TargetColumn = "NeedsHumanReview",
                Confidence = choiceConfidence,
                Status = TriageStatus.RoutedToHumanReview,
                Reason = $"Potential duplicate of issue #{uncertainMatchIssueNum} (P={maxDuplicateProb:P0}). Routed for maintainer review."
            };
        }

        // Apply confidence threshold routing
        if (selectedChoice == "Backlog" && choiceConfidence < thresholds.HumanReviewConfidence)
        {
            selectedChoice = "NeedsHumanReview";
        }

        var status = selectedChoice switch
        {
            "Backlog" => TriageStatus.AcceptedToBacklog,
            "NeedsHumanReview" => TriageStatus.RoutedToHumanReview,
            _ => TriageStatus.DiscardedNoise
        };

        return new TriageDecision
        {
            Finding = finding,
            IsDuplicate = false,
            DuplicateOfIssueNumber = null,
            DuplicateProbability = maxDuplicateProb,
            JevScore = calibratedScore,
            PriorityLabel = priorityLabel,
            TargetColumn = selectedChoice,
            Confidence = choiceConfidence,
            Status = status,
            Reason = $"Jev Choice '{selectedChoice}' with confidence {choiceConfidence:P0} (Score: {calibratedScore:F1}/5.0)"
        };
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
            var decision = await EvaluateFindingAsync(finding, existingIssues, config, ct);
            decisions.Add(decision);
        }

        // Enforce maxIssuesPerRun safety cap on Backlog tickets
        var accepted = decisions.Where(d => d.Status == TriageStatus.AcceptedToBacklog).ToList();
        if (accepted.Count > config.Triage.MaxIssuesPerRun)
        {
            // Keep top N by JevScore descending
            var topAccepted = accepted.OrderByDescending(d => d.JevScore).Take(config.Triage.MaxIssuesPerRun).ToHashSet();
            for (var i = 0; i < decisions.Count; i++)
            {
                var d = decisions[i];
                if (d.Status == TriageStatus.AcceptedToBacklog && !topAccepted.Contains(d))
                {
                    decisions[i] = d with
                    {
                        Status = TriageStatus.RoutedToHumanReview,
                        TargetColumn = "NeedsHumanReview",
                        Reason = $"Deferred to AI Triage review: maxIssuesPerRun cap ({config.Triage.MaxIssuesPerRun}) reached."
                    };
                }
            }
        }

        return decisions;
    }

    public async Task<List<PrCommentTriageDecision>> TriagePrReviewCommentsAsync(
        IReadOnlyList<FindingCandidate> findings,
        IReadOnlyList<ExistingPrReviewComment> existingComments,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var thresholds = config.Triage.Jev.Thresholds;
        var decisions = new List<PrCommentTriageDecision>();

        foreach (var finding in findings)
        {
            long? dupId = null;
            var maxProb = 0.0;

            if (config.Github.PullRequests.AuditExistingReviews && existingComments.Count > 0)
            {
                var exactDup = existingComments.FirstOrDefault(e =>
                    e.FilePath.Replace('\\', '/').TrimStart('/').Equals(finding.FilePath.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase) &&
                    (e.Body.Contains(finding.Title, StringComparison.OrdinalIgnoreCase) ||
                     (e.Line.HasValue && Math.Abs(e.Line.Value - finding.StartLine) <= 3 && e.Body.Contains($"| {finding.Category}]", StringComparison.OrdinalIgnoreCase))));

                if (exactDup != null)
                {
                    dupId = exactDup.Id;
                    maxProb = 1.0;
                }
            }

            if (!dupId.HasValue && config.Triage.Jev.DedupeWithNoul && config.Github.PullRequests.AuditExistingReviews && existingComments.Count > 0)
            {
                var sameFileComments = existingComments
                    .Where(e => e.FilePath.Replace('\\', '/').TrimStart('/').Equals(finding.FilePath.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var existing in sameFileComments)
                {
                    var dedupeState = JsonContent.From(new
                    {
                        candidate = new
                        {
                            filePath = finding.FilePath,
                            line = finding.StartLine,
                            title = finding.Title,
                            body = finding.Summary
                        },
                        existingComment = new
                        {
                            id = existing.Id,
                            filePath = existing.FilePath,
                            line = existing.Line,
                            body = existing.Body
                        }
                    });

                    var dedupeQuestions = new Dictionary<string, Question>
                    {
                        ["is_duplicate_comment"] = Question.Noul(
                            "Does candidate PR comment duplicate an existing review comment or thread on the Pull Request?",
                            JsonContent.From(new NoulCriteria
                            {
                                True = "Both comments address the same defect or suggestion on the same file/area.",
                                False = "The candidate comment raises a distinct, novel review point."
                            })
                        )
                    };

                    var dedupeResp = await _jevClient.SystemOneAsync(dedupeState, dedupeQuestions, cancellationToken: ct);
                    var prob = dedupeResp.Noul("is_duplicate_comment")?.Noul ?? 0.0;
                    if (prob > maxProb) maxProb = prob;

                    if (prob >= thresholds.AutoBacklogConfidence)
                    {
                        dupId = existing.Id;
                        break;
                    }
                }
            }

            if (dupId.HasValue)
            {
                decisions.Add(new PrCommentTriageDecision
                {
                    Finding = finding,
                    IsDuplicate = true,
                    DuplicateOfCommentId = dupId,
                    DuplicateProbability = maxProb,
                    JevScore = 1.0,
                    PriorityLabel = "P3",
                    Action = PrCommentAction.Discard,
                    Confidence = maxProb,
                    Reason = $"Suppressed duplicate of existing PR review comment #{dupId} (P={maxProb:P0})"
                });
                continue;
            }

            if (finding.Severity.Equals("Low", StringComparison.OrdinalIgnoreCase))
            {
                decisions.Add(new PrCommentTriageDecision
                {
                    Finding = finding,
                    IsDuplicate = false,
                    DuplicateProbability = maxProb,
                    JevScore = 1.5,
                    PriorityLabel = "P3",
                    Action = PrCommentAction.Discard,
                    Confidence = 0.95,
                    Reason = "Suppressed low-severity P3 nitpick in PR review mode to prevent scope creep."
                });
                continue;
            }

            var evalState = JsonContent.From(new
            {
                comment = new
                {
                    filePath = finding.FilePath,
                    line = finding.StartLine,
                    category = finding.Category,
                    severity = finding.Severity,
                    title = finding.Title,
                    body = finding.Summary,
                    suggestedCode = finding.SuggestedRemediation
                }
            });

            var evalQuestions = new Dictionary<string, Question>
            {
                ["priority_score"] = Question.Score(
                    "Rate the severity and impact of this PR review finding on a scale of 1 to 5.",
                    [
                        "P3 - Minor nit or formatting suggestion",
                        "P3 - Low priority improvement",
                        "P2 - Medium priority maintainability or test suggestion",
                        "P1 - High priority defect or spec divergence",
                        "P0 - Critical bug or security vulnerability blocking merge"
                    ]
                ),
                ["pr_route_choice"] = Question.Choice(
                    "Select how this PR review finding should be posted.",
                    new Dictionary<string, JsonContent>
                    {
                        ["PostInlineReview"] = JsonContent.From("High-signal, line-specific finding that warrants an inline diff review comment."),
                        ["SummaryCommentOnly"] = JsonContent.From("Broader observation better suited for the top-level PR summary comment."),
                        ["Discard"] = JsonContent.From("Low-value nit, subjective preference, or false positive.")
                    }
                )
            };

            var evalResp = await _jevClient.SystemOneAsync(evalState, evalQuestions, cancellationToken: ct);
            var rawScore = evalResp.Score("priority_score")?.Score ?? 3.0;
            var calibratedScore = Math.Clamp(Math.Round(rawScore, 1), 1.0, 5.0);
            var choiceStr = evalResp.Choice("pr_route_choice")?.Choice ?? "PostInlineReview";
            var confidence = evalResp.Choice("pr_route_choice")?.Confidence ?? 0.88;

            var prio = calibratedScore switch
            {
                >= 4.5 => "P0",
                >= 3.5 => "P1",
                >= 2.5 => "P2",
                _ => "P3"
            };

            var action = prio == "P3"
                ? PrCommentAction.Discard
                : choiceStr switch
                {
                    "PostInlineReview" => PrCommentAction.PostInlineReview,
                    "SummaryCommentOnly" => PrCommentAction.SummaryCommentOnly,
                    _ => PrCommentAction.Discard
                };

            decisions.Add(new PrCommentTriageDecision
            {
                Finding = finding,
                IsDuplicate = false,
                DuplicateProbability = maxProb,
                JevScore = calibratedScore,
                PriorityLabel = prio,
                Action = action,
                Confidence = confidence,
                Reason = prio == "P3"
                    ? $"Suppressed P3 nitpick (Score: {calibratedScore:F1}/5.0) to prevent PR scope creep."
                    : $"Jev Choice '{choiceStr}' ({confidence:P0}, Score: {calibratedScore:F1}/5.0)"
            });
        }

        // Enforce MaxInlineCommentsPerPr cap across the PR lifecycle
        var maxInline = config.Github.PullRequests.MaxInlineCommentsPerPr;
        var remainingInlineBudget = Math.Max(0, maxInline - existingComments.Count);
        var inlineAccepted = decisions.Where(d => d.Action == PrCommentAction.PostInlineReview).ToList();
        if (maxInline > 0 && inlineAccepted.Count > remainingInlineBudget)
        {
            var topInline = inlineAccepted.OrderByDescending(d => d.JevScore).Take(remainingInlineBudget).ToHashSet();
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

        return decisions;
    }

    public async Task<List<IssueResolutionDecision>> VerifyIssueResolutionsAsync(
        IReadOnlyList<IssueResolutionCandidate> candidates,
        IReadOnlyList<ExistingIssue> openIssues,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var closeThreshold = config.Triage.Jev.Thresholds.AutoCloseIssueConfidence;
        var decisions = new List<IssueResolutionDecision>();

        foreach (var candidate in candidates)
        {
            if (!candidate.IsResolved || candidate.ResolvingCommitShas.Count == 0)
            {
                decisions.Add(new IssueResolutionDecision
                {
                    Candidate = candidate,
                    VerifiedResolved = false,
                    ResolutionProbability = 0.0,
                    Action = IssueResolutionAction.LeaveOpen,
                    TargetColumn = config.Github.ProjectsV2.Columns.Backlog,
                    Reason = "Candidate does not cite resolving commits or is marked unresolved."
                });
                continue;
            }

            var matchingIssue = openIssues.FirstOrDefault(i => i.Number == candidate.IssueNumber);

            var verifyState = JsonContent.From(new
            {
                issue = new
                {
                    number = candidate.IssueNumber,
                    title = matchingIssue?.Title ?? candidate.IssueTitle
                },
                auditResolutionVerification = new
                {
                    status = "RESOLVED_AND_VERIFIED_IN_CODEBASE",
                    isResolved = candidate.IsResolved,
                    verifiedByAgent = candidate.AgentName,
                    howItWasFixed = candidate.HowItWasFixed,
                    resolvingCommitShas = candidate.ResolvingCommitShas,
                    resolvingCommitSummaries = candidate.ResolvingCommitSummaries
                }
            });

            var verifyQuestions = new Dictionary<string, Question>
            {
                ["is_issue_resolved"] = Question.Noul(
                    "Does the audit resolution verification confirm that the issue has been fixed in the codebase by the cited commits and fix explanation?",
                    "The resolution verification provides a coherent fix explanation and commit attribution addressing the issue title.",
                    "The fix explanation is empty, nonsensical, or completely unrelated to the issue."
                )
            };

            var verifyResp = await _jevClient.SystemOneAsync(verifyState, verifyQuestions, cancellationToken: ct);
            var prob = verifyResp.Noul("is_issue_resolved")?.Noul ?? 0.0;

            if (prob >= closeThreshold)
            {
                decisions.Add(new IssueResolutionDecision
                {
                    Candidate = candidate,
                    VerifiedResolved = true,
                    ResolutionProbability = prob,
                    Action = IssueResolutionAction.CloseResolved,
                    TargetColumn = config.Github.ProjectsV2.Columns.Done,
                    Reason = $"Verified resolved via Jev Noul (P={prob:P0} >= {closeThreshold:P0})"
                });
            }
            else if (prob >= 0.60)
            {
                decisions.Add(new IssueResolutionDecision
                {
                    Candidate = candidate,
                    VerifiedResolved = false,
                    ResolutionProbability = prob,
                    Action = IssueResolutionAction.CommentProgress,
                    TargetColumn = config.Github.ProjectsV2.Columns.HumanReview,
                    Reason = $"Partial fix confidence (P={prob:P0} < {closeThreshold:P0}); leaving issue open."
                });
            }
            else
            {
                decisions.Add(new IssueResolutionDecision
                {
                    Candidate = candidate,
                    VerifiedResolved = false,
                    ResolutionProbability = prob,
                    Action = IssueResolutionAction.LeaveOpen,
                    TargetColumn = config.Github.ProjectsV2.Columns.Backlog,
                    Reason = $"Insufficient resolution confidence (P={prob:P0})"
                });
            }
        }

        return decisions;
    }
}
