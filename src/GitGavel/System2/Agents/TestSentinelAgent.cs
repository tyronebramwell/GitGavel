using System.Text;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Agents;

public class TestSentinelAgent : ISubagent
{
    private readonly ILlmClient _llmClient;
    private readonly string _model;
    private readonly IReadOnlyList<string> _tiers;

    public string Name => "TestSentinelAgent";

    public TestSentinelAgent(ILlmClient llmClient, string model = "gemini-2.5-pro", IReadOnlyList<string>? tiers = null)
    {
        _llmClient = llmClient;
        _model = model;
        _tiers = tiers ?? ["unit", "integration", "e2e"];
    }

    public async Task<SubagentResult> ExecuteAsync(RepoContext context, CancellationToken ct = default)
    {
        if (context.SourceFiles.Count == 0 && context.TestFiles.Count == 0)
        {
            return new SubagentResult
            {
                AgentName = Name,
                Findings = [],
                DurationMs = 0
            };
        }

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Test Sentinel Agent. Audit the repository's automated testing suite across the test pyramid.");
        promptBuilder.AppendLine($"Target Test Tiers: {string.Join(", ", _tiers)}");
        if (context.IsPullRequestMode && context.ActivePullRequest != null)
        {
            promptBuilder.AppendLine($"PULL REQUEST REVIEW MODE (PR #{context.ActivePullRequest.PrNumber}: {context.ActivePullRequest.Title}): Audit ONLY the changes introduced in this Pull Request's modified files and diff patches.");
        }
        promptBuilder.AppendLine("Identify:");
        promptBuilder.AppendLine("1. Missing Test Detection (MissingTest): critical production classes, public methods, business rules, or error paths lacking Unit, Integration, or E2E coverage.");
        promptBuilder.AppendLine("2. Extra Scenarios & Boundary Coverage (ExtraScenario): omitted edge cases, boundary conditions, invalid inputs, concurrency/race hazards, and negative assertions.");
        promptBuilder.AppendLine("3. Test Quality & Maintainability (TestQuality): brittle assertions, tautological mocks, flaky patterns (e.g., arbitrary Thread.Sleep), or poor fixture isolation.");

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Production Symbol & Complexity Map:");
        var summaries = context.AstSummaries.Count > 0 ? context.AstSummaries : AstAnalyzer.AnalyzeAll(context.SourceFiles);
        foreach (var sum in summaries)
        {
            var symbolList = sum.Symbols.Count > 0
                ? string.Join(", ", sum.Symbols.Select(s => $"{s.Kind} {s.Name}"))
                : "None";
            promptBuilder.AppendLine($"- File: `{sum.RelativePath}` | Symbols: {symbolList} | Complexity: {sum.TotalComplexityScore}");
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Existing Automated Test Suite Files:");
        if (context.TestFiles.Count == 0)
        {
            promptBuilder.AppendLine("(No automated test files discovered in scope)");
        }
        else
        {
            foreach (var testFile in context.TestFiles.Take(15))
            {
                promptBuilder.AppendLine($"--- Test File: {testFile.RelativePath} ---");
                if (!string.IsNullOrWhiteSpace(testFile.DiffPatch))
                {
                    promptBuilder.AppendLine("PR Diff Patch:");
                    promptBuilder.AppendLine(testFile.DiffPatch.Length > 3000 ? testFile.DiffPatch[..3000] + "\n...(truncated)" : testFile.DiffPatch);
                }
                promptBuilder.AppendLine(testFile.Content.Length > 4000 ? testFile.Content[..4000] + "\n...(truncated)" : testFile.Content);
            }
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Production Source Files Under Audit:");
        foreach (var src in context.SourceFiles.Take(12))
        {
            promptBuilder.AppendLine($"--- Source File: {src.RelativePath} ---");
            if (!string.IsNullOrWhiteSpace(src.DiffPatch))
            {
                promptBuilder.AppendLine("PR Diff Patch:");
                promptBuilder.AppendLine(src.DiffPatch.Length > 3000 ? src.DiffPatch[..3000] + "\n...(truncated)" : src.DiffPatch);
            }
            promptBuilder.AppendLine(src.Content.Length > 4000 ? src.Content[..4000] + "\n...(truncated)" : src.Content);
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Return a JSON array of findings with the following schema for each item:");
        promptBuilder.AppendLine("""
        [
          {
            "title": "Concise test gap or test quality finding title",
            "summary": "Clear explanation of the untested logic, missing boundary scenario, or test quality defect",
            "category": "Testing",
            "severity": "High | Medium | Low",
            "testTier": "Unit | Integration | E2E",
            "testDefectType": "MissingTest | ExtraScenario | TestQuality",
            "filePath": "path/to/production/or/test/file",
            "startLine": 10,
            "endLine": 40,
            "suggestedRemediation": "Executable test implementation blueprint (e.g. xUnit/NUnit/Playwright snippet)",
            "details": "Why this scenario is critical for test pyramid reliability",
            "references": [
              "[Microsoft Learn: Unit testing best practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)",
              "[Martin Fowler: Practical Test Pyramid](https://martinfowler.com/articles/practical-test-pyramid.html)"
            ]
          }
        ]
        """);

        try
        {
            var response = await _llmClient.GenerateAsync(new LlmRequest
            {
                Prompt = promptBuilder.ToString(),
                SystemPrompt = "You are a principal SDET and test architecture specialist auditing automated test coverage and quality. Output strictly a JSON array of findings.",
                Model = _model,
                ExpectJson = true
            }, ct);

            var findings = FindingParser.ParseFindings(response.Content, Name, "Testing");

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
