using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System1.Abstractions;
using GitGavel.System1.Models;
using GitGavel.System2.Models;
using GitGavel.Workflow;

namespace GitGavel.System2.Abstractions;

public interface ISubagentOrchestrator
{
    Task<OrchestrationResult> RunAllAsync(
        RepoContext context,
        GitGavelConfig config,
        CancellationToken ct = default);

    Task<OrchestrationResult> RunAllAsync(
        RepoContext context,
        GitGavelConfig config,
        Action<SubagentProgress>? onProgress,
        CancellationToken ct = default) => RunAllAsync(context, config, ct);

    Task<OrchestrationResult> RunAllAsync(
        RepoContext context,
        GitGavelConfig config,
        Action<SubagentProgress>? onProgress,
        ITriageEngine? triageEngine,
        CancellationToken ct = default) => RunAllAsync(context, config, onProgress, ct);

    Task<OrchestrationResult> RunWorkflowAsync(
        RepoContext context,
        GitGavelConfig config,
        WorkflowConfig? workflow = null,
        IReadOnlyDictionary<string, AgentDefinition>? agentDefinitions = null,
        Action<SubagentProgress>? onProgress = null,
        Action<WorkflowSnapshot>? onSnapshotUpdated = null,
        ITriageEngine? triageEngine = null,
        CancellationToken ct = default) => RunAllAsync(context, config, onProgress, triageEngine, ct);

    Task<IssueResolutionAgentResult> EvaluateIssueResolutionsAsync(
        IReadOnlyList<ExistingIssue> openIssues,
        RepoContext repoContext,
        GitGavelConfig config,
        CancellationToken ct = default);
}
