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
}
