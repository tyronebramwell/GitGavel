using GitGavel.Discovery;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Agents;

public abstract class BaseLlmAgent : ISubagent
{
    protected readonly ILlmClient LlmClient;
    protected readonly string Model;

    public abstract string Name { get; }
    protected abstract string DefaultCategory { get; }
    protected abstract string SystemPrompt { get; }

    protected BaseLlmAgent(ILlmClient llmClient, string model)
    {
        LlmClient = llmClient;
        Model = model;
    }

    protected virtual bool ShouldSkipExecution(RepoContext context) => false;

    protected abstract Task<string> BuildPromptAsync(RepoContext context, CancellationToken ct);

    public async Task<SubagentResult> ExecuteAsync(RepoContext context, CancellationToken ct = default)
    {
        if (ShouldSkipExecution(context))
        {
            return new SubagentResult
            {
                AgentName = Name,
                Findings = [],
                DurationMs = 0
            };
        }

        try
        {
            var prompt = await BuildPromptAsync(context, ct);

            var response = await LlmClient.GenerateAsync(new LlmRequest
            {
                AgentName = Name,
                Prompt = prompt,
                SystemPrompt = SystemPrompt,
                Model = Model,
                ExpectJson = true
            }, ct);

            var findings = FindingParser.ParseFindings(response.Content, Name, DefaultCategory);

            return new SubagentResult
            {
                AgentName = Name,
                Findings = findings,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                DurationMs = response.DurationMs
            };
        }
        catch (Exception ex)
        {
            return new SubagentResult
            {
                AgentName = Name,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
