using GitGavel.Configuration;
using GitGavel.Discovery;
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

    public async Task<SubagentGatingResult> EvaluateSubagentGatingAsync(
        RepoContext context,
        IReadOnlyList<string> candidateAgentNames,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var gatingConfig = config.Triage.SubagentGating;
        var signals = GatingSignals.FromRepoContext(context);

        var bypassResult = TriageEngineHelper.TryEvaluateGlobalGatingBypass(signals, candidateAgentNames, gatingConfig);
        if (bypassResult != null)
        {
            return bypassResult;
        }

        var decisions = new List<SubagentGatingDecision>();
        var pendingQuestions = new Dictionary<string, Question>();
        var agentToQuestionKey = new Dictionary<string, string>();

        foreach (var agentName in candidateAgentNames)
        {
            var overrideDecision = TriageEngineHelper.TryEvaluateDeterministicOverride(agentName, signals, gatingConfig);
            if (overrideDecision != null)
            {
                decisions.Add(overrideDecision);
                continue;
            }

            if (TryBuildGatingQuestion(agentName, out var questionKey, out var question))
            {
                pendingQuestions[questionKey] = question;
                agentToQuestionKey[agentName] = questionKey;
            }
            else
            {
                decisions.Add(new SubagentGatingDecision(
                    agentName,
                    ShouldRun: true,
                    Confidence: 0.80,
                    Reason: "Standard subagent dispatch."));
            }
        }

        if (pendingQuestions.Count > 0)
        {
            try
            {
                var metadataState = BuildGatingMetadataState(signals);
                var response = await _jevClient.SystemOneAsync(metadataState, pendingQuestions, cancellationToken: ct);

                foreach (var (agentName, qKey) in agentToQuestionKey)
                {
                    var noul = response.Noul(qKey);
                    var prob = noul?.Noul ?? 0.50;
                    var shouldRun = prob >= gatingConfig.MinConfidence;
                    var reason = shouldRun
                        ? $"Activated by Jev Noul (P={prob:P0} >= {gatingConfig.MinConfidence:P0})"
                        : $"Skipped by Jev Noul (P={prob:P0} < {gatingConfig.MinConfidence:P0})";

                    decisions.Add(new SubagentGatingDecision(agentName, shouldRun, prob, reason));
                }
            }
            catch (Exception ex)
            {
                foreach (var (agentName, _) in agentToQuestionKey)
                {
                    decisions.Add(new SubagentGatingDecision(
                        agentName,
                        ShouldRun: true,
                        Confidence: 1.0,
                        Reason: $"Jev gating fallback (fail-open): {ex.Message}"));
                }
            }
        }

        return TriageEngineHelper.BuildGatingResult(decisions);
    }

    private static bool TryBuildGatingQuestion(string agentName, out string questionKey, out Question question)
    {
        switch (agentName)
        {
            case "SpecDriftAgent":
                questionKey = "run_spec_drift";
                question = Question.Noul(
                    "Do the changes modify specifications, API documentation, OpenAPI schemas, or public interface contracts?",
                    "Changes modify documentation, OpenAPI schemas, public controllers, or interface contracts.",
                    "Changes are purely internal refactoring, tests, or bug fixes with no spec or documentation impact."
                );
                return true;

            case "SecurityAuditor":
                questionKey = "run_security";
                question = Question.Noul(
                    "Do the changes touch security-critical code, authentication, authorization, cryptography, data access, network boundaries, or third-party dependencies?",
                    "Changes touch authentication, secrets, crypto, database queries, network endpoints, or dependencies.",
                    "Changes are cosmetic, UI, documentation, or trivial formatting."
                );
                return true;

            case "ReliabilityAgent":
                questionKey = "run_reliability";
                question = Question.Noul(
                    "Do the changes modify or introduce asynchronous execution, concurrency, task scheduling, exception handling, resource disposal, or performance-critical loops?",
                    "Changes affect async/await, concurrency, error handling, disposable resources, or collections in loops.",
                    "Changes do not modify execution semantics, error handling, or performance-sensitive logic."
                );
                return true;

            case "RefactoringAgent":
                questionKey = "run_refactoring";
                question = Question.Noul(
                    "Do the modified files exhibit significant structural complexity churn, high cyclomatic complexity, or large method modifications?",
                    "Changes introduce large methods, structural churn, deep nesting, or architectural complexity.",
                    "Changes are small, localized, or low-churn adjustments."
                );
                return true;

            case "TestSentinelAgent":
                questionKey = "run_tests";
                question = Question.Noul(
                    "Were new production features or branching logic introduced that require automated unit, integration, or E2E test verification?",
                    "New business logic, classes, methods, or conditional branches were added without corresponding tests.",
                    "Only documentation, build files, or existing tests were modified, or change is a trivial cosmetic fix."
                );
                return true;

            default:
                questionKey = string.Empty;
                question = null!;
                return false;
        }
    }

    private static JsonContent BuildGatingMetadataState(GatingSignals signals) =>
        JsonContent.From(new
        {
            context = new
            {
                isPullRequestMode = signals.IsPullRequestMode,
                prTitle = signals.PrTitle,
                prDescription = signals.PrDescription,
                changedFiles = signals.ChangedFilesSummary,
                specFilesCount = signals.SpecFilesCount,
                sourceFilesCount = signals.SourceFilesCount,
                testFilesCount = signals.TestFilesCount,
                sarifFindingsCount = signals.SarifFindingsCount,
                maxCyclomaticComplexity = signals.MaxCyclomaticComplexity
            }
        });

    public async Task<TriageDecision> EvaluateFindingAsync(
        FindingCandidate finding,
        IReadOnlyList<ExistingIssue> existingIssues,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var thresholds = config.Triage.Jev.Thresholds;

        // 1. Deduplication check against existing issues
        var (definiteDuplicateDecision, maxDuplicateProb, uncertainMatchIssueNum) =
            await TryDeduplicateFindingAsync(finding, existingIssues, config.Triage.Jev.DedupeWithNoul, thresholds, ct);

        if (definiteDuplicateDecision != null)
        {
            return definiteDuplicateDecision;
        }

        // 2. Calibrated urgency score and destination choice
        var (calibratedScore, priorityLabel, selectedChoice, choiceConfidence) =
            await EvaluatePriorityAndChoiceAsync(finding, ct);

        // 3. Handle uncertain duplicate matches
        if (uncertainMatchIssueNum.HasValue && selectedChoice != "Discard")
        {
            return ResolveUncertainDuplicateDecision(
                finding,
                uncertainMatchIssueNum.Value,
                maxDuplicateProb,
                calibratedScore,
                priorityLabel,
                choiceConfidence,
                thresholds);
        }

        // 4. Apply confidence threshold routing
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

    private async Task<(TriageDecision? DefiniteDuplicate, double MaxDuplicateProb, int? UncertainMatchIssueNum)> TryDeduplicateFindingAsync(
        FindingCandidate finding,
        IReadOnlyList<ExistingIssue> existingIssues,
        bool dedupeEnabled,
        JevThresholds thresholds,
        CancellationToken ct)
    {
        var maxDuplicateProb = 0.0;
        int? uncertainMatchIssueNum = null;

        if (!dedupeEnabled || existingIssues.Count == 0)
        {
            return (null, maxDuplicateProb, uncertainMatchIssueNum);
        }

        foreach (var issue in existingIssues)
        {
            var issueFingerprint = issue.GetEffectiveFingerprint();
            if (!string.IsNullOrWhiteSpace(issueFingerprint) &&
                string.Equals(issueFingerprint, finding.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return (new TriageDecision
                {
                    Finding = finding,
                    IsDuplicate = true,
                    DuplicateOfIssueNumber = issue.Number,
                    DuplicateProbability = 1.0,
                    JevScore = 1.0,
                    PriorityLabel = "P3",
                    TargetColumn = "Discard",
                    Confidence = 1.0,
                    Status = TriageStatus.SuppressedDuplicate,
                    Reason = $"Exact fingerprint duplicate of existing issue #{issue.Number}"
                }, 1.0, null);
            }

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
            var prob = dedupeResp.Noul("is_duplicate")?.Noul ?? 0.0;

            if (prob >= thresholds.AutoBacklogConfidence)
            {
                return (new TriageDecision
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
                }, prob, null);
            }

            if (prob > maxDuplicateProb)
            {
                maxDuplicateProb = prob;
                if (prob >= thresholds.HumanReviewConfidence && prob < thresholds.AutoBacklogConfidence)
                {
                    uncertainMatchIssueNum = issue.Number;
                }
            }
        }

        return (null, maxDuplicateProb, uncertainMatchIssueNum);
    }

    private async Task<(double CalibratedScore, string PriorityLabel, string SelectedChoice, double ChoiceConfidence)> EvaluatePriorityAndChoiceAsync(
        FindingCandidate finding,
        CancellationToken ct)
    {
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
        var rawScore = evalResp.Score("priority_score")?.Score ?? 3.0;
        var calibratedScore = Math.Clamp(Math.Round(rawScore, 1), 1.0, 5.0);
        var selectedChoice = evalResp.Choice("route_choice")?.Choice ?? "Backlog";
        var choiceConfidence = evalResp.Choice("route_choice")?.Confidence ?? 0.85;
        var priorityLabel = TriageEngineHelper.MapScoreToPriority(calibratedScore);

        return (calibratedScore, priorityLabel, selectedChoice, choiceConfidence);
    }

    private static TriageDecision ResolveUncertainDuplicateDecision(
        FindingCandidate finding,
        int uncertainMatchIssueNum,
        double maxDuplicateProb,
        double calibratedScore,
        string priorityLabel,
        double choiceConfidence,
        JevThresholds thresholds)
    {
        if (priorityLabel == "P3")
        {
            return new TriageDecision
            {
                Finding = finding,
                IsDuplicate = true,
                DuplicateOfIssueNumber = uncertainMatchIssueNum,
                DuplicateProbability = maxDuplicateProb,
                JevScore = calibratedScore,
                PriorityLabel = priorityLabel,
                TargetColumn = "Discard",
                Confidence = maxDuplicateProb,
                Status = TriageStatus.SuppressedDuplicate,
                Reason = $"Suppressed P3 duplicate of existing issue #{uncertainMatchIssueNum} (P={maxDuplicateProb:P0} >= {thresholds.HumanReviewConfidence:P0})"
            };
        }

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

        TriageEngineHelper.EnforceMaxIssuesPerRunCap(decisions, config.Triage.MaxIssuesPerRun);
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
            var prio = TriageEngineHelper.MapScoreToPriority(calibratedScore);

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

        TriageEngineHelper.EnforceMaxInlineCommentsCap(decisions, config.Github.PullRequests.MaxInlineCommentsPerPr, existingComments.Count);
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
