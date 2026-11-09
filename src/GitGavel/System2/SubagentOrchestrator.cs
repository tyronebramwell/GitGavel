using System.Diagnostics;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System1.Abstractions;
using GitGavel.System1.Models;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Agents;
using GitGavel.System2.Models;
using GitGavel.System2.Providers;
using GitGavel.Workflow;

namespace GitGavel.System2;

public record OrchestrationResult
{
    public List<FindingCandidate> AllFindings { get; init; } = [];
    public List<SubagentResult> SubagentResults { get; init; } = [];
    public IssueResolutionAgentResult? IssueResolution { get; init; }
    public SubagentGatingResult? GatingResult { get; init; }
    public WorkflowSnapshot? WorkflowSnapshot { get; init; }
    public int TotalPromptTokens =>
        SubagentResults.Sum(s => s.PromptTokens) +
        (IssueResolution?.PromptTokens ?? 0);
    public int TotalCompletionTokens =>
        SubagentResults.Sum(s => s.CompletionTokens) +
        (IssueResolution?.CompletionTokens ?? 0);
    public int TotalTokens => TotalPromptTokens + TotalCompletionTokens;
    public long TotalDurationMs { get; init; }
}

public class SubagentOrchestrator : ISubagentOrchestrator
{
    private readonly ILlmClientFactory _clientFactory;
    private readonly ITriageEngine? _triageEngine;

    public SubagentOrchestrator()
        : this(new LlmClientFactory(), triageEngine: null)
    {
    }

    public SubagentOrchestrator(ILlmClient defaultClient, ITriageEngine? triageEngine = null)
        : this(new LlmClientFactory(fallbackClient: defaultClient), triageEngine)
    {
    }

    public SubagentOrchestrator(ILlmClientFactory clientFactory, ITriageEngine? triageEngine = null)
    {
        _clientFactory = clientFactory;
        _triageEngine = triageEngine;
    }

    public Task<OrchestrationResult> RunAllAsync(RepoContext context, GitGavelConfig config, CancellationToken ct = default) =>
        RunAllAsync(context, config, onProgress: null, triageEngine: _triageEngine, ct);

    public Task<OrchestrationResult> RunAllAsync(
        RepoContext context,
        GitGavelConfig config,
        Action<SubagentProgress>? onProgress,
        CancellationToken ct = default) =>
        RunAllAsync(context, config, onProgress, triageEngine: _triageEngine, ct);

    public Task<OrchestrationResult> RunAllAsync(
        RepoContext context,
        GitGavelConfig config,
        Action<SubagentProgress>? onProgress,
        ITriageEngine? triageEngine,
        CancellationToken ct = default) =>
        RunWorkflowAsync(context, config, workflow: null, agentDefinitions: null, onProgress: onProgress, onSnapshotUpdated: null, triageEngine: triageEngine, ct: ct);

    public async Task<OrchestrationResult> RunWorkflowAsync(
        RepoContext context,
        GitGavelConfig config,
        WorkflowConfig? workflow = null,
        IReadOnlyDictionary<string, AgentDefinition>? agentDefinitions = null,
        Action<SubagentProgress>? onProgress = null,
        Action<WorkflowSnapshot>? onSnapshotUpdated = null,
        ITriageEngine? triageEngine = null,
        CancellationToken ct = default)
    {
        triageEngine ??= _triageEngine;
        agentDefinitions ??= AgentDefinitionRegistry.LoadAllDefinitions(config, context.RootDirectory);
        workflow ??= WorkflowLoader.ResolveOrBuildDefault(config, agentDefinitions, context.RootDirectory);

        var stateMachine = new WorkflowStateMachine(workflow, agentDefinitions, _clientFactory, triageEngine);
        var (result, _) = await stateMachine.ExecuteAsync(context, config, onProgress, onSnapshotUpdated, ct);
        return result;
    }

    public async Task<IssueResolutionAgentResult> EvaluateIssueResolutionsAsync(
        IReadOnlyList<ExistingIssue> openIssues,
        RepoContext repoContext,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var defs = AgentDefinitionRegistry.LoadAllDefinitions(config, repoContext.RootDirectory);
        defs.TryGetValue("issueResolution", out var irDef);

        if ((config.Subagents.IssueResolution?.Enabled != true && irDef?.Enabled != true) || openIssues.Count == 0)
        {
            return new IssueResolutionAgentResult
            {
                AgentName = irDef?.Name ?? "IssueResolutionAuditor",
                Candidates = []
            };
        }

        var settingsToUse = (SubagentSettings?)irDef ?? config.Subagents.IssueResolution ?? new IssueResolutionSubagentSettings();
        var client = _clientFactory.CreateClient(settingsToUse, config);
        var agent = new IssueResolutionAgent(client, settingsToUse.Model, irDef);
        return await agent.EvaluateOpenIssuesAsync(openIssues, repoContext, ct);
    }
}
