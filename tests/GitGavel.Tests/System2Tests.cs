using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Agents;
using GitGavel.System2.Providers;

namespace GitGavel.Tests;

public class System2Tests
{
    [Fact]
    public void FindingParser_WithValidJson_ParsesFindingsSuccessfully()
    {
        var json = """
        [
          {
            "title": "Missing Authorization on Admin Endpoint",
            "summary": "The /admin/users endpoint does not enforce role-based authorization.",
            "category": "Security",
            "severity": "High",
            "filePath": "Controllers/AdminController.cs",
            "startLine": 25,
            "endLine": 35,
            "owaspCategory": "A01: Broken Access Control",
            "attackVector": "Unauthenticated user calling admin endpoints directly",
            "suggestedRemediation": "Add [Authorize(Roles = \"Admin\")] attribute."
          }
        ]
        """;

        var findings = FindingParser.ParseFindings(json, "SecurityAuditor", "Security");

        findings.Should().HaveCount(1);
        var f = findings[0];
        f.Title.Should().Be("Missing Authorization on Admin Endpoint");
        f.Category.Should().Be("Security");
        f.Severity.Should().Be("High");
        f.FilePath.Should().Be("Controllers/AdminController.cs");
        f.StartLine.Should().Be(25);
        f.EndLine.Should().Be(35);
        f.OwaspCategory.Should().Be("A01: Broken Access Control");
        f.Fingerprint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void FindingParser_WithMarkdownCodeFences_ExtractsAndParses()
    {
        var raw = """
        Here is the audit output:
        ```json
        [
          {
            "title": "Spec Drift: Missing Query Parameter",
            "summary": "Spec specifies 'pageSize' parameter but controller accepts 'limit'.",
            "category": "SpecDrift",
            "severity": "Medium",
            "filePath": "Controllers/ItemsController.cs",
            "startLine": 12,
            "endLine": 15,
            "suggestedRemediation": "Rename parameter to pageSize."
          }
        ]
        ```
        Hope this helps!
        """;

        var findings = FindingParser.ParseFindings(raw, "SpecDriftAgent", "SpecDrift");

        findings.Should().HaveCount(1);
        findings[0].Title.Should().Be("Spec Drift: Missing Query Parameter");
        findings[0].Category.Should().Be("SpecDrift");
    }

    [Fact]
    public void FindingParser_WithExplicitReferences_ParsesMarkdownAndObjectReferences()
    {
        var raw = """
        [
          {
            "title": "SQL Injection in User Lookup",
            "summary": "Direct concatenation of user parameter into SQL query string.",
            "category": "Security",
            "severity": "Critical",
            "filePath": "src/Repositories/UserRepository.cs",
            "startLine": 45,
            "endLine": 52,
            "owaspCategory": "A03: Injection",
            "suggestedRemediation": "Use parameterized queries.",
            "references": [
              "[OWASP SQL Injection Prevention](https://cheatsheetseries.owasp.org/cheatsheets/SQL_Injection_Prevention_Cheat_Sheet.html)",
              "https://stackoverflow.com/questions/12345/how-to-prevent-sql-injection-in-c-sharp",
              {
                "title": "Microsoft Learn: EF Core Parameterization",
                "url": "https://learn.microsoft.com/en-us/ef/core/querying/raw-sql"
              }
            ]
          }
        ]
        """;

        var findings = FindingParser.ParseFindings(raw, "SecurityAuditor", "Security");

        findings.Should().HaveCount(1);
        var f = findings[0];
        f.References.Should().HaveCount(3);
        f.References[0].Should().Be("[OWASP SQL Injection Prevention](https://cheatsheetseries.owasp.org/cheatsheets/SQL_Injection_Prevention_Cheat_Sheet.html)");
        f.References[1].Should().Be("[Stack Overflow Discussion](https://stackoverflow.com/questions/12345/how-to-prevent-sql-injection-in-c-sharp)");
        f.References[2].Should().Be("[Microsoft Learn: EF Core Parameterization](https://learn.microsoft.com/en-us/ef/core/querying/raw-sql)");
    }

    [Fact]
    public void FindingParser_WithoutReferences_ProvidesIntelligentDefaultReferences()
    {
        var raw = """
        [
          {
            "title": "Unauthenticated Controller Endpoint",
            "summary": "Endpoint does not validate JWT authentication.",
            "category": "Security",
            "severity": "High",
            "filePath": "src/Controllers/SecretController.cs",
            "startLine": 10,
            "endLine": 20,
            "owaspCategory": "A01: Broken Access Control",
            "suggestedRemediation": "Add [Authorize] attribute."
          }
        ]
        """;

        var findings = FindingParser.ParseFindings(raw, "SecurityAuditor", "Security");

        findings.Should().HaveCount(1);
        var f = findings[0];
        f.References.Should().NotBeEmpty();
        f.References.Should().Contain(r => r.Contains("Broken Access Control"));
        f.References.Should().Contain(r => r.Contains("stackoverflow.com"));
    }

    [Fact]
    public async Task SubagentOrchestrator_RunsAllEnabledSubagentsConcurrently()
    {
        var mockClient = new MockLlmClient(req =>
        {
            var agentName = req.Prompt.Contains("Security") ? "SecurityAuditor" :
                            req.Prompt.Contains("Spec") ? "SpecDriftAgent" : "RefactoringAgent";

            var category = req.Prompt.Contains("Security") ? "Security" :
                           req.Prompt.Contains("Spec") ? "SpecDrift" : "Refactoring";

            var json = $$"""
            [
              {
                "title": "{{agentName}} Finding",
                "summary": "Summary for {{agentName}}",
                "category": "{{category}}",
                "severity": "High",
                "filePath": "src/Service.cs",
                "startLine": 10,
                "endLine": 20,
                "suggestedRemediation": "Fix issue"
              }
            ]
            """;

            return new LlmResponse
            {
                Content = json,
                PromptTokens = 50,
                CompletionTokens = 30,
                DurationMs = 15,
                ModelUsed = req.Model
            };
        });

        var orchestrator = new SubagentOrchestrator(mockClient);

        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [new ScannedFile("specs/api.md", "/mock/specs/api.md", 20, 100, "# API Spec", true)],
            SourceFiles = [new ScannedFile("src/Service.cs", "/mock/src/Service.cs", 30, 200, "public class Service {}", false)],
            SarifFindings = []
        };

        var config = new GitGavelConfig();

        var result = await orchestrator.RunAllAsync(context, config);

        result.Should().NotBeNull();
        result.SubagentResults.Should().HaveCount(3);
        result.AllFindings.Should().HaveCount(3);
        result.TotalPromptTokens.Should().Be(150);
        result.TotalCompletionTokens.Should().Be(90);
    }

    [Fact]
    public async Task MockLlmClient_GeneratesRealisticFindingsPerAgent()
    {
        var client = new MockLlmClient();

        var specResp = await client.GenerateAsync(new LlmRequest
        {
            Prompt = "You are the Spec Drift Agent. Compare specifications...",
            Model = "gemini-2.5-pro"
        });
        specResp.Content.Should().Contain("SPECIFICATION.md");
        specResp.Content.Should().Contain("references");
        specResp.Content.Should().Contain("stackoverflow.com");

        var secResp = await client.GenerateAsync(new LlmRequest
        {
            Prompt = "You are the Security Auditor Agent. Audit the repository...",
            Model = "gemini-2.5-pro"
        });
        secResp.Content.Should().Contain("KeyResolver.cs");
        secResp.Content.Should().Contain("references");
        secResp.Content.Should().Contain("OWASP A07:2021");

        var refactResp = await client.GenerateAsync(new LlmRequest
        {
            Prompt = "You are the Refactoring Agent. Analyze complexity...",
            Model = "gemini-2.5-flash"
        });
        refactResp.Content.Should().Contain("AuditCommand.cs");
        refactResp.Content.Should().Contain("references");
        refactResp.Content.Should().Contain("refactoring.guru");
    }

    [Fact]
    public void LlmClientFactory_WithForceMock_ReturnsMockClient()
    {
        var factory = new LlmClientFactory(forceMock: true);
        var config = new GitGavelConfig();
        var client = factory.CreateClient(new SubagentSettings { Provider = "openai" }, config);

        client.Should().BeOfType<MockLlmClient>();
    }

    [Fact]
    public void LlmClientFactory_WithProviders_ResolvesAppropriateClients()
    {
        var factory = new LlmClientFactory();
        var config = new GitGavelConfig();

        var gemini = factory.CreateClient(new SubagentSettings { Provider = "gemini" }, config);
        gemini.Should().BeOfType<GeminiLlmClient>();

        var openai = factory.CreateClient(new SubagentSettings { Provider = "openai" }, config);
        openai.Should().BeOfType<OpenAiLlmClient>();

        var anthropic = factory.CreateClient(new SubagentSettings { Provider = "anthropic" }, config);
        anthropic.Should().BeOfType<AnthropicLlmClient>();

        var mock = factory.CreateClient(new SubagentSettings { Provider = "mock" }, config);
        mock.Should().BeOfType<MockLlmClient>();
    }
}
