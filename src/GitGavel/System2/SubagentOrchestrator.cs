using System.Diagnostics;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System1.Abstractions;
using GitGavel.System1.Models;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Agents;
using GitGavel.System2.Models;
using GitGavel.System2.Providers;

namespace GitGavel.System2;

public record OrchestrationResult
{
    public List<FindingCandidate> AllFindings { get; init; } = [];
    public List<SubagentResult> SubagentResults { get; init; } = [];
    public IssueResolutionAgentResult? IssueResolution { get; init; }
    public SubagentGatingResult? GatingResult { get; init; }
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

    public async Task<OrchestrationResult> RunAllAsync(
        RepoContext context,
        GitGavelConfig config,
        Action<SubagentProgress>? onProgress,
        ITriageEngine? triageEngine,
        CancellationToken ct = default)
    {
        triageEngine ??= _triageEngine;
        var agents = new List<ISubagent>();

        if (config.Subagents.SpecDrift?.Enabled == true)
        {
            var client = _clientFactory.CreateClient(config.Subagents.SpecDrift, config);
            agents.Add(new SpecDriftAgent(client, config.Subagents.SpecDrift.Model));
        }

        if (config.Subagents.Security?.Enabled == true)
        {
            var client = _clientFactory.CreateClient(config.Subagents.Security, config);
            agents.Add(new SecurityAuditorAgent(client, config.Subagents.Security.Model));
        }

        if (config.Subagents.Reliability?.Enabled == true)
        {
            var client = _clientFactory.CreateClient(config.Subagents.Reliability, config);
            agents.Add(new ReliabilityAgent(client, config.Subagents.Reliability.Model));
        }

        if (config.Subagents.Refactoring?.Enabled == true)
        {
            var client = _clientFactory.CreateClient(config.Subagents.Refactoring, config);
            agents.Add(new RefactoringAgent(client, config.Subagents.Refactoring.Model));
        }

        if (config.Subagents.Tests?.Enabled == true)
        {
            var client = _clientFactory.CreateClient(config.Subagents.Tests, config);
            agents.Add(new TestSentinelAgent(client, config.Subagents.Tests.Model, config.Subagents.Tests.Tiers));
        }

        SubagentGatingResult? gatingResult = null;
        if (triageEngine != null && agents.Count > 0)
        {
            try
            {
                var candidateNames = agents.Select(a => a.Name).ToList();
                gatingResult = await triageEngine.EvaluateSubagentGatingAsync(context, candidateNames, config, ct);

                if (gatingResult?.HasSkippedAgents == true)
                {
                    var skippedSet = gatingResult.SkippedAgentNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var decision in gatingResult.Decisions.Where(d => !d.ShouldRun))
                    {
                        try
                        {
                            onProgress?.Invoke(new SubagentProgress(
                                decision.AgentName,
                                SubagentProgressStatus.Skipped,
                                SkipReason: decision.Reason));
                        }
                        catch
                        {
                            // Progress callback exceptions should never disrupt orchestration
                        }
                    }

                    agents = agents.Where(a => !skippedSet.Contains(a.Name)).ToList();
                }
            }
            catch
            {
                // Never fail orchestration if gating evaluation throws; proceed with configured agents
            }
        }

        var sw = Stopwatch.StartNew();

        foreach (var agent in agents)
        {
            try
            {
                onProgress?.Invoke(new SubagentProgress(agent.Name, SubagentProgressStatus.Started));
            }
            catch
            {
                // Progress callback exceptions should never disrupt orchestration
            }
        }

        // Run subagents in parallel using Task.WhenAll with per-agent progress notification
        var tasks = agents.Select(async agent =>
        {
            var agentSw = Stopwatch.StartNew();
            SubagentResult result;
            SubagentProgressStatus status;
            try
            {
                result = await agent.ExecuteAsync(context, ct);
                agentSw.Stop();
                status = result.IsSuccess ? SubagentProgressStatus.Completed : SubagentProgressStatus.Failed;
            }
            catch (Exception ex)
            {
                agentSw.Stop();
                result = new SubagentResult
                {
                    AgentName = agent.Name,
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    DurationMs = agentSw.ElapsedMilliseconds
                };
                status = SubagentProgressStatus.Failed;
            }

            try
            {
                onProgress?.Invoke(new SubagentProgress(agent.Name, status, result, agentSw.ElapsedMilliseconds));
            }
            catch
            {
                // Progress callback exceptions should never disrupt orchestration
            }

            return result;
        }).ToList();

        var results = await Task.WhenAll(tasks);

        sw.Stop();

        var allFindings = results
            .Where(r => r.IsSuccess && r.Findings != null)
            .SelectMany(r => r.Findings)
            .ToList();

        return new OrchestrationResult
        {
            AllFindings = allFindings,
            SubagentResults = results.ToList(),
            TotalDurationMs = sw.ElapsedMilliseconds,
            GatingResult = gatingResult
        };
    }

    public async Task<IssueResolutionAgentResult> EvaluateIssueResolutionsAsync(
        IReadOnlyList<ExistingIssue> openIssues,
        RepoContext repoContext,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        if (config.Subagents.IssueResolution?.Enabled != true || openIssues.Count == 0)
        {
            return new IssueResolutionAgentResult
            {
                AgentName = "IssueResolutionAuditor",
                Candidates = []
            };
        }

        var client = _clientFactory.CreateClient(config.Subagents.IssueResolution, config);
        var agent = new IssueResolutionAgent(client, config.Subagents.IssueResolution.Model);
        return await agent.EvaluateOpenIssuesAsync(openIssues, repoContext, ct);
    }
}
