using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Providers;

public class MockLlmClient : ILlmClient
{
    private readonly Func<LlmRequest, LlmResponse>? _responseHandler;

    public MockLlmClient(Func<LlmRequest, LlmResponse>? responseHandler = null)
    {
        _responseHandler = responseHandler;
    }

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (_responseHandler != null)
        {
            return Task.FromResult(_responseHandler(request));
        }

        string sampleJson;
        var p = request.Prompt;

        if (p.Contains("Spec Drift Agent", StringComparison.OrdinalIgnoreCase))
        {
            sampleJson = """
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
        }
        else if (p.Contains("Security Auditor Agent", StringComparison.OrdinalIgnoreCase))
        {
            sampleJson = """
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
        }
        else
        {
            sampleJson = """
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
        }

        return Task.FromResult(new LlmResponse
        {
            Content = sampleJson,
            PromptTokens = 120,
            CompletionTokens = 85,
            DurationMs = 25,
            ModelUsed = request.Model
        });
    }
}
