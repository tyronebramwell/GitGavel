using System.Text;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Agents;

public class RefactoringAgent(ILlmClient llmClient, string model = LlmModelDefaults.DefaultGeminiFlashModel)
    : BaseLlmAgent(llmClient, LlmModelDefaults.ResolveSubagentModel(model, LlmModelDefaults.DefaultGeminiFlashModel))
{
    public override string Name => "RefactoringAgent";
    protected override string DefaultCategory => "Refactoring";
    protected override string SystemPrompt => "You are a software architect specializing in clean code and refactoring. Output strictly a JSON array of findings.";

    protected override Task<string> BuildPromptAsync(RepoContext context, CancellationToken ct)
    {
        var summaries = AgentPromptBuilder.ResolveAstSummaries(context);
        var hotspots = summaries.Where(s => s.IsComplexityHotspot).ToList();

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Architecture & Refactoring Agent. Flag real architectural debt, tight coupling across architectural boundaries, large code duplication, and severe maintainability issues.");
        promptBuilder.AppendLine("NOTE: Focus only on high-confidence, actionable architectural debt and anti-patterns. The project uses lightweight multi-language heuristics, standard DI defaults, and modular command patterns by architectural design; do not flag these intentional design decisions as architectural debt.");

        AgentPromptBuilder.AppendPullRequestGuidance(
            promptBuilder,
            context,
            "ANTI-SCOPE-CREEP RULE: Only report High or Medium severity architectural defects introduced in this PR. Do NOT flag Low-severity nits, do NOT nitpick test files (*Tests.cs), and do NOT suggest extracting extra interfaces/services for newly added helper methods; return [] if the PR changes are clean.");

        AgentPromptBuilder.AppendSymbolMap(promptBuilder, summaries, includeComplexity: true);

        if (hotspots.Count > 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("### Detected Complexity Hotspots:");
            foreach (var spot in hotspots)
            {
                promptBuilder.AppendLine($"- Hotspot: `{spot.RelativePath}` (Complexity score: {spot.TotalComplexityScore}, Symbols: {spot.Symbols.Count})");
            }
        }

        var hotspotPaths = new HashSet<string>(hotspots.Select(h => h.RelativePath));
        var prioritizedFiles = (context.SourceFiles ?? [])
            .OrderByDescending(f => hotspotPaths.Contains(f.RelativePath))
            .ThenByDescending(f => f.LineCount);

        AgentPromptBuilder.AppendFilesSection(promptBuilder, prioritizedFiles, "### Source Code Implementation:");

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Return a JSON array of findings with the following schema for each item. For 'references', provide 2-3 links in markdown format (e.g. [Title](url)) pointing to refactoring guides, architecture docs, or Stack Overflow topics:");
        promptBuilder.AppendLine("""
        [
          {
            "title": "Concise refactoring title",
            "summary": "Clear explanation of architectural debt or coupling issue",
            "category": "Refactoring",
            "severity": "High | Medium | Low",
            "filePath": "path/to/target/file",
            "startLine": 20,
            "endLine": 60,
            "suggestedRemediation": "Concrete refactoring proposal or design pattern recommendation",
            "details": "Impact on maintainability and testability",
            "references": [
              "[Refactoring Guru: Code Smells and Refactoring Techniques](https://refactoring.guru/refactoring)",
              "[Microsoft Learn: Architectural Principles and Clean Architecture](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/architectural-principles)",
              "[Stack Overflow: Decoupling and dependency injection patterns](https://stackoverflow.com/questions/tagged/dependency-injection)"
            ]
          }
        ]
        """);

        return Task.FromResult(promptBuilder.ToString());
    }
}
