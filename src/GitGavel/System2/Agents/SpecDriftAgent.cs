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
        if (context.IsPullRequestMode && context.ActivePullRequest != null)
        {
            promptBuilder.AppendLine($"PULL REQUEST REVIEW MODE (PR #{context.ActivePullRequest.PrNumber}: {context.ActivePullRequest.Title}): Audit ONLY the changes introduced in this Pull Request's modified files and diff patches.");
        }
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Specification Documents:");
        foreach (var spec in context.SpecFiles)
        {
            promptBuilder.AppendLine($"--- File: {spec.RelativePath} ---");
            promptBuilder.AppendLine(spec.Content.Length > 32000 ? spec.Content[..32000] + "\n...(truncated)" : spec.Content);
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Repository Architecture & Symbol Map:");
        var summaries = context.AstSummaries.Count > 0 ? context.AstSummaries : AstAnalyzer.AnalyzeAll(context.SourceFiles);
        foreach (var sum in summaries)
        {
            var symbolList = sum.Symbols.Count > 0
                ? string.Join(", ", sum.Symbols.Select(s => $"{s.Kind} {s.Name}"))
                : "None";
            promptBuilder.AppendLine($"- File: `{sum.RelativePath}` | Symbols: {symbolList}");
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Source Code Implementation:");
        // Provide all scanned source files so the LLM has complete visibility of the implementation
        foreach (var src in context.SourceFiles)
        {
            promptBuilder.AppendLine($"--- File: {src.RelativePath} ---");
            if (!string.IsNullOrWhiteSpace(src.DiffPatch))
            {
                promptBuilder.AppendLine("PR Diff Patch:");
                promptBuilder.AppendLine(src.DiffPatch.Length > 4000 ? src.DiffPatch[..4000] + "\n...(truncated)" : src.DiffPatch);
            }
            promptBuilder.AppendLine(src.Content.Length > 8000 ? src.Content[..8000] + "\n...(truncated)" : src.Content);
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Return a JSON array of findings with the following schema for each item. For 'references', provide 2-3 links in markdown format (e.g. [Title](url)) pointing to official framework/API docs, Stack Overflow topics, or research articles:");
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
            "details": "Specification section referenced vs actual implementation",
            "references": [
              "[Microsoft Learn: REST API Design Guidelines](https://learn.microsoft.com/en-us/azure/architecture/best-practices/api-design)",
              "[Stack Overflow: Managing OpenAPI contract drift](https://stackoverflow.com/questions/tagged/openapi)"
            ]
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
