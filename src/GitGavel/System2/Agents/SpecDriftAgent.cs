using System.Text;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Agents;

public class SpecDriftAgent(ILlmClient llmClient, string model = LlmModelDefaults.DefaultGeminiProModel)
    : BaseLlmAgent(llmClient, LlmModelDefaults.ResolveSubagentModel(model, LlmModelDefaults.DefaultGeminiProModel))
{
    public override string Name => "SpecDriftAgent";
    protected override string DefaultCategory => "SpecDrift";
    protected override string SystemPrompt => "You are an expert software architect auditing specification compliance. Return strictly a JSON array of findings.";

    protected override bool ShouldSkipExecution(RepoContext context) => (context.SpecFiles?.Count ?? 0) == 0;

    protected override Task<string> BuildPromptAsync(RepoContext context, CancellationToken ct)
    {
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Spec Drift Agent. Compare the repository specifications/documentation against the actual source code implementation.");
        promptBuilder.AppendLine("Identify any missing endpoints, divergent parameters, unimplemented requirements, or schema drift.");
        promptBuilder.AppendLine("IMPORTANT: Include documentation drift (e.g., README.md, SPECIFICATION.md, docs/) where documented CLI commands, options, features, architecture, or configurations are missing, outdated, or contradict the actual codebase.");
        promptBuilder.AppendLine("IMPORTANT: Do NOT report compliant implementations or positive alignments where no remediation is needed; return [] if all changes match the specification.");

        AgentPromptBuilder.AppendPullRequestGuidance(
            promptBuilder,
            context,
            "ANTI-SCOPE-CREEP RULE: Only report Critical, High, or Medium severity specification contradictions introduced by this PR. Do NOT report Low-severity nits, test file details, or compliant changes; return [] if no substantive spec drift exists.");

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Specification Documents:");
        foreach (var spec in context.SpecFiles ?? [])
        {
            promptBuilder.AppendLine($"--- File: {spec.RelativePath} ---");
            promptBuilder.AppendLine(AgentPromptBuilder.Truncate(spec.Content, AgentPromptBuilder.MaxSpecLength));
        }

        var summaries = AgentPromptBuilder.ResolveAstSummaries(context);
        AgentPromptBuilder.AppendSymbolMap(promptBuilder, summaries);
        AgentPromptBuilder.AppendFilesSection(promptBuilder, context.SourceFiles ?? [], "### Source Code Implementation:");

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

        return Task.FromResult(promptBuilder.ToString());
    }
}
