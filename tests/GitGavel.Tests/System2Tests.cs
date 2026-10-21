using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.Git;
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
    public async Task SubagentOrchestrator_RunsAllEnabledSubagentsConcurrently()
    {
        var mockClient = new MockLlmClient(req =>
        {
            var agentName = req.Prompt.Contains("Security") ? "SecurityAuditor" :
                            req.Prompt.Contains("Spec") ? "SpecDriftAgent" :
                            req.Prompt.Contains("Test Sentinel") ? "TestSentinelAgent" : "RefactoringAgent";

            var category = req.Prompt.Contains("Security") ? "Security" :
                           req.Prompt.Contains("Spec") ? "SpecDrift" :
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
        result.SubagentResults.Should().HaveCount(4);
        result.AllFindings.Should().HaveCount(4);
        result.TotalPromptTokens.Should().Be(200);
        result.TotalCompletionTokens.Should().Be(120);
    }

    [Fact]
    public async Task SubagentOrchestrator_InPullRequestMode_RunsSameFourSubagentsOnDiffScopedFiles()
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

        result.SubagentResults.Should().HaveCount(4);
        result.AllFindings.Should().HaveCount(4);
        capturedPrompts.Should().HaveCount(4);
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

        capturedPrompts.Should().HaveCount(4);
        var specPrompt = capturedPrompts.Single(p => p.Contains("Spec Drift Agent"));
        specPrompt.Should().Contain("## Section 10: Final Spec Requirement");
        specPrompt.Should().Contain("src/File20.cs");

        var securityPrompt = capturedPrompts.Single(p => p.Contains("Security Auditor Agent"));
        securityPrompt.Should().Contain("src/File20.cs");

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
        result.SubagentResults.Should().HaveCount(4);
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
        var issueSrcLen = exceedLimitByOne ? 6001 : 6000;

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

        capturedPrompts.Should().HaveCount(4);
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

        // Also verify IssueResolutionAgent 6000-character boundary
        capturedPrompts.Clear();
        var issueAgent = new IssueResolutionAgent(mockClient);
        var issueRepoCtx = new RepoContext
        {
            RootDirectory = "/mock",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/IssueFile.cs", "/mock/src/IssueFile.cs", 100, issueSrcLen, new string('X', issueSrcLen), false)],
            SarifFindings = []
        };

        await issueAgent.EvaluateOpenIssuesAsync([new ExistingIssue(1, "Issue 1", "Body", "open")], issueRepoCtx);
        capturedPrompts.Should().ContainSingle();
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
}
