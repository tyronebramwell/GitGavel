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
                "details": "Specification drift detected between documentation and implementation."
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
                "details": "Credentials stored directly in local configuration files can be inadvertently committed."
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
                "details": "Method has high cyclomatic complexity and mixes presentation logic with domain orchestration."
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
