using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System1.Models;
using GitGavel.System2.Models;

namespace GitGavel.System1.Abstractions;

public interface ITriageEngine
{
    Task<SubagentGatingResult> EvaluateSubagentGatingAsync(
        RepoContext context,
        IReadOnlyList<string> candidateAgentNames,
        GitGavelConfig config,
        CancellationToken ct = default);

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

    Task<List<PrCommentTriageDecision>> TriagePrReviewCommentsAsync(
        IReadOnlyList<FindingCandidate> findings,
        IReadOnlyList<ExistingPrReviewComment> existingComments,
        GitGavelConfig config,
        CancellationToken ct = default);

    Task<List<IssueResolutionDecision>> VerifyIssueResolutionsAsync(
        IReadOnlyList<IssueResolutionCandidate> candidates,
        IReadOnlyList<ExistingIssue> openIssues,
        GitGavelConfig config,
        CancellationToken ct = default);
}
