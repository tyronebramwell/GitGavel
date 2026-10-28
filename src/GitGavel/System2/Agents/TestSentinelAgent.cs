using System.Text;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Agents;

public class TestSentinelAgent : BaseLlmAgent
{
    private readonly IReadOnlyList<string> _tiers;

    public override string Name => "TestSentinelAgent";
    protected override string DefaultCategory => "Testing";
    protected override string SystemPrompt => "You are a principal SDET and test architecture specialist auditing automated test coverage and quality. Output strictly a JSON array of findings.";

    public TestSentinelAgent(ILlmClient llmClient, string model = LlmModelDefaults.DefaultGeminiProModel, IReadOnlyList<string>? tiers = null)
        : base(llmClient, LlmModelDefaults.ResolveSubagentModel(model, LlmModelDefaults.DefaultGeminiProModel))
    {
        _tiers = tiers ?? ["unit", "integration", "e2e"];
    }

    protected override bool ShouldSkipExecution(RepoContext context)
        => (context.SourceFiles?.Count ?? 0) == 0 && (context.TestFiles?.Count ?? 0) == 0;

    protected override Task<string> BuildPromptAsync(RepoContext context, CancellationToken ct)
    {
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Test Sentinel Agent. Audit the repository's automated testing suite across the test pyramid.");
        promptBuilder.AppendLine($"Target Test Tiers: {string.Join(", ", _tiers)}");

        AgentPromptBuilder.AppendPullRequestGuidance(
            promptBuilder,
            context,
            "ANTI-SCOPE-CREEP RULE: Only report High or Medium severity missing tests for completely untested production logic introduced in this PR. If the PR already includes unit/integration tests for the modified production methods, return []. Do NOT request tests for trivial constructors, getters, or tests for existing tests.");

        promptBuilder.AppendLine("Identify:");
        promptBuilder.AppendLine("1. Missing Test Detection (MissingTest): critical production classes, public methods, business rules, or error paths lacking Unit, Integration, or E2E coverage.");
        promptBuilder.AppendLine("2. Extra Scenarios & Boundary Coverage (ExtraScenario): omitted edge cases, boundary conditions, invalid inputs, concurrency/race hazards, and negative assertions.");
        promptBuilder.AppendLine("3. Test Quality & Maintainability (TestQuality): brittle assertions, tautological mocks, flaky patterns (e.g., arbitrary Thread.Sleep), or poor fixture isolation.");

        var summaries = AgentPromptBuilder.ResolveAstSummaries(context);
        AgentPromptBuilder.AppendSymbolMap(promptBuilder, summaries, includeComplexity: true, heading: "### Production Symbol & Complexity Map:");

        if ((context.TestFiles?.Count ?? 0) == 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("### Existing Automated Test Suite Files:");
            promptBuilder.AppendLine("(No automated test files discovered in scope)");
        }
        else
        {
            AgentPromptBuilder.AppendFilesSection(promptBuilder, context.TestFiles!, "### Existing Automated Test Suite Files:", fileLabelPrefix: "Test File");
        }

        AgentPromptBuilder.AppendFilesSection(promptBuilder, context.SourceFiles ?? [], "### Production Source Files Under Audit:", fileLabelPrefix: "Source File");

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

        return Task.FromResult(promptBuilder.ToString());
    }
}
