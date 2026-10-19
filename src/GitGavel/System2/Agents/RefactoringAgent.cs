using System.Text;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Agents;

public class RefactoringAgent : ISubagent
{
    private readonly ILlmClient _llmClient;
    private readonly string _model;

    public string Name => "RefactoringAgent";

    public RefactoringAgent(ILlmClient llmClient, string model = "gemini-2.5-flash")
    {
        _llmClient = llmClient;
        _model = model;
    }

    public async Task<SubagentResult> ExecuteAsync(RepoContext context, CancellationToken ct = default)
    {
        var summaries = context.AstSummaries.Count > 0 ? context.AstSummaries : AstAnalyzer.AnalyzeAll(context.SourceFiles);
        var hotspots = summaries.Where(s => s.IsComplexityHotspot).ToList();

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Architecture & Refactoring Agent. Flag real architectural debt, tight coupling across architectural boundaries, large code duplication, and severe maintainability issues.");
        promptBuilder.AppendLine("NOTE: Focus only on high-confidence, actionable architectural debt and anti-patterns. The project uses lightweight multi-language heuristics, standard DI defaults, and modular command patterns by architectural design; do not flag these intentional design decisions as architectural debt.");

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Repository Architecture & Symbol Map:");
        foreach (var sum in summaries)
        {
            var symbolList = sum.Symbols.Count > 0
                ? string.Join(", ", sum.Symbols.Select(s => $"{s.Kind} {s.Name}"))
                : "None";
            promptBuilder.AppendLine($"- File: `{sum.RelativePath}` | Symbols: {symbolList} | Complexity: {sum.TotalComplexityScore}");
        }

        if (hotspots.Count > 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("### Detected Complexity Hotspots:");
            foreach (var spot in hotspots)
            {
                promptBuilder.AppendLine($"- Hotspot: `{spot.RelativePath}` (Complexity score: {spot.TotalComplexityScore}, Symbols: {spot.Symbols.Count})");
            }
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Source Code Implementation:");
        var hotspotPaths = new HashSet<string>(hotspots.Select(h => h.RelativePath));
        var prioritizedFiles = context.SourceFiles
            .OrderByDescending(f => hotspotPaths.Contains(f.RelativePath))
            .ThenByDescending(f => f.LineCount);

        foreach (var src in prioritizedFiles)
        {
            promptBuilder.AppendLine($"--- File: {src.RelativePath} ---");
            promptBuilder.AppendLine(src.Content.Length > 8000 ? src.Content[..8000] + "\n...(truncated)" : src.Content);
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Return a JSON array of findings with the following schema for each item:");
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
            "details": "Impact on maintainability and testability"
          }
        ]
        """);

        try
        {
            var response = await _llmClient.GenerateAsync(new LlmRequest
            {
                Prompt = promptBuilder.ToString(),
                SystemPrompt = "You are a software architect specializing in clean code and refactoring. Output strictly a JSON array of findings.",
                Model = _model,
                ExpectJson = true
            }, ct);

            var findings = FindingParser.ParseFindings(response.Content, Name, "Refactoring");

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
