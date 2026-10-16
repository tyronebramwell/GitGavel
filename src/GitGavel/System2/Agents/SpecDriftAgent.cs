using System.Text;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Agents;

public class SpecDriftAgent : ISubagent
{
    private readonly ILlmClient _llmClient;
    private readonly string _model;

    public string Name => "SpecDriftAgent";

    public SpecDriftAgent(ILlmClient llmClient, string model = "gemini-2.5-pro")
    {
        _llmClient = llmClient;
        _model = model;
    }

    public async Task<SubagentResult> ExecuteAsync(RepoContext context, CancellationToken ct = default)
    {
        if (context.SpecFiles.Count == 0)
        {
            return new SubagentResult
            {
                AgentName = Name,
                Findings = [],
                DurationMs = 0
            };
        }

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Spec Drift Agent. Compare the repository specifications/documentation against the actual source code implementation.");
        promptBuilder.AppendLine("Identify any missing endpoints, divergent parameters, unimplemented requirements, or schema drift.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Specification Documents:");
        foreach (var spec in context.SpecFiles.Take(5))
        {
            promptBuilder.AppendLine($"--- File: {spec.RelativePath} ---");
            promptBuilder.AppendLine(spec.Content.Length > 4000 ? spec.Content[..4000] + "\n...(truncated)" : spec.Content);
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Key Source Code Files:");
        foreach (var src in context.SourceFiles.Take(10))
        {
            promptBuilder.AppendLine($"--- File: {src.RelativePath} ---");
            promptBuilder.AppendLine(src.Content.Length > 3000 ? src.Content[..3000] + "\n...(truncated)" : src.Content);
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Return a JSON array of findings with the following schema for each item:");
        promptBuilder.AppendLine("""
        [
          {
            "title": "Concise issue title",
            "summary": "Clear summary of divergence between spec and code",
            "category": "SpecDrift",
            "severity": "Critical | High | Medium | Low",
            "filePath": "path/to/affected/file",
            "startLine": 10,
            "endLine": 30,
            "suggestedRemediation": "Concrete code snippet or action plan to align code with spec",
            "details": "Specification section referenced vs actual implementation"
          }
        ]
        """);

        try
        {
            var response = await _llmClient.GenerateAsync(new LlmRequest
            {
                Prompt = promptBuilder.ToString(),
                SystemPrompt = "You are an expert software architect auditing specification compliance. Return strictly a JSON array of findings.",
                Model = _model,
                ExpectJson = true
            }, ct);

            var findings = FindingParser.ParseFindings(response.Content, Name, "SpecDrift");

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
