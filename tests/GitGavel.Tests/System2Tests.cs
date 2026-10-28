using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.Git;
using GitGavel.System1.Engines;
using GitGavel.System1.Models;
using GitGavel.System2;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Agents;
using GitGavel.System2.Models;
using GitGavel.System2.Providers;

namespace GitGavel.Tests;

[Collection("EnvironmentVariables")]
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
    public void FindingParser_ForReliabilityCategory_ProvidesIntelligentDefaultReferences()
    {
        var raw = """
        [
          {
            "title": "Unbounded Memory Cache Growth in Hot Path",
            "summary": "ConcurrentDictionary grows without eviction policy causing OutOfMemoryException.",
            "category": "Reliability",
            "severity": "High",
            "filePath": "src/Cache/MemoryCacheService.cs",
            "startLine": 45,
            "endLine": 60,
            "defectType": "ResourceLeak",
            "suggestedRemediation": "Use IMemoryCache with size limits and eviction policies."
          }
        ]
        """;

        var findings = FindingParser.ParseFindings(raw, "ReliabilityAgent", "Reliability");

        findings.Should().HaveCount(1);
        var f = findings[0];
        f.Category.Should().Be("Reliability");
        f.DefectType.Should().Be("ResourceLeak");
        f.References.Should().NotBeEmpty();
        f.References.Should().Contain(r => r.Contains("Reliability and Exception Handling"));
        f.References.Should().Contain(r => r.Contains("performance"));
    }

    [Fact]
    public async Task ReliabilityAgent_ExecuteAsync_ProducesReliabilityFindings()
    {
        var capturedPrompt = "";
        string? capturedModel = null;
        var mockClient = new MockLlmClient(req =>
        {
            capturedPrompt = req.Prompt;
            capturedModel = req.Model;
            return new LlmResponse
            {
                Content = """
                [
                  {
                    "title": "Unobserved Task Exception and Race Condition",
                    "summary": "Background task is not awaited and accesses mutable shared dictionary.",
                    "category": "Reliability",
                    "severity": "Critical",
                    "defectType": "Concurrency",
                    "filePath": "src/Worker.cs",
                    "startLine": 20,
                    "endLine": 35,
                    "details": "Race condition on dictionary mutation without synchronization.",
                    "suggestedRemediation": "await Task.WhenAll(tasks); and use ConcurrentDictionary.",
                    "references": [
                      "[Microsoft Learn: Async/Await Best Practices](https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)"
                    ]
                  }
                ]
                """,
                PromptTokens = 80,
                CompletionTokens = 45,
                DurationMs = 25,
                ModelUsed = req.Model
            };
        });

        var agent = new ReliabilityAgent(mockClient);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles =
            [
                new ScannedFile("src/Worker.cs", "/mock/src/Worker.cs", 50, 1000, "public class Worker { public void Run() { Task.Run(() => {}); } }", false),
                new ScannedFile("tests/WorkerTests.cs", "/mock/tests/WorkerTests.cs", 30, 600, "public class WorkerTests {}", false, true)
            ],
            TestFiles = [],
            SarifFindings = []
        };

        var result = await agent.ExecuteAsync(context);

        result.IsSuccess.Should().BeTrue();
        result.AgentName.Should().Be("ReliabilityAgent");
        result.Findings.Should().HaveCount(1);
        var finding = result.Findings[0];
        finding.Category.Should().Be("Reliability");
        finding.Severity.Should().Be("Critical");
        finding.DefectType.Should().Be("Concurrency");
        finding.FilePath.Should().Be("src/Worker.cs");
        finding.SuggestedRemediation.Should().Contain("ConcurrentDictionary");

        capturedPrompt.Should().Contain("Reliability & Performance Agent");
        capturedPrompt.Should().Contain("Provable Logic Defects & Bugs");
        capturedPrompt.Should().Contain("src/Worker.cs");
        capturedPrompt.Should().NotContain("tests/WorkerTests.cs");
        capturedModel.Should().Be("gemini-3.1-pro-preview");
    }

    [Fact]
    public async Task ReliabilityAgent_InPullRequestMode_IncludesPrDiffGuidance()
    {
        var capturedPrompt = "";
        var mockClient = new MockLlmClient(req =>
        {
            capturedPrompt = req.Prompt;
            return new LlmResponse
            {
                Content = "[]",
                PromptTokens = 60,
                CompletionTokens = 10,
                DurationMs = 15,
                ModelUsed = req.Model
            };
        });

        var agent = new ReliabilityAgent(mockClient);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles =
            [
                new ScannedFile("src/OrderProcessor.cs", "/mock/src/OrderProcessor.cs", 80, 1500, "public class OrderProcessor {}", false, false, "@@ -10,3 +10,12 @@\n+ var x = list[index];")
            ],
            TestFiles = [],
            SarifFindings = [],
            ActivePullRequest = new PullRequestContext
            {
                PrNumber = 99,
                Title = "Optimize batch processor",
                HeadSha = "abcdef123456",
                ChangedFiles = [new PullRequestChangedFile("src/OrderProcessor.cs", "modified", 10, 0, "patch")]
            }
        };

        var result = await agent.ExecuteAsync(context);

        result.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().Contain("PULL REQUEST REVIEW MODE (PR #99: Optimize batch processor)");
        capturedPrompt.Should().Contain("ANTI-SCOPE-CREEP RULE");
        capturedPrompt.Should().Contain("PR Diff Patch:");
        capturedPrompt.Should().Contain("+ var x = list[index];");
        capturedPrompt.Should().NotContain("public class OrderProcessor {}");
    }

    [Fact]
    public async Task ReliabilityAgent_ExecuteAsync_WhenLlmThrowsHttpRequestException_ReturnsFailureResult()
    {
        var mockClient = new MockLlmClient(_ => throw new HttpRequestException("Gemini API call failed with status code 503 (ServiceUnavailable)"));
        var agent = new ReliabilityAgent(mockClient);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 20, 400, "public class App {}", false)],
            TestFiles = [],
            SarifFindings = []
        };

        var result = await agent.ExecuteAsync(context);

        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.AgentName.Should().Be("ReliabilityAgent");
        result.ErrorMessage.Should().Contain("Gemini API call failed with status code 503");
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReliabilityAgent_ExecuteAsync_WhenLlmThrowsTimeoutException_ReturnsFailureResult()
    {
        var mockClient = new MockLlmClient(_ => throw new TimeoutException("The LLM request timed out after 300 seconds."));
        var agent = new ReliabilityAgent(mockClient);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/Service.cs", "/mock/src/Service.cs", 30, 600, "public class Service {}", false)],
            TestFiles = [],
            SarifFindings = []
        };

        var result = await agent.ExecuteAsync(context);

        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.AgentName.Should().Be("ReliabilityAgent");
        result.ErrorMessage.Should().Contain("timed out after 300 seconds");
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReliabilityAgent_ExecuteAsync_WithNullAstSummariesOrNullSourceFiles_DoesNotThrow()
    {
        var mockClient = new MockLlmClient(req => new LlmResponse
        {
            Content = "[]",
            PromptTokens = 10,
            CompletionTokens = 5,
            DurationMs = 5,
            ModelUsed = req.Model
        });

        var agent = new ReliabilityAgent(mockClient);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = null!,
            TestFiles = [],
            SarifFindings = [],
            AstSummaries = null!
        };

        var result = await agent.ExecuteAsync(context);

        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task SubagentOrchestrator_RunAllAsync_WhenSubagentConfigIsNull_DoesNotThrow()
    {
        var mockClient = new MockLlmClient(req => new LlmResponse
        {
            Content = "[]",
            PromptTokens = 10,
            CompletionTokens = 5,
            DurationMs = 5,
            ModelUsed = req.Model
        });

        var orchestrator = new SubagentOrchestrator(mockClient);
        var config = new GitGavelConfig();
        config.Subagents.Reliability = null!;

        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 10, 100, "public class App {}", false)],
            TestFiles = [],
            SarifFindings = []
        };

        var result = await orchestrator.RunAllAsync(context, config);

        result.Should().NotBeNull();
        result.SubagentResults.Should().HaveCount(4); // 4 remaining enabled subagents
    }

    [Fact]
    public void FindingParser_DecoupledDefectTypeAndTestDefectType_NoCrossPollution()
    {
        // 1. Only defectType provided
        var rawReliability = """
        [
          {
            "title": "State Overwrite Bug",
            "summary": "Loop overwrites shared variable",
            "category": "Reliability",
            "severity": "High",
            "filePath": "src/Loop.cs",
            "defectType": "LogicDefect"
          }
        ]
        """;
        var findings1 = FindingParser.ParseFindings(rawReliability, "ReliabilityAgent", "Reliability");
        findings1.Should().HaveCount(1);
        findings1[0].DefectType.Should().Be("LogicDefect");
        findings1[0].TestDefectType.Should().BeNull();

        // 2. Only testDefectType provided
        var rawTesting = """
        [
          {
            "title": "Missing Unit Test",
            "summary": "Core service lacks branch coverage",
            "category": "Testing",
            "severity": "Medium",
            "filePath": "tests/ServiceTests.cs",
            "testTier": "Unit",
            "testDefectType": "MissingTest"
          }
        ]
        """;
        var findings2 = FindingParser.ParseFindings(rawTesting, "TestSentinelAgent", "Testing");
        findings2.Should().HaveCount(1);
        findings2[0].TestDefectType.Should().Be("MissingTest");
        findings2[0].DefectType.Should().BeNull();
    }

    [Fact]
    public async Task ReliabilityAgent_InPullRequestMode_OnlyAuditsFilesWithDiffPatches()
    {
        var capturedPrompt = "";
        var mockClient = new MockLlmClient(req =>
        {
            capturedPrompt = req.Prompt;
            return new LlmResponse
            {
                Content = "[]",
                PromptTokens = 50,
                CompletionTokens = 10,
                DurationMs = 10,
                ModelUsed = req.Model
            };
        });

        var agent = new ReliabilityAgent(mockClient);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles =
            [
                new ScannedFile("src/ChangedFile.cs", "/mock/src/ChangedFile.cs", 40, 800, "public class ChangedFile {}", false, false, "@@ -1,3 +1,5 @@\n+ var x = 1;"),
                new ScannedFile("src/UnchangedFile.cs", "/mock/src/UnchangedFile.cs", 100, 2000, "public class UnchangedFile {}", false, false, null)
            ],
            TestFiles = [],
            SarifFindings = [],
            ActivePullRequest = new PullRequestContext
            {
                PrNumber = 76,
                Title = "Test PR filtering",
                HeadSha = "123456",
                ChangedFiles = [new PullRequestChangedFile("src/ChangedFile.cs", "modified", 2, 0, "patch")]
            }
        };

        var result = await agent.ExecuteAsync(context);
        result.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().Contain("src/ChangedFile.cs");
        capturedPrompt.Should().NotContain("src/UnchangedFile.cs");
    }

    [Fact]
    public async Task ReliabilityAgent_CapsPromptAtTwentySourceFiles()
    {
        var capturedPrompt = "";
        var mockClient = new MockLlmClient(req =>
        {
            capturedPrompt = req.Prompt;
            return new LlmResponse
            {
                Content = "[]",
                PromptTokens = 50,
                CompletionTokens = 10,
                DurationMs = 10,
                ModelUsed = req.Model
            };
        });

        var agent = new ReliabilityAgent(mockClient);
        var sourceFiles = Enumerable.Range(1, 25)
            .Select(i => new ScannedFile($"src/File{i:D2}.cs", $"/mock/src/File{i:D2}.cs", 10 + i, 100 * i, $"public class File{i:D2} {{}}", false))
            .ToList();

        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = sourceFiles,
            TestFiles = [],
            SarifFindings = []
        };

        var result = await agent.ExecuteAsync(context);
        result.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().Contain("src/File25.cs");
        capturedPrompt.Should().Contain("src/File06.cs");
        capturedPrompt.Should().NotContain("src/File05.cs");
        capturedPrompt.Should().NotContain("src/File01.cs");
    }

    [Fact]
    public async Task SubagentOrchestrator_RunsAllEnabledSubagentsConcurrently()
    {
        var mockClient = new MockLlmClient(req =>
        {
            var agentName = req.Prompt.Contains("Security") ? "SecurityAuditor" :
                            req.Prompt.Contains("Spec") ? "SpecDriftAgent" :
                            req.Prompt.Contains("Reliability") ? "ReliabilityAgent" :
                            req.Prompt.Contains("Test Sentinel") ? "TestSentinelAgent" : "RefactoringAgent";

            var category = req.Prompt.Contains("Security") ? "Security" :
                           req.Prompt.Contains("Spec") ? "SpecDrift" :
                           req.Prompt.Contains("Reliability") ? "Reliability" :
                           req.Prompt.Contains("Test Sentinel") ? "Testing" : "Refactoring";

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
            TestFiles = [new ScannedFile("tests/ServiceTests.cs", "/mock/tests/ServiceTests.cs", 25, 180, "public class ServiceTests {}", false, true)],
            SarifFindings = []
        };

        var config = new GitGavelConfig();

        var result = await orchestrator.RunAllAsync(context, config);

        result.Should().NotBeNull();
        result.SubagentResults.Should().HaveCount(5);
        result.AllFindings.Should().HaveCount(5);
        result.TotalPromptTokens.Should().Be(250);
        result.TotalCompletionTokens.Should().Be(150);
    }

    [Fact]
    public async Task SubagentOrchestrator_WithOnProgressCallback_EmitsStartedAndCompletedEventsWithTimings()
    {
        var progressEvents = new System.Collections.Concurrent.ConcurrentBag<SubagentProgress>();
        var mockClient = new MockLlmClient(req => new LlmResponse
        {
            Content = "[]",
            PromptTokens = 50,
            CompletionTokens = 30,
            DurationMs = 15,
            ModelUsed = req.Model
        });

        var orchestrator = new SubagentOrchestrator(mockClient);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [new ScannedFile("specs/api.md", "/mock/specs/api.md", 20, 100, "# API Spec", true)],
            SourceFiles = [new ScannedFile("src/Service.cs", "/mock/src/Service.cs", 30, 200, "public class Service {}", false)],
            TestFiles = [],
            SarifFindings = []
        };
        var config = new GitGavelConfig();

        var result = await orchestrator.RunAllAsync(context, config, progressEvents.Add);

        result.Should().NotBeNull();
        result.SubagentResults.Should().HaveCount(5);

        var started = progressEvents.Where(p => p.Status == SubagentProgressStatus.Started).ToList();
        var completed = progressEvents.Where(p => p.Status == SubagentProgressStatus.Completed).ToList();

        started.Should().HaveCount(5);
        completed.Should().HaveCount(5);

        var agentNames = new[] { "SpecDriftAgent", "SecurityAuditor", "ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent" };
        started.Select(s => s.AgentName).Should().BeEquivalentTo(agentNames);
        completed.Select(c => c.AgentName).Should().BeEquivalentTo(agentNames);

        foreach (var c in completed)
        {
            c.Result.Should().NotBeNull();
            c.Result!.IsSuccess.Should().BeTrue();
            c.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public async Task SubagentOrchestrator_WhenSubagentFails_EmitsFailedProgressEvent()
    {
        var progressEvents = new System.Collections.Concurrent.ConcurrentBag<SubagentProgress>();
        var mockClient = new MockLlmClient(req =>
        {
            if (req.Prompt.Contains("Security"))
            {
                throw new HttpRequestException("Gemini API call failed with status code 500");
            }

            return new LlmResponse
            {
                Content = "[]",
                PromptTokens = 20,
                CompletionTokens = 10,
                DurationMs = 5,
                ModelUsed = req.Model
            };
        });

        var orchestrator = new SubagentOrchestrator(mockClient);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/Service.cs", "/mock/src/Service.cs", 30, 200, "public class Service {}", false)],
            SarifFindings = []
        };
        var config = new GitGavelConfig();

        var result = await orchestrator.RunAllAsync(context, config, progressEvents.Add);

        result.Should().NotBeNull();
        result.SubagentResults.Should().HaveCount(5);

        var failed = progressEvents.Where(p => p.Status == SubagentProgressStatus.Failed).ToList();
        failed.Should().HaveCount(1);
        failed[0].AgentName.Should().Be("SecurityAuditor");
        failed[0].Result.Should().NotBeNull();
        failed[0].Result!.IsSuccess.Should().BeFalse();
        failed[0].Result!.ErrorMessage.Should().Contain("Gemini API call failed with status code 500");
    }

    [Fact]
    public async Task SubagentOrchestrator_WhenProgressCallbackThrows_DoesNotFailOrchestration()
    {
        var mockClient = new MockLlmClient(req => new LlmResponse
        {
            Content = "[]",
            PromptTokens = 20,
            CompletionTokens = 10,
            DurationMs = 5,
            ModelUsed = req.Model
        });

        var orchestrator = new SubagentOrchestrator(mockClient);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/Service.cs", "/mock/src/Service.cs", 30, 200, "public class Service {}", false)],
            SarifFindings = []
        };
        var config = new GitGavelConfig();

        // Progress callback throws on every invocation
        var result = await orchestrator.RunAllAsync(context, config, _ => throw new InvalidOperationException("UI render failure"));

        result.Should().NotBeNull();
        result.SubagentResults.Should().HaveCount(5);
        result.SubagentResults.Should().OnlyContain(r => r.IsSuccess, "callback exceptions should not mark successful subagents as failed");
    }

    [Fact]
    public async Task SubagentOrchestrator_DefaultInterfaceImplementation_DelegatesToThreeParamOverload()
    {
        var wasCalled = false;
        var orchestratorMock = new Moq.Mock<ISubagentOrchestrator> { CallBase = true };
        orchestratorMock
            .Setup(o => o.RunAllAsync(Moq.It.IsAny<RepoContext>(), Moq.It.IsAny<GitGavelConfig>(), Moq.It.IsAny<CancellationToken>()))
            .Callback(() => wasCalled = true)
            .Returns(Task.FromResult(new OrchestrationResult()));

        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [],
            SarifFindings = []
        };

        var result = await orchestratorMock.Object.RunAllAsync(context, new GitGavelConfig(), _ => { });

        result.Should().NotBeNull();
        wasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task SubagentOrchestrator_InPullRequestMode_RunsSameFiveSubagentsOnDiffScopedFiles()
    {
        var capturedPrompts = new List<string>();
        var mockClient = new MockLlmClient(req =>
        {
            lock (capturedPrompts)
            {
                capturedPrompts.Add(req.Prompt);
            }

            return new LlmResponse
            {
                Content = """
                [
                  {
                    "title": "PR Diff Finding",
                    "summary": "Issue in modified diff lines",
                    "category": "Security",
                    "severity": "High",
                    "filePath": "src/GitGavel/Git/GitService.cs",
                    "startLine": 70,
                    "endLine": 75,
                    "suggestedRemediation": "if (maxCount <= 0) return [];"
                  }
                ]
                """,
                PromptTokens = 40,
                CompletionTokens = 25,
                DurationMs = 10,
                ModelUsed = req.Model
            };
        });

        var orchestrator = new SubagentOrchestrator(mockClient);

        var prContext = new PullRequestContext
        {
            PrNumber = 42,
            Title = "Fix GitService fallback test isolation",
            HeadSha = "cfad09afc6f436fb3af957ff2d94eba2d3e1ea36",
            ChangedFiles =
            [
                new PullRequestChangedFile("src/GitGavel/Git/GitService.cs", "modified", 15, 2, "@@ -65,2 +65,15 @@")
            ]
        };

        var fullRepoContext = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [new ScannedFile("SPECIFICATION.md", "/mock/SPECIFICATION.md", 20, 100, "# Spec", true)],
            SourceFiles =
            [
                new ScannedFile("src/GitGavel/Git/GitService.cs", "/mock/src/GitGavel/Git/GitService.cs", 80, 1500, "public class GitService {}", false),
                new ScannedFile("src/GitGavel/OtherFile.cs", "/mock/src/GitGavel/OtherFile.cs", 50, 900, "public class OtherFile {}", false)
            ],
            TestFiles = [],
            SarifFindings = []
        };

        var scopedContext = fullRepoContext.ScopeToPullRequest(prContext);
        var result = await orchestrator.RunAllAsync(scopedContext, new GitGavelConfig());

        result.SubagentResults.Should().HaveCount(5);
        result.AllFindings.Should().HaveCount(5);
        capturedPrompts.Should().HaveCount(5);
        capturedPrompts.Should().AllSatisfy(p =>
        {
            p.Should().Contain("PULL REQUEST REVIEW MODE (PR #42");
            p.Should().Contain("GitService.cs");
            p.Should().NotContain("OtherFile.cs");
        });
    }

    [Fact]
    public async Task IssueResolutionAgent_IdentifiesCompletedIssuesAndResolvingCommits()
    {
        var mockClient = new MockLlmClient();
        var agent = new IssueResolutionAgent(mockClient);

        var openIssues = new List<ExistingIssue>
        {
            new(101, "Isolate CI environment variables in GitService fallback test", "Fix test isolation", "open")
        };

        var repoContext = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [],
            SourceFiles = [],
            SarifFindings = [],
            RecentCommits =
            [
                new GitCommitRecord(
                    "cfad09afc6f436fb3af957ff2d94eba2d3e1ea36",
                    "cfad09a",
                    "fix(tests): isolate CI environment variables in GitService fallback test",
                    "Tyrone",
                    "2026-10-20T14:04:33+01:00",
                    ["tests/GitGavel.Tests/GitServiceTests.cs"])
            ]
        };

        var result = await agent.EvaluateOpenIssuesAsync(openIssues, repoContext);

        result.IsSuccess.Should().BeTrue();
        result.Candidates.Should().HaveCount(1);
        var c = result.Candidates[0];
        c.IssueNumber.Should().Be(101);
        c.IsResolved.Should().BeTrue();
        c.HowItWasFixed.Should().Contain("GITHUB_SHA");
        c.ResolvingCommitShas.Should().Contain("cfad09afc6f436fb3af957ff2d94eba2d3e1ea36");
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

    [Fact]
    public void LlmClientFactory_DefaultTimeout_Is300Seconds()
    {
        var factory = new LlmClientFactory();
        var config = new GitGavelConfig();

        var gemini = (GeminiLlmClient)factory.CreateClient(new SubagentSettings { Provider = "gemini" }, config);
        gemini.Timeout.Should().Be(TimeSpan.FromSeconds(300));

        var openai = (OpenAiLlmClient)factory.CreateClient(new SubagentSettings { Provider = "openai" }, config);
        openai.Timeout.Should().Be(TimeSpan.FromSeconds(300));

        var anthropic = (AnthropicLlmClient)factory.CreateClient(new SubagentSettings { Provider = "anthropic" }, config);
        anthropic.Timeout.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void LlmClientFactory_WithGlobalAndPerAgentTimeout_AppliesCorrectTimeout()
    {
        var factory = new LlmClientFactory();
        var config = new GitGavelConfig();
        config.Subagents.TimeoutSeconds = 400;

        // Global config applied
        var geminiGlobal = (GeminiLlmClient)factory.CreateClient(new SubagentSettings { Provider = "gemini" }, config);
        geminiGlobal.Timeout.Should().Be(TimeSpan.FromSeconds(400));

        // Subagent override applied
        var geminiOverride = (GeminiLlmClient)factory.CreateClient(new SubagentSettings { Provider = "gemini", TimeoutSeconds = 600 }, config);
        geminiOverride.Timeout.Should().Be(TimeSpan.FromSeconds(600));
    }

    [Fact]
    public async Task GeminiLlmClient_StripsModelsPrefix_WhenBuildingUrl()
    {
        string? requestedUrl = null;
        IEnumerable<string>? headerValues = null;
        var handler = new SimpleTestHttpMessageHandler(req =>
        {
            requestedUrl = req.RequestUri?.ToString();
            req.Headers.TryGetValues("x-goog-api-key", out headerValues);
            var json = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [{ "text": "{\"findings\": []}" }]
                  }
                }
              ]
            }
            """;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var client = new GeminiLlmClient(apiKey: "test-key", httpClient: new HttpClient(handler));
        var response = await client.GenerateAsync(new LlmRequest
        {
            Prompt = "Hello",
            Model = "models/gemini-3.1-pro-preview"
        });

        response.Content.Should().Be("{\"findings\": []}");
        requestedUrl.Should().Contain("/models/gemini-3.1-pro-preview:generateContent");
        requestedUrl.Should().NotContain("/models/models/");
        requestedUrl.Should().NotContain("?key=");
        requestedUrl.Should().NotContain("test-key");
        headerValues.Should().NotBeNull();
        headerValues.Should().ContainSingle().Which.Should().Be("test-key");
    }

    private class SimpleTestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    [Fact]
    public async Task System2Agents_IncludeFullSpecAndAllSourceFilesWithoutTruncation()
    {
        var capturedPrompts = new List<string>();
        var mockClient = new MockLlmClient(req =>
        {
            lock (capturedPrompts)
            {
                capturedPrompts.Add(req.Prompt);
            }

            return new LlmResponse
            {
                Content = "[]",
                PromptTokens = 50,
                CompletionTokens = 10,
                DurationMs = 5,
                ModelUsed = req.Model
            };
        });

        var longSpecContent = new string('A', 6000) + "\n## Section 10: Final Spec Requirement";
        var sourceFiles = Enumerable.Range(1, 20)
            .Select(i => new ScannedFile($"src/File{i:D2}.cs", $"/mock/src/File{i:D2}.cs", 25, 300, $"public class Class{i:D2} {{}}", false))
            .ToList();

        var repoContext = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [new ScannedFile("SPECIFICATION.md", "/mock/SPECIFICATION.md", 200, longSpecContent.Length, longSpecContent, true)],
            SourceFiles = sourceFiles,
            TestFiles = [new ScannedFile("tests/File20Tests.cs", "/mock/tests/File20Tests.cs", 20, 250, "public class File20Tests {}", false, true)],
            SarifFindings = []
        };

        ISubagentOrchestrator orchestrator = new SubagentOrchestrator(mockClient);
        await orchestrator.RunAllAsync(repoContext, new GitGavelConfig());

        capturedPrompts.Should().HaveCount(5);
        var specPrompt = capturedPrompts.Single(p => p.Contains("Spec Drift Agent"));
        specPrompt.Should().Contain("## Section 10: Final Spec Requirement");
        specPrompt.Should().Contain("src/File20.cs");

        var securityPrompt = capturedPrompts.Single(p => p.Contains("Security Auditor Agent"));
        securityPrompt.Should().Contain("src/File20.cs");

        var reliabilityPrompt = capturedPrompts.Single(p => p.Contains("Reliability & Performance Agent"));
        reliabilityPrompt.Should().Contain("src/File20.cs");

        var testPrompt = capturedPrompts.Single(p => p.Contains("Test Sentinel Agent"));
        testPrompt.Should().Contain("src/File20.cs");
        testPrompt.Should().Contain("tests/File20Tests.cs");
    }

    [Fact]
    public async Task IssueResolutionAgent_EvaluatesIssue1EvenWhenMoreThan25OpenIssuesExist()
    {
        string? capturedPrompt = null;
        var mockClient = new MockLlmClient(req =>
        {
            capturedPrompt = req.Prompt;
            return new LlmResponse
            {
                Content = """
                [
                  {
                    "issueNumber": 1,
                    "issueTitle": "Missing Implementation of Generative LLM Agents (System 2)",
                    "isResolved": true,
                    "howItWasFixed": "Implemented ISubagentOrchestrator, SubagentOrchestrator, and System 2 agents with full repository context visibility.",
                    "resolvingCommitShas": ["d60cdcf"]
                  }
                ]
                """,
                PromptTokens = 100,
                CompletionTokens = 40,
                DurationMs = 10,
                ModelUsed = req.Model
            };
        });

        var agent = new IssueResolutionAgent(mockClient);

        // Simulate 31 open issues returned newest-first (#31 down to #1)
        var openIssues = Enumerable.Range(1, 31)
            .Reverse()
            .Select(n => new ExistingIssue(
                Number: n,
                Title: n == 1 ? "Missing Implementation of Generative LLM Agents (System 2)" : $"Open Issue #{n}",
                Body: $"Description for issue #{n}",
                State: "open"))
            .ToList();

        var repoContext = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [],
            SourceFiles =
            [
                new ScannedFile("src/GitGavel/Program.cs", "/mock/src/GitGavel/Program.cs", 25, 400, "var app = new CommandApp<AuditCommand>();", false),
                new ScannedFile("src/GitGavel/System2/SubagentOrchestrator.cs", "/mock/src/GitGavel/System2/SubagentOrchestrator.cs", 100, 2000, "public class SubagentOrchestrator : ISubagentOrchestrator {}", false)
            ],
            SarifFindings = []
        };

        var result = await agent.EvaluateOpenIssuesAsync(openIssues, repoContext);

        result.IsSuccess.Should().BeTrue();
        result.Candidates.Should().ContainSingle(c => c.IssueNumber == 1 && c.IsResolved);
        capturedPrompt.Should().NotBeNull();
        capturedPrompt!.Should().Contain("--- Issue #1: Missing Implementation of Generative LLM Agents (System 2) ---");
        capturedPrompt.Should().Contain("### Repository Architecture & Symbol Map:");
        capturedPrompt.Should().Contain("SubagentOrchestrator");
    }

    [Fact]
    public async Task SubagentOrchestrator_ParameterlessConstructor_InitializesDefaultFactory()
    {
        var orchestrator = new SubagentOrchestrator();
        var config = new GitGavelConfig();
        config.Subagents.SpecDrift.Provider = "mock";
        config.Subagents.Security.Provider = "mock";
        config.Subagents.Reliability.Provider = "mock";
        config.Subagents.Refactoring.Provider = "mock";
        config.Subagents.Tests.Provider = "mock";

        var repoContext = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [new ScannedFile("SPECIFICATION.md", "/mock/SPECIFICATION.md", 10, 50, "# Spec", true)],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 10, 50, "public class App {}", false)],
            SarifFindings = []
        };

        var result = await orchestrator.RunAllAsync(repoContext, config);
        result.SubagentResults.Should().HaveCount(5);
        result.AllFindings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AuditCommand_WithInjectedOrchestrator_UsesInjectedOrchestratorInstance()
    {
        var customOrchestratorCalled = false;
        var mockOrchestrator = new Moq.Mock<ISubagentOrchestrator>();
        mockOrchestrator
            .Setup(o => o.RunAllAsync(Moq.It.IsAny<RepoContext>(), Moq.It.IsAny<GitGavelConfig>(), Moq.It.IsAny<CancellationToken>()))
            .Callback(() => customOrchestratorCalled = true)
            .Returns(Task.FromResult(new OrchestrationResult { AllFindings = [], SubagentResults = [] }));

        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_cmd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var cmd = new GitGavel.Commands.AuditCommand(orchestrator: mockOrchestrator.Object);
            var remaining = new Moq.Mock<Spectre.Console.Cli.IRemainingArguments>();
            var cmdCtx = new Spectre.Console.Cli.CommandContext([], remaining.Object, "audit", null);
            var settings = new GitGavel.Commands.AuditCommand.Settings
            {
                RepoDirectory = tempDir,
                DryRun = true,
                UseMock = true
            };

            var exitCode = await cmd.ExecuteAsync(cmdCtx, settings, CancellationToken.None);

            exitCode.Should().Be(0);
            customOrchestratorCalled.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task System2Agents_PromptTruncationLimits_EnforceExactBoundaries(bool exceedLimitByOne)
    {
        var capturedPrompts = new List<string>();
        var mockClient = new MockLlmClient(req =>
        {
            lock (capturedPrompts)
            {
                capturedPrompts.Add(req.Prompt);
            }

            return new LlmResponse
            {
                Content = "[]",
                PromptTokens = 10,
                CompletionTokens = 5,
                DurationMs = 1,
                ModelUsed = req.Model
            };
        });

        var specLen = exceedLimitByOne ? 32001 : 32000;
        var srcLen = exceedLimitByOne ? 8001 : 8000;
        var diffLen = exceedLimitByOne ? 4001 : 4000;
        var issueBodyLen = exceedLimitByOne ? 1501 : 1500;

        var repoContext = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [new ScannedFile("SPECIFICATION.md", "/mock/SPECIFICATION.md", 100, specLen, new string('S', specLen), true)],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 100, srcLen, new string('C', srcLen), false, false, new string('D', diffLen))],
            TestFiles = [new ScannedFile("tests/AppTests.cs", "/mock/tests/AppTests.cs", 100, srcLen, new string('T', srcLen), false, true, new string('P', diffLen))],
            SarifFindings = []
        };

        var orchestrator = new SubagentOrchestrator(mockClient);
        await orchestrator.RunAllAsync(repoContext, new GitGavelConfig());

        capturedPrompts.Should().HaveCount(5);
        foreach (var prompt in capturedPrompts)
        {
            if (exceedLimitByOne)
            {
                prompt.Should().Contain("...(truncated)");
            }
            else
            {
                prompt.Should().NotContain("...(truncated)");
            }
        }

        // Also verify IssueResolutionAgent 1500-character issue body boundary and file content omission
        capturedPrompts.Clear();
        var issueAgent = new IssueResolutionAgent(mockClient);
        var issueRepoCtx = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/IssueFile.cs", "/mock/src/IssueFile.cs", 100, 2000, new string('X', 2000), false)],
            SarifFindings = []
        };

        var issueBody = new string('B', issueBodyLen);
        await issueAgent.EvaluateOpenIssuesAsync([new ExistingIssue(1, "Issue 1", issueBody, "open")], issueRepoCtx);
        capturedPrompts.Should().ContainSingle();
        capturedPrompts[0].Should().Contain("(File contents omitted for context size limits. Rely on the AST Symbol Map and Git Commit History above to determine issue resolution.)");
        capturedPrompts[0].Should().NotContain("--- File: src/IssueFile.cs ---");
        if (exceedLimitByOne)
        {
            capturedPrompts[0].Should().Contain("...(truncated)");
        }
        else
        {
            capturedPrompts[0].Should().NotContain("...(truncated)");
        }
    }

    [Fact]
    public async Task IssueResolutionAgent_DoesNotAppendSourceAndTestFileContents_PreservingTokenLimitAndMemory()
    {
        string? capturedPrompt = null;
        var mockClient = new MockLlmClient(req =>
        {
            capturedPrompt = req.Prompt;
            return new LlmResponse
            {
                Content = "[]",
                PromptTokens = 50,
                CompletionTokens = 10,
                DurationMs = 5,
                ModelUsed = req.Model
            };
        });

        var issueAgent = new IssueResolutionAgent(mockClient);
        var sourceFiles = Enumerable.Range(1, 10).Select(i =>
            new ScannedFile($"src/LargeFile{i}.cs", $"/mock/src/LargeFile{i}.cs", 1000, 50000, new string('A', 50000), false)
        ).ToList();
        var testFiles = Enumerable.Range(1, 10).Select(i =>
            new ScannedFile($"tests/LargeTest{i}.cs", $"/mock/tests/LargeTest{i}.cs", 1000, 50000, new string('T', 50000), false, true)
        ).ToList();

        var repoContext = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [],
            SourceFiles = sourceFiles,
            TestFiles = testFiles,
            SarifFindings = [],
            RecentCommits =
            [
                new GitCommitRecord("cfad09afc6f436fb3af957ff2d94eba2d3e1ea36", "cfad09a", "fix(bug): resolve issue 101", "Developer", "2026-10-20T14:04:33Z", ["src/LargeFile1.cs"])
            ]
        };

        var issues = new List<ExistingIssue>
        {
            new(101, "Fix memory leak in parser", "Details about leak", "open")
        };

        var result = await issueAgent.EvaluateOpenIssuesAsync(issues, repoContext);

        result.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().NotBeNull();
        capturedPrompt!.Should().Contain("(File contents omitted for context size limits. Rely on the AST Symbol Map and Git Commit History above to determine issue resolution.)");
        capturedPrompt.Should().NotContain("--- File: src/LargeFile");
        capturedPrompt.Should().NotContain(new string('A', 100));
        capturedPrompt.Should().NotContain(new string('T', 100));
        // Ensure prompt length remains compact (under 10KB), avoiding token exhaustion from 1,000,000+ characters of raw code
        capturedPrompt.Length.Should().BeLessThan(10000);
    }

    [Fact]
    public async Task IssueResolutionAgent_FiltersOutUnresolvedItemsAndAttributesHeadCommitWhenShasOmitted()
    {
        var mockClient = new MockLlmClient(_ => new LlmResponse
        {
            Content = """
            [
              {
                "issueNumber": 2,
                "issueTitle": "Missing Implementation of Jev Decision Engine (System 1)",
                "isResolved": true,
                "howItWasFixed": "TypeSafeJevEngine was implemented with Noul, Score, and Choice primitives.",
                "resolvingCommitShas": []
              },
              {
                "issueNumber": 99,
                "issueTitle": "Unresolved Future Feature",
                "isResolved": false,
                "howItWasFixed": "",
                "resolvingCommitShas": []
              }
            ]
            """,
            PromptTokens = 50,
            CompletionTokens = 25,
            DurationMs = 5,
            ModelUsed = "gemini-2.5-flash"
        });

        var agent = new IssueResolutionAgent(mockClient);
        var repoCtx = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [],
            SourceFiles = [],
            SarifFindings = [],
            RecentCommits =
            [
                new GitCommitRecord(
                    "67d3429273714987ff04e9a131cdff44503c7d41",
                    "67d3429",
                    "feat: implement Phase 2 System 1 Jev Triage Engine",
                    "Tyrone",
                    "2026-10-20T12:00:00Z",
                    ["src/GitGavel/System1/Engines/TypeSafeJevEngine.cs"])
            ]
        };

        var openIssues = new List<ExistingIssue>
        {
            new(2, "Missing Implementation of Jev Decision Engine (System 1)", "Body", "open"),
            new(99, "Unresolved Future Feature", "Body", "open")
        };

        var result = await agent.EvaluateOpenIssuesAsync(openIssues, repoCtx);

        result.IsSuccess.Should().BeTrue();
        result.Candidates.Should().ContainSingle();
        result.Candidates[0].IssueNumber.Should().Be(2);
        result.Candidates[0].ResolvingCommitShas.Should().ContainSingle().Which.Should().Be("67d3429273714987ff04e9a131cdff44503c7d41");
        result.Candidates[0].ResolvingCommitSummaries.Should().ContainSingle().Which.Should().Contain("`67d3429`");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("test-gh-token-dryrun", false)]
    public async Task AuditCommand_DryRunAndZeroFindings_ResolvesGitHubServiceByTokenAndPassesFullRepoContextToIssueResolution(
        string? ghTokenValue,
        bool useMockFlag)
    {
        var prevGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var prevGithub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        RepoContext? capturedRunAllContext = null;
        RepoContext? capturedIssueResContext = null;

        var mockOrchestrator = new Moq.Mock<ISubagentOrchestrator>();
        mockOrchestrator
            .Setup(o => o.RunAllAsync(Moq.It.IsAny<RepoContext>(), Moq.It.IsAny<GitGavelConfig>(), Moq.It.IsAny<CancellationToken>()))
            .Callback<RepoContext, GitGavelConfig, CancellationToken>((ctx, _, _) => capturedRunAllContext = ctx)
            .Returns(Task.FromResult(new OrchestrationResult { AllFindings = [], SubagentResults = [] }));

        mockOrchestrator
            .Setup(o => o.EvaluateIssueResolutionsAsync(
                Moq.It.IsAny<IReadOnlyList<ExistingIssue>>(),
                Moq.It.IsAny<RepoContext>(),
                Moq.It.IsAny<GitGavelConfig>(),
                Moq.It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ExistingIssue>, RepoContext, GitGavelConfig, CancellationToken>((_, ctx, _, _) => capturedIssueResContext = ctx)
            .Returns(Task.FromResult(new IssueResolutionAgentResult
            {
                AgentName = "IssueResolutionAuditor",
                Candidates =
                [
                    new IssueResolutionCandidate
                    {
                        IssueNumber = 101,
                        IssueTitle = "Isolate CI environment variables in GitService fallback test",
                        IsResolved = true,
                        HowItWasFixed = "Cleared CI env vars in GitServiceTests",
                        ResolvingCommitShas = ["cfad09afc6f436fb3af957ff2d94eba2d3e1ea36"]
                    }
                ],
                IsSuccess = true
            }));

        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_dryrun_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "src", "UnchangedFile.cs"), "public class UnchangedFile {}");
        await File.WriteAllTextAsync(Path.Combine(tempDir, ".gitgavel.yml"), "triage:\n  engine: llm-synthesizer\n");

        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", ghTokenValue);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

            var cmd = new GitGavel.Commands.AuditCommand(orchestrator: mockOrchestrator.Object);
            var remaining = new Moq.Mock<Spectre.Console.Cli.IRemainingArguments>();
            var cmdCtx = new Spectre.Console.Cli.CommandContext([], remaining.Object, "audit", null);
            var settings = new GitGavel.Commands.AuditCommand.Settings
            {
                RepoDirectory = tempDir,
                DryRun = true,
                UseMock = useMockFlag,
                PullRequestNumber = useMockFlag ? 42 : null
            };

            var exitCode = await cmd.ExecuteAsync(cmdCtx, settings, CancellationToken.None);

            exitCode.Should().Be(0);
            capturedRunAllContext.Should().NotBeNull();

            if (useMockFlag)
            {
                // In PR mode, RunAllAsync receives PR-scoped context while EvaluateIssueResolutionsAsync receives fullRepoContext (including UnchangedFile.cs)
                capturedRunAllContext!.IsPullRequestMode.Should().BeTrue();
                capturedIssueResContext.Should().NotBeNull();
                capturedIssueResContext!.SourceFiles.Should().Contain(f => f.RelativePath.Contains("UnchangedFile.cs"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", prevGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", prevGithub);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SubagentOrchestrator_WithGatingEngine_ExecutesOnlyActiveAgentsAndReportsSkipped()
    {
        var skippedEvents = new System.Collections.Concurrent.ConcurrentBag<SubagentProgress>();
        var executedAgentPrompts = new System.Collections.Concurrent.ConcurrentBag<string>();

        var mockClient = new MockLlmClient(req =>
        {
            executedAgentPrompts.Add(req.Prompt);
            return new LlmResponse
            {
                Content = "[]",
                PromptTokens = 25,
                CompletionTokens = 10,
                DurationMs = 5,
                ModelUsed = req.Model
            };
        });

        var triageEngine = new MockTriageEngine();
        var orchestrator = new SubagentOrchestrator(mockClient, triageEngine);

        var prContext = new PullRequestContext
        {
            PrNumber = 88,
            Title = "docs: update API specification",
            Body = "Update specs",
            BaseRef = "main",
            HeadRef = "docs/specs",
            HeadSha = "1122334",
            ChangedFiles = [new PullRequestChangedFile("specs/api.md", "modified", 10, 0, "@@ -1,5 +1,15 @@")]
        };

        var repoContext = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [new ScannedFile("specs/api.md", "/mock/specs/api.md", 20, 200, "# API", true, DiffPatch: "@@ -1,5 +1,15 @@")],
            SourceFiles = [],
            SarifFindings = [],
            ActivePullRequest = prContext
        };

        var config = new GitGavelConfig
        {
            Triage = new TriageConfig
            {
                SubagentGating = new SubagentGatingConfig
                {
                    Enabled = true,
                    BypassInFullRepoMode = true,
                    AlwaysRunSpecDriftOnSpecChanges = true
                }
            }
        };

        Action<SubagentProgress> onProgress = p =>
        {
            if (p.Status == SubagentProgressStatus.Skipped)
            {
                skippedEvents.Add(p);
            }
        };

        var result = await orchestrator.RunAllAsync(repoContext, config, onProgress, CancellationToken.None);

        result.Should().NotBeNull();
        result.GatingResult.Should().NotBeNull();
        result.GatingResult!.HasSkippedAgents.Should().BeTrue();
        result.GatingResult.ActiveAgentNames.Should().ContainSingle().Which.Should().Be("SpecDriftAgent");
        result.GatingResult.EstimatedTokensSaved.Should().Be(4 * 5000L);

        // SubagentResults should only contain the active SpecDriftAgent
        result.SubagentResults.Should().ContainSingle().Which.AgentName.Should().Be("SpecDriftAgent");

        // Skipped events should have been emitted for the 4 skipped agents
        skippedEvents.Select(s => s.AgentName).Should().BeEquivalentTo(["SecurityAuditor", "ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent"]);
        foreach (var s in skippedEvents)
        {
            s.SkipReason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void FindingParser_WhenJsonArrayIsTruncated_SalvagesCompletedFindingsAndEmitsWarning()
    {
        var truncatedJson = """
        [
          {
            "title": "Completed First Finding",
            "summary": "This finding object is complete.",
            "category": "Reliability",
            "severity": "High",
            "filePath": "src/Service.cs",
            "startLine": 10,
            "endLine": 20,
            "suggestedRemediation": "Fix 1"
          },
          {
            "title": "Truncated Second Finding",
            "summary": "This object was cut off mid-stream due to max_tokens
        """;

        string? capturedWarning = null;
        var findings = FindingParser.ParseFindings(truncatedJson, "ReliabilityAgent", "Reliability", w => capturedWarning = w);

        findings.Should().ContainSingle();
        findings[0].Title.Should().Be("Completed First Finding");
        capturedWarning.Should().NotBeNullOrWhiteSpace();
        capturedWarning.Should().Contain("Salvaged truncated JSON");
    }

    [Fact]
    public void FindingParser_ParseIssueResolutions_CoversAllSchemaVariantsAndEdgeCases()
    {
        var wrappedJson = """
        ```json
        {
          "resolutions": [
            {
              "number": "42",
              "title": "Fix String Int And Bool Parsing",
              "resolved": "true",
              "explanation": "Resolved via fallback property names",
              "commitSha": "abcdef1234567"
            },
            {
              "issueNumber": 0,
              "issueTitle": "Invalid Zero Issue Number Should Be Skipped",
              "isResolved": true
            }
          ]
        }
        ```
        """;

        var parserService = new FindingParserService();
        var candidates = parserService.ParseIssueResolutions(wrappedJson);

        candidates.Should().ContainSingle();
        candidates[0].IssueNumber.Should().Be(42);
        candidates[0].IssueTitle.Should().Be("Fix String Int And Bool Parsing");
        candidates[0].IsResolved.Should().BeTrue();
        candidates[0].HowItWasFixed.Should().Be("Resolved via fallback property names");
        candidates[0].ResolvingCommitShas.Should().ContainSingle().Which.Should().Be("abcdef1234567");

        // Empty or invalid JSON returns empty list and invokes warning
        parserService.ParseIssueResolutions("").Should().BeEmpty();
        string? warn = null;
        parserService.ParseIssueResolutions("[{broken json", w => warn = w).Should().BeEmpty();
        warn.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SubagentOrchestrator_EvaluateIssueResolutionsAsync_RespectsDisabledConfigAndEmptyIssues()
    {
        var orchestrator = new SubagentOrchestrator(new MockLlmClient());
        var repoContext = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [],
            SourceFiles = [],
            SarifFindings = []
        };

        var disabledConfig = new GitGavelConfig();
        disabledConfig.Subagents.IssueResolution.Enabled = false;

        var openIssues = new List<ExistingIssue> { new(101, "Open Issue", "Body", "open") };

        // 1. Disabled in config -> returns empty
        var resDisabled = await orchestrator.EvaluateIssueResolutionsAsync(openIssues, repoContext, disabledConfig);
        resDisabled.Candidates.Should().BeEmpty();

        // 2. Enabled but empty issues -> returns empty
        var enabledConfig = new GitGavelConfig();
        var resEmpty = await orchestrator.EvaluateIssueResolutionsAsync([], repoContext, enabledConfig);
        resEmpty.Candidates.Should().BeEmpty();

        // 3. Enabled with open issues -> evaluates and returns candidates
        var resActive = await orchestrator.EvaluateIssueResolutionsAsync(openIssues, repoContext, enabledConfig);
        resActive.IsSuccess.Should().BeTrue();
        resActive.Candidates.Should().ContainSingle(c => c.IssueNumber == 101);
    }
}
