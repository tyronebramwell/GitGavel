using GitGavel.Configuration;
using GitGavel.System1.Abstractions;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.System1.Engines;

public class MockTriageEngine : ITriageEngine
{
    private readonly Func<FindingCandidate, ExistingIssue?, (bool isDuplicate, double prob, double score, string choice)>? _customEvaluator;

    public MockTriageEngine(Func<FindingCandidate, ExistingIssue?, (bool isDuplicate, double prob, double score, string choice)>? customEvaluator = null)
    {
        _customEvaluator = customEvaluator;
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

        var accepted = decisions.Where(d => d.Status == TriageStatus.AcceptedToBacklog).ToList();
        if (accepted.Count > config.Triage.MaxIssuesPerRun)
        {
            var top = accepted.OrderByDescending(d => d.JevScore).Take(config.Triage.MaxIssuesPerRun).ToHashSet();
            for (var i = 0; i < decisions.Count; i++)
            {
                var d = decisions[i];
                if (d.Status == TriageStatus.AcceptedToBacklog && !top.Contains(d))
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
