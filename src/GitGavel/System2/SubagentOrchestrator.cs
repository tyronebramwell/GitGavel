using System.Diagnostics;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Agents;
using GitGavel.System2.Models;

namespace GitGavel.System2;

public record OrchestrationResult
{
    public List<FindingCandidate> AllFindings { get; init; } = [];
    public List<SubagentResult> SubagentResults { get; init; } = [];
    public int TotalPromptTokens => SubagentResults.Sum(s => s.PromptTokens);
    public int TotalCompletionTokens => SubagentResults.Sum(s => s.CompletionTokens);
    public int TotalTokens => TotalPromptTokens + TotalCompletionTokens;
    public long TotalDurationMs { get; init; }
}

public class SubagentOrchestrator
{
    private readonly ILlmClient _llmClient;

    public SubagentOrchestrator(ILlmClient llmClient)
    {
        _llmClient = llmClient;
    }

    public async Task<OrchestrationResult> RunAllAsync(RepoContext context, GitGavelConfig config, CancellationToken ct = default)
    {
        var agents = new List<ISubagent>();

        if (config.Subagents.SpecDrift.Enabled)
        {
            agents.Add(new SpecDriftAgent(_llmClient, config.Subagents.SpecDrift.Model));
        }

        if (config.Subagents.Security.Enabled)
        {
            agents.Add(new SecurityAuditorAgent(_llmClient, config.Subagents.Security.Model));
        }

        if (config.Subagents.Refactoring.Enabled)
        {
            agents.Add(new RefactoringAgent(_llmClient, config.Subagents.Refactoring.Model));
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
}
