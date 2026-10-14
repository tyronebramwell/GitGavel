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
}
