using FluentAssertions;
using GitGavel.Commands;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.Git;
using GitGavel.Skills;
using GitGavel.System1.Engines;
using GitGavel.System2;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Agents;
using GitGavel.System2.Models;
using GitGavel.System2.Providers;
using GitGavel.Workflow;
using Moq;
using Spectre.Console;

namespace GitGavel.Tests;

public class WorkflowSkillsArtifactsAndYamlAgentsTests
{
    [Fact]
    public void StandardAgentYamlCatalog_ContainsAllSixStandardAgents_WithModifiablePrompts()
    {
        var all = StandardAgentYamlCatalog.GetAllStandardDefinitions();
        all.Should().HaveCount(6);
        all.Select(x => x.Definition.Id).Should().BeEquivalentTo(
            ["specDrift", "security", "reliability", "refactoring", "tests", "issueResolution"]);

        foreach (var (fileName, yaml, def) in all)
        {
            fileName.Should().EndWith(".agent.yml");
            yaml.Should().NotBeNullOrWhiteSpace();
            def.SystemPrompt.Should().NotBeNullOrWhiteSpace();
            def.UserPromptIntro.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void AgentDefinitionRegistry_EjectAndLoadOverride_ModifiesStandardAndRegistersCustomAgents()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel-agents-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var agentsDir = Path.Combine(tempDir, ".gitgavel", "agents");
            var ejected = AgentDefinitionRegistry.EjectStandardAgents(agentsDir, tempDir, force: true);
            ejected.Should().HaveCount(6);

            // Modify security.agent.yml systemPrompt and add a custom agent file
            var secFile = Path.Combine(agentsDir, "security.agent.yml");
            File.WriteAllText(secFile, """
                version: 1
                id: "security"
                name: "CustomSecuritySentinel"
                category: "Security"
                enabled: true
                provider: "gemini"
                model: "gemini-2.5-pro"
                systemPrompt: "CUSTOM OVERRIDDEN SECURITY SYSTEM PROMPT"
                additionalInstructions: "Enforce zero-trust token validation."
                """);

            var customFile = Path.Combine(agentsDir, "api-conventions.agent.yml");
            File.WriteAllText(customFile, """
                version: 1
                id: "apiConventions"
                name: "ApiConventionsAgent"
                category: "Architecture"
                enabled: true
                provider: "gemini"
                model: "gemini-2.5-flash"
                scope:
                  include: ["src/Controllers/**/*.cs"]
                  exclude: ["**/*.Tests/**"]
                systemPrompt: "Audit REST controller conventions."
                """);

            var config = new GitGavelConfig();
            config.Subagents.DefinitionsPath = ".gitgavel/agents";

            var loaded = AgentDefinitionRegistry.LoadAllDefinitions(config, tempDir);
            loaded.Should().ContainKey("security");
            loaded["security"].Name.Should().Be("CustomSecuritySentinel");
            loaded["security"].SystemPrompt.Should().Be("CUSTOM OVERRIDDEN SECURITY SYSTEM PROMPT");
            loaded["security"].AdditionalInstructions.Should().Contain("zero-trust");

            loaded.Should().ContainKey("apiConventions");
            loaded["apiConventions"].Name.Should().Be("ApiConventionsAgent");
            loaded["apiConventions"].Scope!.Include.Should().Contain("src/Controllers/**/*.cs");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PipelineArtifactIngestor_ParsesTrxJunitCoberturaLcovAndBuildsAgentPromptSection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel-artifacts-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var trxFile = Path.Combine(tempDir, "results.trx");
            await File.WriteAllTextAsync(trxFile, """
                <TestRun>
                  <ResultSummary outcome="Failed">
                    <Counters total="10" passed="8" failed="2" notExecuted="0" />
                  </ResultSummary>
                  <Results>
                    <UnitTestResult testName="AuthServiceTests.TokenExpired_Throws" outcome="Failed">
                      <Output>
                        <ErrorInfo>
                          <Message>Expected SecurityTokenExpiredException</Message>
                          <StackTrace>at AuthServiceTests.cs:line 42</StackTrace>
                        </ErrorInfo>
                      </Output>
                    </UnitTestResult>
                  </Results>
                </TestRun>
                """);

            var cobFile = Path.Combine(tempDir, "coverage.cobertura.xml");
            await File.WriteAllTextAsync(cobFile, """
                <coverage line-rate="0.72" branch-rate="0.65">
                  <packages>
                    <package name="Core">
                      <classes>
                        <class name="AuthService" filename="src/AuthService.cs">
                          <lines>
                            <line number="10" hits="1" />
                            <line number="11" hits="0" />
                            <line number="12" hits="0" />
                          </lines>
                        </class>
                      </classes>
                    </package>
                  </packages>
                </coverage>
                """);

            var artifactsCfg = new ArtifactsConfig
            {
                Sources =
                [
                    new ArtifactSourceConfig { Id = "test-results", Type = "trx", Paths = ["results.trx"] },
                    new ArtifactSourceConfig { Id = "coverage-report", Type = "cobertura", Paths = ["coverage.cobertura.xml"] }
                ]
            };

            var ctx = await PipelineArtifactIngestor.IngestAsync(tempDir, artifactsCfg);
            ctx.Artifacts.Should().HaveCount(2);
            ctx.TotalTestFailures.Should().Be(2);
            ctx.TotalTestsExecuted.Should().Be(10);
            ctx.LowestLineCoveragePercent.Should().BeApproximately(72.0, 0.1);

            var promptSection = ctx.BuildPromptSectionForAgent(["test-results", "coverage-report"]);
            promptSection.Should().Contain("AuthServiceTests.TokenExpired_Throws");
            promptSection.Should().Contain("Line Coverage = 72.0%");
            promptSection.Should().Contain("src/AuthService.cs");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SkillResolver_DiscoversLocalAndClonesRemoteSkillsOnDemand_AndInjectsIntoAgentPrompt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel-skills-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Setup local skill folder with SKILL.md
            var localSkillFolder = Path.Combine(tempDir, ".gitgavel", "skills", "owasp-dotnet");
            Directory.CreateDirectory(localSkillFolder);
            await File.WriteAllTextAsync(Path.Combine(localSkillFolder, "SKILL.md"), """
                ---
                name: OWASP .NET Hardening
                description: Security rules for .NET APIs
                ---
                Always parameterize SQL queries and validate JWT audiences.
                """);

            // 2. Setup simulated git clone via mock IProcessRunner
            var mockRunner = new Mock<IProcessRunner>();
            mockRunner
                .Setup(r => r.Run("git", It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("clone")), It.IsAny<int>(), It.IsAny<Action<string>?>()))
                .Returns((string _, string _, IEnumerable<string> args, int _, Action<string>? _) =>
                {
                    var targetCloneDir = args.Last();
                    var skillDir = Path.Combine(targetCloneDir, "skills", "xunit-patterns");
                    Directory.CreateDirectory(skillDir);
                    File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
                        ---
                        name: xUnit Best Practices
                        description: Guidelines for deterministic unit tests
                        ---
                        Avoid Thread.Sleep in tests; use TaskCompletionSource.
                        """);
                    return "Cloned";
                });
            mockRunner
                .Setup(r => r.Run("git", It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("rev-parse")), It.IsAny<int>(), It.IsAny<Action<string>?>()))
                .Returns("a1b2c3d");

            var skillsConfig = new SkillsConfig
            {
                CacheDir = ".gitgavel/cache/skills",
                FetchOnDemand = true,
                Repositories =
                [
                    new SkillRepositoryConfig { Alias = "local", LocalPath = ".gitgavel/skills" },
                    new SkillRepositoryConfig { Alias = "team-skills", Git = "https://github.com/org/skills.git", Ref = "main", Path = "skills" }
                ]
            };

            var resolver = new SkillResolver(mockRunner.Object);
            var skillsCtx = await resolver.ResolveSkillsOnDemandAsync(
                tempDir,
                skillsConfig,
                ["local/owasp-dotnet", "team-skills/xunit-patterns"]);

            skillsCtx.Skills.Should().HaveCount(2);
            skillsCtx.Repositories.Should().Contain(r => r.Alias == "team-skills" && r.WasClonedOnDemand && r.CommitSha == "a1b2c3d");

            var secPromptSection = skillsCtx.BuildPromptSectionForAgent(["local/owasp-dotnet"]);
            secPromptSection.Should().Contain("OWASP .NET Hardening");
            secPromptSection.Should().Contain("Always parameterize SQL queries");
            secPromptSection.Should().NotContain("TaskCompletionSource");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WorkflowLoader_Validate_DetectsCyclesAndUnknownDependencies()
    {
        var cyclicYaml = """
            version: 1
            name: "Cyclic Workflow"
            nodes:
              - id: "nodeA"
                system: "system2"
                dependsOn: ["nodeB"]
              - id: "nodeB"
                system: "system2"
                inputsFrom: ["nodeA"]
            """;

        Action actCycle = () => WorkflowLoader.LoadFromString(cyclicYaml);
        actCycle.Should().Throw<InvalidOperationException>().WithMessage("*Circular dependency cycle*");

        var unknownDepYaml = """
            version: 1
            name: "Unknown Dependency Workflow"
            nodes:
              - id: "nodeA"
                system: "system2"
                dependsOn: ["nonExistentNode"]
            """;

        Action actUnknown = () => WorkflowLoader.LoadFromString(unknownDepYaml);
        actUnknown.Should().Throw<InvalidOperationException>().WithMessage("*unknown upstream dependency*");
    }

    [Fact]
    public async Task WorkflowStateMachine_ResolvesParallelAndSeries_PassesInputsFromData_AndRespectsDependsOnOrderOnly()
    {
        var workflowYaml = """
            version: 1
            name: "Parallel + Series Data vs Order Test Workflow"
            concurrency:
              maxParallelSteps: 2
              schedulingPolicy: "critical-path"
            nodes:
              - id: "pre-gate"
                system: "system1"
                engine: "deterministic"
                type: "subagent-gating"
                rules:
                  forceActivate:
                    security: "true"
                    specDrift: "true"
                  skipIf:
                    refactoring: "true"

              - id: "security"
                agent: "security"
                system: "system2"
                dependsOn: ["pre-gate"]
                passResults: true

              - id: "specDrift"
                agent: "specDrift"
                system: "system2"
                dependsOn: ["pre-gate"]
                passResults: false

              - id: "refactoring"
                agent: "refactoring"
                system: "system2"
                dependsOn: ["pre-gate"]
                passResults: true

              - id: "high-sev-gate"
                system: "system1"
                engine: "deterministic"
                inputsFrom: ["security"]
                condition: "inputs.findingsCount(severity >= 'High') > 0"
                passResults: true

              - id: "remediationVerifier"
                agent: "remediationVerifier"
                system: "system2"
                inputsFrom: ["high-sev-gate"]
                passResults: true

              - id: "independentFollowUp"
                agent: "independentFollowUp"
                system: "system2"
                dependsOn: ["security", "specDrift"]
                inputsFrom: []
                passResults: true
            """;

        var workflow = WorkflowLoader.LoadFromString(workflowYaml);
        var config = new GitGavelConfig();
        var definitions = AgentDefinitionRegistry.LoadAllDefinitions(config);

        definitions["remediationVerifier"] = new AgentDefinition
        {
            Id = "remediationVerifier",
            Name = "RemediationVerifierAgent",
            Category = "Security",
            Provider = "gemini",
            Model = "gemini-2.5-pro",
            UserPromptTemplate = "UPSTREAM:{{UpstreamStageResults}}"
        };

        definitions["independentFollowUp"] = new AgentDefinition
        {
            Id = "independentFollowUp",
            Name = "IndependentFollowUpAgent",
            Category = "Architecture",
            Provider = "gemini",
            Model = "gemini-2.5-flash",
            UserPromptTemplate = "INDEPENDENT_UPSTREAM:{{UpstreamStageResults}}"
        };

        var capturedPrompts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mockClient = new Mock<ILlmClient>();
        mockClient
            .Setup(c => c.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LlmRequest req, CancellationToken _) =>
            {
                lock (capturedPrompts)
                {
                    capturedPrompts[req.AgentName ?? "unknown"] = req.Prompt;
                }

                if (req.AgentName == "SecurityAuditor")
                {
                    return new LlmResponse
                    {
                        Content = """
                        [
                          {
                            "title": "SQL Injection in UserRepo",
                            "summary": "Unsanitized query",
                            "category": "Security",
                            "severity": "High",
                            "filePath": "src/UserRepo.cs",
                            "startLine": 12,
                            "endLine": 18,
                            "suggestedRemediation": "Use parameterized query"
                          }
                        ]
                        """,
                        ModelUsed = req.Model,
                        PromptTokens = 100,
                        CompletionTokens = 50,
                        DurationMs = 10
                    };
                }

                return new LlmResponse
                {
                    Content = "[]",
                    ModelUsed = req.Model,
                    PromptTokens = 50,
                    CompletionTokens = 10,
                    DurationMs = 5
                };
            });

        var clientFactory = new LlmClientFactory(fallbackClient: mockClient.Object);
        var stateMachine = new WorkflowStateMachine(workflow, definitions, clientFactory);

        var repoContext = new RepoContext
        {
            RootDirectory = Directory.GetCurrentDirectory(),
            SpecFiles = [new ScannedFile("SPECIFICATION.md", "/repo/SPECIFICATION.md", 10, 100, "# Spec", true)],
            SourceFiles = [new ScannedFile("src/UserRepo.cs", "/repo/src/UserRepo.cs", 25, 300, "class UserRepo {}", false)],
            SarifFindings = []
        };

        var snapshots = new List<WorkflowSnapshot>();
        var (result, finalSnapshot) = await stateMachine.ExecuteAsync(
            repoContext,
            config,
            onSnapshotUpdated: snap => snapshots.Add(snap));

        snapshots.Should().NotBeEmpty();
        finalSnapshot.Nodes.First(n => n.Id == "refactoring").Status.Should().Be(WorkflowNodeStatus.Skipped);
        finalSnapshot.Nodes.First(n => n.Id == "security").Status.Should().Be(WorkflowNodeStatus.Completed);
        finalSnapshot.Nodes.First(n => n.Id == "high-sev-gate").Status.Should().Be(WorkflowNodeStatus.Completed);
        finalSnapshot.Nodes.First(n => n.Id == "remediationVerifier").Status.Should().Be(WorkflowNodeStatus.Completed);
        finalSnapshot.Nodes.First(n => n.Id == "independentFollowUp").Status.Should().Be(WorkflowNodeStatus.Completed);

        // Verify remediationVerifier (inputsFrom: ["high-sev-gate"] -> ["security"]) received security's finding!
        capturedPrompts["RemediationVerifierAgent"].Should().Contain("SQL Injection in UserRepo");

        // Verify independentFollowUp (dependsOn: ["security", "specDrift"], inputsFrom: []) received NO findings!
        capturedPrompts["IndependentFollowUpAgent"].Should().Be("INDEPENDENT_UPSTREAM:");

        // Verify console diagram renderer produces valid Spectre markup for the snapshot
        var diagramMarkup = ConsoleWorkflowDiagramRenderer.FormatDiagramMarkup(finalSnapshot);
        diagramMarkup.Should().Contain("Level 0 (Root)");
        diagramMarkup.Should().Contain("Completed");
        diagramMarkup.Should().Contain("Skipped");
        diagramMarkup.Should().Contain("inputsFrom: [[high-sev-gate]]");
        diagramMarkup.Should().Contain("dependsOn: [[security, specDrift]]");
    }

    [Fact]
    public async Task NonJevGateEvaluator_EvaluatesDeterministicArtifactAndGlobRules_AndFastLlmGate()
    {
        var context = new RepoContext
        {
            RootDirectory = "/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/Auth/TokenService.cs", "/repo/src/Auth/TokenService.cs", 40, 800, "class TokenService {}", false)],
            SarifFindings = [new SarifFinding("SEC01", "Hardcoded secret", "error", "src/Auth/TokenService.cs", 10, 12)],
            PipelineArtifacts = new PipelineArtifactsContext
            {
                Artifacts =
                [
                    new IngestedPipelineArtifact(
                        "tests",
                        "trx",
                        ["results.trx"],
                        "1 failed",
                        TestSummary: new TestRunArtifactSummary(10, 9, 1, 0, [])),
                    new IngestedPipelineArtifact(
                        "coverage",
                        "cobertura",
                        ["cov.xml"],
                        "74%",
                        CoverageSummary: new CoverageArtifactSummary(74.0, 68.0, []))
                ]
            }
        };

        NonJevGateEvaluator.EvaluateExpression("artifacts.sarif.alertCount > 0 && files.changedMatches('src/Auth/**')", context).Should().BeTrue();
        NonJevGateEvaluator.EvaluateExpression("artifacts.testResults.failedCount > 0 || artifacts.coverage.lineCoveragePercent < 80", context).Should().BeTrue();
        NonJevGateEvaluator.EvaluateExpression("!git.isPullRequest && files.specFilesChanged == 0", context).Should().BeTrue();

        var mockLlm = new Mock<ILlmClient>();
        mockLlm
            .Setup(c => c.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = """{"shouldRun": false, "confidence": 0.22, "reason": "No database migrations modified."}""",
                ModelUsed = "gemini-2.5-flash"
            });

        var llmDecision = await NonJevGateEvaluator.EvaluateFastLlmGateAsync(
            "MigrationAuditor",
            "Were EF Core database migrations modified?",
            context,
            mockLlm.Object,
            "gemini-2.5-flash",
            minConfidence: 0.50);

        llmDecision.ShouldRun.Should().BeFalse();
        llmDecision.Confidence.Should().BeApproximately(0.22, 0.01);
        llmDecision.Reason.Should().Contain("No database migrations");
    }

    [Fact]
    public async Task WorkflowStateMachine_EnforcesMaxParallelSteps_AndDispatchesCriticalPathFirst()
    {
        var workflowYaml = """
            version: 1
            name: "Bounded Concurrency & Critical Path Test"
            concurrency:
              maxParallelSteps: 1
              schedulingPolicy: "critical-path"
            nodes:
              - id: "leafNode"
                agent: "refactoring"
                system: "system2"
              - id: "criticalRoot"
                agent: "security"
                system: "system2"
              - id: "downstreamSeries1"
                agent: "reliability"
                system: "system2"
                dependsOn: ["criticalRoot"]
              - id: "downstreamSeries2"
                agent: "tests"
                system: "system2"
                dependsOn: ["downstreamSeries1"]
            """;

        var workflow = WorkflowLoader.LoadFromString(workflowYaml);
        var config = new GitGavelConfig();
        var definitions = AgentDefinitionRegistry.LoadAllDefinitions(config);

        var executionOrder = new List<string>();
        var currentActive = 0;
        var maxObservedActive = 0;
        var gateLock = new object();

        var mockClient = new Mock<ILlmClient>();
        mockClient
            .Setup(c => c.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (LlmRequest req, CancellationToken ct) =>
            {
                lock (gateLock)
                {
                    executionOrder.Add(req.AgentName ?? "");
                    currentActive++;
                    if (currentActive > maxObservedActive) maxObservedActive = currentActive;
                }

                await Task.Delay(25, ct);

                lock (gateLock)
                {
                    currentActive--;
                }

                return new LlmResponse { Content = "[]", ModelUsed = req.Model };
            });

        var stateMachine = new WorkflowStateMachine(workflow, definitions, new LlmClientFactory(fallbackClient: mockClient.Object));
        var repoContext = new RepoContext
        {
            RootDirectory = "/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/Program.cs", "/repo/src/Program.cs", 10, 100, "class Program {}", false)],
            SarifFindings = []
        };

        await stateMachine.ExecuteAsync(repoContext, config);

        // Concurrency cap of 1 must never be exceeded
        maxObservedActive.Should().Be(1);

        // criticalRoot has 2 transitive dependents (downstreamSeries1 -> downstreamSeries2), whereas leafNode has 0,
        // so criticalRoot (SecurityAuditor) must be dispatched before leafNode (RefactoringAgent)!
        executionOrder.First().Should().Be("SecurityAuditor");
    }

    [Fact]
    public void SarifParser_ParsesSarif10StringMessageAndResultFile_AndSarif21ObjectMessage()
    {
        var sarif10 = """
            {
              "version": "1.0.0",
              "runs": [
                {
                  "results": [
                    {
                      "ruleId": "CS8600",
                      "level": "warning",
                      "message": "Converting null literal or possible null value to non-nullable type.",
                      "locations": [
                        {
                          "resultFile": {
                            "uri": "src/Program.cs",
                            "region": {
                              "startLine": 14,
                              "endLine": 16
                            }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var findings = SarifParser.Parse(sarif10);
        findings.Should().ContainSingle();
        findings[0].RuleId.Should().Be("CS8600");
        findings[0].Message.Should().Be("Converting null literal or possible null value to non-nullable type.");
        findings[0].FilePath.Should().Be("src/Program.cs");
        findings[0].StartLine.Should().Be(14);
        findings[0].EndLine.Should().Be(16);
    }

    [Fact]
    public void NonJevGateEvaluator_SplitOutsideQuotes_PreservesOperatorsInsideSingleAndDoubleQuotes()
    {
        var context = new RepoContext
        {
            RootDirectory = "/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/a||b/Service.cs", "/repo/src/a||b/Service.cs", 10, 100, "class S {}", false)],
            SarifFindings = []
        };

        // Ensure '||' and '&&' inside quoted globs are not split as boolean operators
        var parts = NonJevGateEvaluator.SplitOutsideQuotes("files.changedMatches('src/a||b/**') && !git.isPullRequest", "||");
        parts.Should().ContainSingle();

        NonJevGateEvaluator.EvaluateExpression("files.changedMatches('src/a||b/**') && !git.isPullRequest", context)
            .Should().BeTrue();
    }

    [Fact]
    public void AgentDefinitionRegistry_ThrowsWhenExplicitAgentsPathIsInvalidOrMissing()
    {
        var config = new GitGavelConfig();
        var act = () => AgentDefinitionRegistry.LoadAllDefinitions(
            config,
            repoRootDir: Directory.GetCurrentDirectory(),
            explicitAgentsPath: "nonexistent-agents-folder");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed to load explicit agent definitions*");
    }

    [Fact]
    public async Task WorkflowLoader_ValidatesAllRules_AndThrowsOnMissingExplicitWorkflowFile()
    {
        var config = new GitGavelConfig();
        var defs = AgentDefinitionRegistry.LoadAllDefinitions(config);
        var rootDir = Directory.GetCurrentDirectory();

        // Missing explicit workflow file must throw FileNotFoundException
        var missingExplicitAct = () => WorkflowLoader.ResolveOrBuildDefault(
            config,
            defs,
            repoRootDir: rootDir,
            explicitWorkflowPath: "missing.workflow.yml");
        missingExplicitAct.Should().Throw<FileNotFoundException>();

        // Missing LoadFromFileAsync must throw FileNotFoundException
        var loadFileAct = async () => await WorkflowLoader.LoadFromFileAsync("missing.workflow.yml", rootDir);
        await loadFileAct.Should().ThrowAsync<FileNotFoundException>();

        // Empty YAML content must throw InvalidOperationException
        var emptyAct = () => WorkflowLoader.LoadFromString("   ");
        emptyAct.Should().Throw<InvalidOperationException>();

        // Duplicate node IDs
        var dupIdAct = () => WorkflowLoader.LoadFromString("""
            version: 1
            nodes:
              - id: "dup"
                agent: "security"
              - id: "dup"
                agent: "tests"
            """);
        dupIdAct.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*");

        // Self-dependency
        var selfDepAct = () => WorkflowLoader.LoadFromString("""
            version: 1
            nodes:
              - id: "selfNode"
                agent: "security"
                dependsOn: ["selfNode"]
            """);
        selfDepAct.Should().Throw<InvalidOperationException>().WithMessage("*cannot depend on itself*");

        // Unknown dependency
        var unknownDepAct = () => WorkflowLoader.LoadFromString("""
            version: 1
            nodes:
              - id: "nodeA"
                agent: "security"
                inputsFrom: ["ghostNode"]
            """);
        unknownDepAct.Should().Throw<InvalidOperationException>().WithMessage("*unknown*");

        // Invalid MaxParallelSteps
        var badConcurrencyAct = () => WorkflowLoader.LoadFromString("""
            version: 1
            concurrency:
              maxParallelSteps: 0
            nodes:
              - id: "nodeA"
                agent: "security"
            """);
        badConcurrencyAct.Should().Throw<InvalidOperationException>().WithMessage("*maxParallelSteps*");
    }

    [Fact]
    public async Task DeclarativeYamlAgent_ShouldSkipAndBuildPrompt_CoversAllScopeAndPromptOptions()
    {
        var mockClient = new Mock<ILlmClient>();
        string? capturedPrompt = null;
        mockClient
            .Setup(c => c.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new LlmResponse { Content = "[]", ModelUsed = "gemini-2.5-pro" });

        // 1. RequireSpecs skip when no specs exist
        var specReqDef = new AgentDefinition
        {
            Id = "customSpec",
            Name = "CustomSpecAgent",
            Scope = new AgentScopeConfig { RequireSpecs = true }
        };
        var specAgent = new DeclarativeYamlAgent(mockClient.Object, specReqDef);
        var emptyRepoContext = new RepoContext
        {
            RootDirectory = Directory.GetCurrentDirectory(),
            SpecFiles = [],
            SourceFiles = [],
            TestFiles = [],
            SarifFindings = []
        };

        var skipSpecResult = await specAgent.ExecuteAsync(emptyRepoContext);
        skipSpecResult.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().BeNull();

        // 2. RequireSourceOrTests skip when neither source nor tests exist
        var testReqDef = new AgentDefinition
        {
            Id = "customTest",
            Name = "CustomTestAgent",
            Scope = new AgentScopeConfig { RequireSourceOrTests = true }
        };
        var testAgent = new DeclarativeYamlAgent(mockClient.Object, testReqDef);
        var skipTestResult = await testAgent.ExecuteAsync(emptyRepoContext);
        skipTestResult.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().BeNull();

        // 3. Full prompt build with all Scope options enabled
        var richDef = new AgentDefinition
        {
            Id = "richAgent",
            Name = "RichDeclarativeAgent",
            Category = "Reliability",
            Tiers = ["unit", "integration"],
            UserPromptIntro = "INTRO_HEADER",
            AdditionalPromptBody = "ADDITIONAL_BODY_SECTION",
            AdditionalInstructions = "PROJECT_SPECIFIC_RULE",
            AntiScopeCreepRule = "CUSTOM_PR_RULE",
            JsonSchemaInstructions = "CUSTOM_JSON_SCHEMA",
            Scope = new AgentScopeConfig
            {
                IncludeSpecs = true,
                IncludeSarif = true,
                IncludeAst = true,
                IncludeAstComplexity = true,
                AstHeading = "### Custom AST Heading:",
                IncludeComplexityHotspots = true,
                HotspotsHeading = "### Custom Hotspots Heading:",
                HotspotItemPrefix = "- Spot:",
                IncludeTests = true,
                ExcludeTestSourceFiles = true,
                PrioritizeHotspots = true,
                MaxSourceFiles = 1,
                OmitDiffContentInPrMode = true,
                SourceHeading = "### Custom Source Heading:",
                SourceLabelPrefix = "ProdFile"
            }
        };

        var complexCode = string.Join("\n", Enumerable.Range(1, 25).Select(i => $"if (x == {i} && y == {i}) {{ Console.WriteLine({i}); }}"));
        var hotspotFile = new ScannedFile("src/Hotspot.cs", "/repo/src/Hotspot.cs", 50, 800, $"public class Hotspot {{ public void Run(int x, int y) {{ {complexCode} }} }}", false, IsTest: false, DiffPatch: "@@ -1 +1 @@\n+HotspotDiff");
        var smallFile = new ScannedFile("src/Small.cs", "/repo/src/Small.cs", 5, 50, "public class Small {}", false, IsTest: false, DiffPatch: "@@ -1 +1 @@\n+SmallDiff");
        var testInSource = new ScannedFile("src/FakeTests.cs", "/repo/src/FakeTests.cs", 10, 100, "public class FakeTests {}", false, IsTest: true, DiffPatch: "@@ -1 +1 @@");

        var richContext = new RepoContext
        {
            RootDirectory = Directory.GetCurrentDirectory(),
            SpecFiles = [new ScannedFile("SPEC.md", "/repo/SPEC.md", 5, 50, "# Spec Content", true)],
            SourceFiles = [smallFile, hotspotFile, testInSource],
            TestFiles = [new ScannedFile("tests/UnitTests.cs", "/repo/tests/UnitTests.cs", 15, 200, "public class UnitTests { public void Test1() {} }", false, IsTest: true)],
            SarifFindings = [new SarifFinding("SEC100", "SQLi risk", "error", "src/Hotspot.cs", 5, 6)],
            ActivePullRequest = new PullRequestContext { PrNumber = 42, Title = "PR Title" },
            UpstreamFindings =
            [
                new FindingCandidate
                {
                    Title = "Upstream Issue",
                    Summary = "Passed from upstream",
                    Category = "Security",
                    Severity = "High",
                    AgentName = "SecurityAuditor",
                    FilePath = "src/Hotspot.cs",
                    StartLine = 5,
                    EndLine = 6,
                    SuggestedRemediation = "Apply fix"
                }
            ]
        };

        var richAgent = new DeclarativeYamlAgent(mockClient.Object, richDef);
        await richAgent.ExecuteAsync(richContext);

        capturedPrompt.Should().NotBeNull();
        capturedPrompt.Should().Contain("INTRO_HEADER");
        capturedPrompt.Should().Contain("Target Test Tiers: unit, integration");
        capturedPrompt.Should().Contain("CUSTOM_PR_RULE");
        capturedPrompt.Should().Contain("ADDITIONAL_BODY_SECTION");
        capturedPrompt.Should().Contain("PROJECT_SPECIFIC_RULE");
        capturedPrompt.Should().Contain("Upstream Issue");
        capturedPrompt.Should().Contain("### Specification Documents:");
        capturedPrompt.Should().Contain("SEC100");
        capturedPrompt.Should().Contain("### Custom AST Heading:");
        capturedPrompt.Should().Contain("### Custom Hotspots Heading:");
        capturedPrompt.Should().Contain("- Spot: `src/Hotspot.cs`");
        capturedPrompt.Should().Contain("### Existing Automated Test Suite Symbol Map");
        capturedPrompt.Should().Contain("### Custom Source Heading:");
        capturedPrompt.Should().Contain("--- ProdFile: src/Hotspot.cs ---");
        // MaxSourceFiles = 1 with PrioritizeHotspots = true must pick Hotspot.cs and omit Small.cs & FakeTests.cs
        capturedPrompt.Should().NotContain("--- ProdFile: src/Small.cs ---");
        capturedPrompt.Should().NotContain("--- ProdFile: src/FakeTests.cs ---");
        // OmitDiffContentInPrMode = true must include DiffPatch (+HotspotDiff) but omit full class body
        capturedPrompt.Should().Contain("+HotspotDiff");
        capturedPrompt.Should().NotContain("public class Hotspot");
        capturedPrompt.Should().Contain("CUSTOM_JSON_SCHEMA");
    }

    [Fact]
    public async Task WorkflowStateMachine_HandlesNotUsedFailedAndSkippedTransitions_WithStatusReasons()
    {
        var workflowYaml = """
            version: 1
            nodes:
              - id: "disabledNode"
                agent: "specDrift"
                system: "system2"
                enabled: false
              - id: "failingNode"
                agent: "security"
                system: "system2"
              - id: "downstreamOfFailed"
                agent: "refactoring"
                system: "system2"
                inputsFrom: ["failingNode"]
            """;

        var workflow = WorkflowLoader.LoadFromString(workflowYaml);
        var config = new GitGavelConfig();
        var definitions = AgentDefinitionRegistry.LoadAllDefinitions(config);

        var mockSubagent = new Mock<ISubagent>();
        mockSubagent.SetupGet(s => s.Name).Returns("ThrowingSecurityAgent");
        mockSubagent
            .Setup(s => s.ExecuteAsync(It.IsAny<RepoContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated LLM network failure"));

        var stateMachine = new WorkflowStateMachine(
            workflow,
            definitions,
            new LlmClientFactory(forceMock: true),
            customAgentFactory: (key, _) => key == "security"
                ? mockSubagent.Object
                : new DeclarativeYamlAgent(new MockLlmClient(), definitions[key]));

        var repoContext = new RepoContext
        {
            RootDirectory = Directory.GetCurrentDirectory(),
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/App.cs", "/repo/src/App.cs", 10, 100, "class App {}", false)],
            SarifFindings = []
        };

        var (result, snapshot) = await stateMachine.ExecuteAsync(repoContext, config);

        var disabledState = snapshot.Nodes.First(n => n.Id == "disabledNode");
        disabledState.Status.Should().Be(WorkflowNodeStatus.NotUsed);
        disabledState.StatusReason.Should().Contain("Disabled");

        var failedState = snapshot.Nodes.First(n => n.Id == "failingNode");
        failedState.Status.Should().Be(WorkflowNodeStatus.Failed);
        failedState.StatusReason.Should().Contain("Simulated LLM network failure");

        var skippedDownstream = snapshot.Nodes.First(n => n.Id == "downstreamOfFailed");
        skippedDownstream.Status.Should().Be(WorkflowNodeStatus.Skipped);
        skippedDownstream.StatusReason.Should().Contain("Skipped because upstream dependency 'failingNode' was failed");
    }
}
