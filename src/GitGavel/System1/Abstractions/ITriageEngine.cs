using GitGavel.Configuration;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.System1.Abstractions;

public interface ITriageEngine
{
    Task<TriageDecision> EvaluateFindingAsync(
        FindingCandidate finding,
        IReadOnlyList<ExistingIssue> existingIssues,
        GitGavelConfig config,
        CancellationToken ct = default);

    Task<List<TriageDecision>> TriageAllAsync(
        IReadOnlyList<FindingCandidate> findings,
        IReadOnlyList<ExistingIssue> existingIssues,
        GitGavelConfig config,
        CancellationToken ct = default);
}
