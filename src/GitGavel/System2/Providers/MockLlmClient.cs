using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Providers;

public class MockLlmClient : ILlmClient
{
    private readonly Func<LlmRequest, LlmResponse>? _responseHandler;
    private readonly Dictionary<string, string> _agentResponses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Func<LlmRequest, bool> Predicate, string JsonContent)> _customRules = [];

    public MockLlmClient(Func<LlmRequest, LlmResponse>? responseHandler = null)
    {
        _responseHandler = responseHandler;
        InitializeDefaultAgentResponses();
    }

    public void RegisterAgentResponse(string agentName, string jsonContent)
    {
        _agentResponses[agentName] = jsonContent;
    }

    public void RegisterRule(Func<LlmRequest, bool> predicate, string jsonContent)
    {
        _customRules.Add((predicate, jsonContent));
    }

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (_responseHandler != null)
        {
            return Task.FromResult(_responseHandler(request));
        }

        foreach (var (predicate, jsonContent) in _customRules)
        {
            if (predicate(request))
            {
                return Task.FromResult(CreateResponse(jsonContent, request.Model));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.AgentName) &&
            _agentResponses.TryGetValue(request.AgentName, out var agentJson))
        {
            return Task.FromResult(CreateResponse(agentJson, request.Model));
        }

        var inferredAgent = InferAgentFromPrompt(request.Prompt ?? "");
        var sampleJson = _agentResponses.TryGetValue(inferredAgent, out var fallbackJson)
            ? fallbackJson
            : _agentResponses["RefactoringAgent"];

        return Task.FromResult(CreateResponse(sampleJson, request.Model));
    }

    private static string InferAgentFromPrompt(string prompt)
    {
        if (prompt.StartsWith("You are the Spec Drift Agent", StringComparison.OrdinalIgnoreCase))
            return "SpecDriftAgent";
        if (prompt.StartsWith("You are the Security Auditor Agent", StringComparison.OrdinalIgnoreCase))
            return "SecurityAuditor";
        if (prompt.StartsWith("You are the Test Sentinel Agent", StringComparison.OrdinalIgnoreCase))
            return "TestSentinelAgent";
        if (prompt.StartsWith("You are the Issue Resolution Agent", StringComparison.OrdinalIgnoreCase))
            return "IssueResolutionAuditor";
        return "RefactoringAgent";
    }

    private static LlmResponse CreateResponse(string content, string model) => new()
    {
        Content = content,
        PromptTokens = 120,
        CompletionTokens = 85,
        DurationMs = 25,
        ModelUsed = model
    };

    private void InitializeDefaultAgentResponses()
    {
        _agentResponses["SpecDriftAgent"] = """
            [
              {
                "title": "Model Tier Specification Drift in SPECIFICATION.md",
                "summary": "SPECIFICATION.md references legacy gemini-1.5 model targets while runtime configurations have evolved to gemini-2.5 / gemini-3.x tiers.",
                "category": "SpecDrift",
                "severity": "Medium",
                "filePath": "SPECIFICATION.md",
                "startLine": 215,
                "endLine": 224,
                "suggestedRemediation": "Align SPECIFICATION.md model definitions with modern production Gemini releases.",
                "details": "Specification drift detected between documentation and implementation.",
                "references": [
                  "[Semantic Versioning 2.0.0](https://semver.org/)",
                  "[Microsoft Learn: Breaking Changes and API Versioning](https://learn.microsoft.com/en-us/dotnet/standard/base-types/breaking-changes)",
                  "[Stack Overflow: Handling API specification drift in CI/CD](https://stackoverflow.com/questions/tagged/api-design)"
                ]
              }
            ]
            """;

        _agentResponses["SecurityAuditor"] = """
            [
              {
                "title": "Plain-text API Key Storage in Repository Configuration",
                "summary": "Plain-text API keys in .gitgavel.yml pose a credential leakage risk if committed to remote repositories.",
                "category": "Security",
                "severity": "High",
                "filePath": "src/GitGavel/Configuration/KeyResolver.cs",
                "startLine": 12,
                "endLine": 35,
                "suggestedRemediation": "Enforce loading secrets strictly via environment variables or secret vaults in non-local environments.",
                "owaspCategory": "A07:2021-Identification and Authentication Failures",
                "details": "Credentials stored directly in local configuration files can be inadvertently committed.",
                "references": [
                  "[OWASP A07:2021 Identification and Authentication Failures](https://owasp.org/Top10/A07_2021-Identification_and_Authentication_Failures/)",
                  "[Microsoft Learn: Safe storage of app secrets in development](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)",
                  "[Stack Overflow: Storing API secrets securely in .NET](https://stackoverflow.com/questions/tagged/asp.net-core-security)"
                ]
              }
            ]
            """;

        _agentResponses["TestSentinelAgent"] = """
            [
              {
                "title": "Missing Integration & E2E Tests for CLI Audit Pipeline",
                "summary": "AuditCommand pipeline execution lacks end-to-end CLI verification covering report generation and issue closure workflows.",
                "category": "Testing",
                "severity": "High",
                "testTier": "E2E",
                "testDefectType": "MissingTest",
                "filePath": "src/GitGavel/Commands/AuditCommand.cs",
                "startLine": 57,
                "endLine": 150,
                "suggestedRemediation": "[Fact]\npublic async Task AuditCommand_DryRunMock_GeneratesAllFiveReports()\n{\n    var cmd = new AuditCommand();\n    // Assert exit code 0 and generated reports\n}",
                "details": "End-to-end CLI test coverage ensures subagent orchestration, Jev triage, and reporting work cohesively.",
                "references": [
                  "[Microsoft Learn: Unit testing best practices with .NET](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)",
                  "[Martin Fowler: Practical Test Pyramid](https://martinfowler.com/articles/practical-test-pyramid.html)",
                  "[Stack Overflow: Automated test design patterns and assertions](https://stackoverflow.com/questions/tagged/unit-testing)"
                ]
              }
            ]
            """;

        _agentResponses["IssueResolutionAuditor"] = """
            [
              {
                "issueNumber": 101,
                "issueTitle": "Isolate CI environment variables in GitService fallback test",
                "isResolved": true,
                "howItWasFixed": "Cleared GITHUB_SHA, GITHUB_REF_NAME, and GITHUB_REPOSITORY environment variables within a try/finally block in GitServiceTests to prevent CI leakage.",
                "resolvingCommitShas": ["cfad09afc6f436fb3af957ff2d94eba2d3e1ea36"],
                "resolvingCommitSummaries": ["`cfad09a` — *fix(tests): isolate CI environment variables in GitService fallback test*"]
              }
            ]
            """;

        _agentResponses["RefactoringAgent"] = """
            [
              {
                "title": "AuditCommand.ExecuteAsync High Cyclomatic Complexity",
                "summary": "AuditCommand.ExecuteAsync exceeds 200 lines and orchestrates multiple pipeline stages directly.",
                "category": "Refactoring",
                "severity": "Medium",
                "filePath": "src/GitGavel/Commands/AuditCommand.cs",
                "startLine": 45,
                "endLine": 289,
                "suggestedRemediation": "Extract pipeline stages into dedicated coordinator services to improve cohesion and testability.",
                "details": "Method has high cyclomatic complexity and mixes presentation logic with domain orchestration.",
                "references": [
                  "[Refactoring Guru: Large Class & High Cyclomatic Complexity](https://refactoring.guru/smells/large-class)",
                  "[Microsoft Learn: Clean Architecture and Single Responsibility](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/architectural-principles#single-responsibility)",
                  "[Stack Overflow: Refactoring large orchestration methods](https://stackoverflow.com/questions/tagged/refactoring)"
                ]
              }
            ]
            """;

        _agentResponses["ReliabilityAgent"] = _agentResponses["RefactoringAgent"];
    }
}
