using System.Diagnostics;
using GitGavel.Configuration;
using GitGavel.Discovery;
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
    public PrReviewAuditResult? PrReviewAudit { get; init; }
    public IssueResolutionAgentResult? IssueResolution { get; init; }
    public int TotalPromptTokens =>
        SubagentResults.Sum(s => s.PromptTokens) +
        (PrReviewAudit?.PromptTokens ?? 0) +
        (IssueResolution?.PromptTokens ?? 0);
    public int TotalCompletionTokens =>
        SubagentResults.Sum(s => s.CompletionTokens) +
        (PrReviewAudit?.CompletionTokens ?? 0) +
        (IssueResolution?.CompletionTokens ?? 0);
    public int TotalTokens => TotalPromptTokens + TotalCompletionTokens;
    public long TotalDurationMs { get; init; }
}

public class SubagentOrchestrator
{
    private readonly ILlmClientFactory _clientFactory;

    public SubagentOrchestrator(ILlmClient defaultClient)
        : this(new LlmClientFactory(fallbackClient: defaultClient))
    {
    }

    public SubagentOrchestrator(ILlmClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<OrchestrationResult> RunAllAsync(RepoContext context, GitGavelConfig config, CancellationToken ct = default)
    {
        var agents = new List<ISubagent>();

        if (config.Subagents.SpecDrift.Enabled)
        {
            var client = _clientFactory.CreateClient(config.Subagents.SpecDrift, config);
            agents.Add(new SpecDriftAgent(client, config.Subagents.SpecDrift.Model));
        }

        if (config.Subagents.Security.Enabled)
        {
            var client = _clientFactory.CreateClient(config.Subagents.Security, config);
            agents.Add(new SecurityAuditorAgent(client, config.Subagents.Security.Model));
        }

        if (config.Subagents.Refactoring.Enabled)
        {
            var client = _clientFactory.CreateClient(config.Subagents.Refactoring, config);
            agents.Add(new RefactoringAgent(client, config.Subagents.Refactoring.Model));
        }

        if (config.Subagents.Tests.Enabled)
        {
            var client = _clientFactory.CreateClient(config.Subagents.Tests, config);
            agents.Add(new TestSentinelAgent(client, config.Subagents.Tests.Model, config.Subagents.Tests.Tiers));
        }

        var sw = Stopwatch.StartNew();

        // Run subagents in parallel using Task.WhenAll
        var tasks = agents.Select(agent => agent.ExecuteAsync(context, ct));
        var results = await Task.WhenAll(tasks);

        sw.Stop();

        var allFindings = results
            .Where(r => r.IsSuccess)
            .SelectMany(r => r.Findings)
            .ToList();

        return new OrchestrationResult
        {
            AllFindings = allFindings,
            SubagentResults = results.ToList(),
            TotalDurationMs = sw.ElapsedMilliseconds
        };
    }

    public async Task<PrReviewAuditResult> AuditPullRequestAsync(
        PullRequestContext prContext,
        RepoContext repoContext,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var client = _clientFactory.CreateClient(config.Subagents.PrReview, config);
        var agent = new PrReviewAuditorAgent(client, config.Subagents.PrReview.Model);
        return await agent.AuditPullRequestAsync(prContext, repoContext, config, ct);
    }

    public async Task<IssueResolutionAgentResult> EvaluateIssueResolutionsAsync(
        IReadOnlyList<ExistingIssue> openIssues,
        RepoContext repoContext,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var client = _clientFactory.CreateClient(config.Subagents.IssueResolution, config);
        var agent = new IssueResolutionAgent(client, config.Subagents.IssueResolution.Model);
        return await agent.EvaluateOpenIssuesAsync(openIssues, repoContext, ct);
    }
}
