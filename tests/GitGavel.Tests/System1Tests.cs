using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System1.Engines;
using GitGavel.System1.Models;
using GitGavel.System2.Models;
using Moq;
using TypeSafe.Jev;

namespace GitGavel.Tests;

public class System1Tests
{
    private readonly GitGavelConfig _config = new();

    private static FindingCandidate CreateFinding(string title, string category, string severity, string filePath) =>
        new()
        {
            Title = title,
            Summary = "Summary for " + title,
            Category = category,
            Severity = severity,
            FilePath = filePath,
            StartLine = 10,
            EndLine = 20,
            AgentName = "TestAgent",
            SuggestedRemediation = "Fix it"
        };

    [Fact]
    public async Task JevEngine_WhenNoulDetectsDuplicate_SuppressesIssue()
    {
        var mockJev = new Mock<IJevClient>();

        var dedupeResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["is_duplicate"] = new NoulAnswer { Noul = 0.95 }
            }
        };

        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.Is<IReadOnlyDictionary<string, Question>>(q => q.ContainsKey("is_duplicate")),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(dedupeResp);

        var engine = new TypeSafeJevEngine(mockJev.Object);

        var finding = CreateFinding("SQL Injection Vulnerability", "Security", "Critical", "Data/Repo.cs");
        var existingIssues = new List<ExistingIssue>
        {
            new(101, "SQL Injection Vulnerability", "Existing issue in Data/Repo.cs")
        };

        var decision = await engine.EvaluateFindingAsync(finding, existingIssues, _config);

        decision.Should().NotBeNull();
        decision.IsDuplicate.Should().BeTrue();
        decision.DuplicateOfIssueNumber.Should().Be(101);
        decision.Status.Should().Be(TriageStatus.SuppressedDuplicate);
        decision.TargetColumn.Should().Be("Discard");
        decision.DuplicateProbability.Should().Be(0.95);
    }

    [Fact]
    public async Task JevEngine_WhenNoulIsUncertain_RoutesToHumanReview()
    {
        var mockJev = new Mock<IJevClient>();

        // 1. Dedupe call returns uncertain probability 0.60
        var dedupeResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["is_duplicate"] = new NoulAnswer { Noul = 0.60 }
            }
        };

        // 2. Eval call returns Backlog and score 4.0
        var evalResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["priority_score"] = new ScoreAnswer { Score = 4.0, Confidence = 0.95, Legend = new Dictionary<int, System.Text.Json.Nodes.JsonNode?> { [1] = null }, Probabilities = new Dictionary<int, double> { [1] = 1.0 } },
                ["route_choice"] = new ChoiceAnswer { Choice = "Backlog", Confidence = 0.92, Probabilities = new Dictionary<string, double> { ["Backlog"] = 0.92 } }
            }
        };

        mockJev.SetupSequence(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.IsAny<IReadOnlyDictionary<string, Question>>(),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(dedupeResp)
            .ReturnsAsync(evalResp);

        var engine = new TypeSafeJevEngine(mockJev.Object);

        var finding = CreateFinding("Refactor Data Access", "Refactoring", "High", "Data/Repo.cs");
        var existingIssues = new List<ExistingIssue>
        {
            new(42, "Refactor Repository queries", "Query cleanup needed")
        };

        var decision = await engine.EvaluateFindingAsync(finding, existingIssues, _config);

        decision.Should().NotBeNull();
        decision.IsDuplicate.Should().BeFalse();
        decision.DuplicateOfIssueNumber.Should().Be(42);
        decision.Status.Should().Be(TriageStatus.RoutedToHumanReview);
        decision.TargetColumn.Should().Be("NeedsHumanReview");
    }

    [Fact]
    public async Task JevEngine_WhenNovelAndCritical_RoutesToBacklogAsP0()
    {
        var mockJev = new Mock<IJevClient>();

        var evalResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["priority_score"] = new ScoreAnswer { Score = 4.9, Confidence = 0.98, Legend = new Dictionary<int, System.Text.Json.Nodes.JsonNode?> { [1] = null }, Probabilities = new Dictionary<int, double> { [1] = 1.0 } },
                ["route_choice"] = new ChoiceAnswer { Choice = "Backlog", Confidence = 0.96, Probabilities = new Dictionary<string, double> { ["Backlog"] = 0.96 } }
            }
        };

        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.Is<IReadOnlyDictionary<string, Question>>(q => q.ContainsKey("priority_score")),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(evalResp);

        var engine = new TypeSafeJevEngine(mockJev.Object);

        var finding = CreateFinding("Remote Code Execution", "Security", "Critical", "Api/Exec.cs");
        var decision = await engine.EvaluateFindingAsync(finding, [], _config);

        decision.Should().NotBeNull();
        decision.PriorityLabel.Should().Be("P0");
        decision.TargetColumn.Should().Be("Backlog");
        decision.Status.Should().Be(TriageStatus.AcceptedToBacklog);
    }

    [Fact]
    public async Task TriageAll_EnforcesMaxIssuesPerRunCap_ExemptingP0ThroughP2()
    {
        var engine = new MockTriageEngine();
        var config = new GitGavelConfig
        {
            Triage = new TriageConfig { MaxIssuesPerRun = 2 }
        };

        var findings = new List<FindingCandidate>
        {
            CreateFinding("Finding P0", "Security", "Critical", "File1.cs"),
            CreateFinding("Finding P1", "Security", "High", "File2.cs"),
            CreateFinding("Finding P2", "Security", "Medium", "File3.cs"),
            CreateFinding("Finding P3-1", "Refactoring", "Low", "File4.cs"),
            CreateFinding("Finding P3-2", "Refactoring", "Low", "File5.cs"),
            CreateFinding("Finding P3-3", "Refactoring", "Low", "File6.cs")
        };

        var decisions = await engine.TriageAllAsync(findings, [], config);

        decisions.Should().HaveCount(6);
        // P0, P1, P2 are exempt (3) + top 2 P3 findings allowed (2) = 5 AcceptedToBacklog
        decisions.Count(d => d.Status == TriageStatus.AcceptedToBacklog).Should().Be(5);
        // 3rd P3 finding exceeds MaxIssuesPerRun (2) and is discarded
        decisions.Count(d => d.Status == TriageStatus.DiscardedNoise).Should().Be(1);
    }

    [Fact]
    public async Task JevEngine_WhenNoulIsBelowHumanReviewThreshold_DoesNotRouteAsUncertainDuplicate()
    {
        var mockJev = new Mock<IJevClient>();

        // 1. Dedupe call returns low similarity 0.25 (< HumanReviewConfidence 0.50)
        var dedupeResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["is_duplicate"] = new NoulAnswer { Noul = 0.25 }
            }
        };

        // 2. Eval call returns Backlog and score 3.2 (P2)
        var evalResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["priority_score"] = new ScoreAnswer { Score = 3.2, Confidence = 0.90, Legend = new Dictionary<int, System.Text.Json.Nodes.JsonNode?> { [1] = null }, Probabilities = new Dictionary<int, double> { [1] = 1.0 } },
                ["route_choice"] = new ChoiceAnswer { Choice = "Backlog", Confidence = 0.90, Probabilities = new Dictionary<string, double> { ["Backlog"] = 0.90 } }
            }
        };

        mockJev.SetupSequence(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.IsAny<IReadOnlyDictionary<string, Question>>(),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(dedupeResp)
            .ReturnsAsync(evalResp);

        var engine = new TypeSafeJevEngine(mockJev.Object);

        var finding = CreateFinding("Add retry policy", "Refactoring", "Medium", "Net/Http.cs");
        var existingIssues = new List<ExistingIssue>
        {
            new(50, "Unrelated issue", "Some other description")
        };

        var decision = await engine.EvaluateFindingAsync(finding, existingIssues, _config);

        decision.IsDuplicate.Should().BeFalse();
        decision.DuplicateOfIssueNumber.Should().BeNull();
        decision.Status.Should().Be(TriageStatus.AcceptedToBacklog);
    }

    [Fact]
    public async Task JevEngine_WhenExactFingerprintMatchesExistingIssue_SuppressesWithoutCallingJev()
    {
        var mockJev = new Mock<IJevClient>(MockBehavior.Strict);
        var engine = new TypeSafeJevEngine(mockJev.Object);

        var finding = CreateFinding("Extract interface", "Refactoring", "Medium", "Services/OrderService.cs");
        var existingBody = $"""
            ## ⚠️ Summary
            Summary for Extract interface

            ---
            <!-- gitgavel-metadata
            fingerprint: {finding.Fingerprint}
            category: Refactoring
            jev_score: 3.0
            jev_confidence: 0.90
            agent: RefactoringAgent
            created_at: 2026-10-21T12:00:00Z
            -->
            """;

        var existingIssues = new List<ExistingIssue>
        {
            new(77, "Extract interface", existingBody)
        };

        var decision = await engine.EvaluateFindingAsync(finding, existingIssues, _config);

        decision.IsDuplicate.Should().BeTrue();
        decision.DuplicateOfIssueNumber.Should().Be(77);
        decision.DuplicateProbability.Should().Be(1.0);
        decision.Status.Should().Be(TriageStatus.SuppressedDuplicate);
        decision.TargetColumn.Should().Be("Discard");
        mockJev.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JevEngine_WhenP3FindingHasUncertainDuplicateMatch_SuppressesAsDuplicate()
    {
        var mockJev = new Mock<IJevClient>();

        // 1. Dedupe call returns uncertain probability 0.65 (between 0.50 and 0.85)
        var dedupeResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["is_duplicate"] = new NoulAnswer { Noul = 0.65 }
            }
        };

        // 2. Eval call returns P3 score (1.8) and Backlog choice
        var evalResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["priority_score"] = new ScoreAnswer { Score = 1.8, Confidence = 0.90, Legend = new Dictionary<int, System.Text.Json.Nodes.JsonNode?> { [1] = null }, Probabilities = new Dictionary<int, double> { [1] = 1.0 } },
                ["route_choice"] = new ChoiceAnswer { Choice = "Backlog", Confidence = 0.90, Probabilities = new Dictionary<string, double> { ["Backlog"] = 0.90 } }
            }
        };

        mockJev.SetupSequence(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.IsAny<IReadOnlyDictionary<string, Question>>(),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(dedupeResp)
            .ReturnsAsync(evalResp);

        var engine = new TypeSafeJevEngine(mockJev.Object);

        var finding = CreateFinding("Minor naming nit", "Refactoring", "Low", "Utils/Helper.cs");
        var existingIssues = new List<ExistingIssue>
        {
            new(28, "Helper cleanup", "Minor cleanup in Helper.cs")
        };

        var decision = await engine.EvaluateFindingAsync(finding, existingIssues, _config);

        decision.IsDuplicate.Should().BeTrue();
        decision.DuplicateOfIssueNumber.Should().Be(28);
        decision.DuplicateProbability.Should().Be(0.65);
        decision.PriorityLabel.Should().Be("P3");
        decision.Status.Should().Be(TriageStatus.SuppressedDuplicate);
        decision.TargetColumn.Should().Be("Discard");
    }

    [Fact]
    public async Task JevEngine_TriageAll_EnforcesMaxIssuesPerRunCapOnP3_AndExemptsP0ThroughP2()
    {
        var mockJev = new Mock<IJevClient>();

        SystemOneResponse CreateEvalResp(double score) => new()
        {
            Answers = new Dictionary<string, Answer>
            {
                ["priority_score"] = new ScoreAnswer { Score = score, Confidence = 0.90, Legend = new Dictionary<int, System.Text.Json.Nodes.JsonNode?> { [1] = null }, Probabilities = new Dictionary<int, double> { [1] = 1.0 } },
                ["route_choice"] = new ChoiceAnswer { Choice = "Backlog", Confidence = 0.90, Probabilities = new Dictionary<string, double> { ["Backlog"] = 0.90 } }
            }
        };

        mockJev.SetupSequence(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.IsAny<IReadOnlyDictionary<string, Question>>(),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEvalResp(4.8)) // P0
            .ReturnsAsync(CreateEvalResp(3.8)) // P1
            .ReturnsAsync(CreateEvalResp(2.8)) // P2
            .ReturnsAsync(CreateEvalResp(2.0)) // P3 #1
            .ReturnsAsync(CreateEvalResp(1.5)); // P3 #2 (exceeds cap of 1)

        var engine = new TypeSafeJevEngine(mockJev.Object);
        var config = new GitGavelConfig
        {
            Triage = new TriageConfig { MaxIssuesPerRun = 1 }
        };

        var findings = new List<FindingCandidate>
        {
            CreateFinding("P0 Finding", "Security", "Critical", "A.cs"),
            CreateFinding("P1 Finding", "Security", "High", "B.cs"),
            CreateFinding("P2 Finding", "Refactoring", "Medium", "C.cs"),
            CreateFinding("P3 Finding 1", "Refactoring", "Low", "D.cs"),
            CreateFinding("P3 Finding 2", "Refactoring", "Low", "E.cs")
        };

        var decisions = await engine.TriageAllAsync(findings, [], config);

        decisions.Should().HaveCount(5);
        decisions.Count(d => d.Status == TriageStatus.AcceptedToBacklog).Should().Be(4);
        decisions.Single(d => d.Finding.Title == "P3 Finding 2").Status.Should().Be(TriageStatus.DiscardedNoise);
        decisions.Single(d => d.Finding.Title == "P3 Finding 2").TargetColumn.Should().Be("Discard");
    }

    [Fact]
    public async Task JevEngine_VerifyIssueResolutions_WhenNoulHighConfidence_ClosesResolvedIssueAndSendsCalibratedPayload()
    {
        var mockJev = new Mock<IJevClient>();
        JsonContent? capturedState = null;
        IReadOnlyDictionary<string, Question>? capturedQuestions = null;

        var verifyResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["is_issue_resolved"] = new NoulAnswer { Noul = 0.96 }
            }
        };

        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.Is<IReadOnlyDictionary<string, Question>>(q => q.ContainsKey("is_issue_resolved")),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .Callback<JsonContent, IReadOnlyDictionary<string, Question>, JevRequestOptions?, CancellationToken>((s, q, _, _) =>
            {
                capturedState = s;
                capturedQuestions = q;
            })
            .ReturnsAsync(verifyResp);

        var engine = new TypeSafeJevEngine(mockJev.Object);

        var candidates = new List<IssueResolutionCandidate>
        {
            new()
            {
                IssueNumber = 101,
                IssueTitle = "Fix GitService fallback test",
                IsResolved = true,
                HowItWasFixed = "Isolated CI env vars in GitServiceTests",
                ResolvingCommitShas = ["cfad09afc6f436fb3af957ff2d94eba2d3e1ea36"],
                ResolvingCommitSummaries = ["`cfad09a` — *fix(tests): isolate CI environment variables in GitService fallback test*"]
            }
        };

        var openIssues = new List<ExistingIssue>
        {
            new(101, "Fix GitService fallback test", "Description", "open")
        };

        var decisions = await engine.VerifyIssueResolutionsAsync(candidates, openIssues, _config);

        decisions.Should().HaveCount(1);
        decisions[0].VerifiedResolved.Should().BeTrue();
        decisions[0].ResolutionProbability.Should().Be(0.96);
        decisions[0].Action.Should().Be(IssueResolutionAction.CloseResolved);
        decisions[0].TargetColumn.Should().Be("Done");

        capturedState.Should().NotBeNull();
        var stateJson = capturedState!.Value.ToString();
        stateJson.Should().Contain("\"issue\"");
        stateJson.Should().Contain("\"audit_resolution_verification\"");
        stateJson.Should().Contain("\"RESOLVED_AND_VERIFIED_IN_CODEBASE\"");
        stateJson.Should().Contain("IssueResolutionAuditor");
        stateJson.Should().Contain("Isolated CI env vars in GitServiceTests");

        capturedQuestions.Should().NotBeNull();
        var noulQuestion = capturedQuestions!["is_issue_resolved"].Should().BeOfType<NoulQuestion>().Subject;
        var questionJson = noulQuestion.Instructions.ToString() + " | " + noulQuestion.Criteria?.True.ToString() + " | " + noulQuestion.Criteria?.False.ToString();
        questionJson.Should().Contain("Does the audit resolution verification confirm that the issue has been fixed in the codebase by the cited commits and fix explanation?");
        questionJson.Should().Contain("The resolution verification provides a coherent fix explanation and commit attribution addressing the issue title.");
        questionJson.Should().Contain("The fix explanation is empty, nonsensical, or completely unrelated to the issue.");
    }

    [Theory]
    [InlineData(0.75, IssueResolutionAction.CommentProgress, "AI Triage")]
    [InlineData(0.40, IssueResolutionAction.LeaveOpen, "Backlog")]
    public async Task JevEngine_VerifyIssueResolutions_HandlesPartialAndLowConfidenceThresholds(
        double probability,
        IssueResolutionAction expectedAction,
        string expectedColumn)
    {
        var mockJev = new Mock<IJevClient>();
        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.Is<IReadOnlyDictionary<string, Question>>(q => q.ContainsKey("is_issue_resolved")),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SystemOneResponse
            {
                Answers = new Dictionary<string, Answer>
                {
                    ["is_issue_resolved"] = new NoulAnswer { Noul = probability }
                }
            });

        var engine = new TypeSafeJevEngine(mockJev.Object);
        var candidates = new List<IssueResolutionCandidate>
        {
            new()
            {
                IssueNumber = 10,
                IssueTitle = "Partial Issue",
                IsResolved = true,
                HowItWasFixed = "Partial work",
                ResolvingCommitShas = ["abc1234"]
            }
        };

        var UnresolvedCandidate = new IssueResolutionCandidate
        {
            IssueNumber = 11,
            IssueTitle = "Unresolved Issue",
            IsResolved = false,
            HowItWasFixed = "",
            ResolvingCommitShas = []
        };

        var decisions = await engine.VerifyIssueResolutionsAsync(
            [candidates[0], UnresolvedCandidate],
            [new ExistingIssue(10, "Partial Issue", "Body", "open")],
            _config);

        decisions.Should().HaveCount(2);
        decisions[0].VerifiedResolved.Should().BeFalse();
        decisions[0].Action.Should().Be(expectedAction);
        decisions[0].TargetColumn.Should().Be(expectedColumn);

        decisions[1].VerifiedResolved.Should().BeFalse();
        decisions[1].Action.Should().Be(IssueResolutionAction.LeaveOpen);
        decisions[1].ResolutionProbability.Should().Be(0.0);
    }

    [Fact]
    public async Task JevEngine_TriagePrReviewComments_SuppressesDuplicateAndScoresNovel()
    {
        var mockJev = new Mock<IJevClient>();

        var dedupeResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["is_duplicate_comment"] = new NoulAnswer { Noul = 0.92 }
            }
        };

        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.Is<IReadOnlyDictionary<string, Question>>(q => q.ContainsKey("is_duplicate_comment")),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(dedupeResp);

        var engine = new TypeSafeJevEngine(mockJev.Object);

        var findings = new List<FindingCandidate>
        {
            new()
            {
                FilePath = "src/App.cs",
                StartLine = 25,
                EndLine = 28,
                Category = "Security",
                Severity = "High",
                Title = "Missing null check",
                Summary = "Check for null input.",
                AgentName = "SecurityAuditor",
                SuggestedRemediation = "ArgumentNullException.ThrowIfNull(input);"
            }
        };

        var existingComments = new List<ExistingPrReviewComment>
        {
            new(555, "src/App.cs", 25, "Missing null check here", "reviewer", DateTime.UtcNow)
        };

        var decisions = await engine.TriagePrReviewCommentsAsync(findings, existingComments, _config);

        decisions.Should().HaveCount(1);
        decisions[0].IsDuplicate.Should().BeTrue();
        decisions[0].DuplicateOfCommentId.Should().Be(555);
        decisions[0].Action.Should().Be(PrCommentAction.Discard);
    }

    [Fact]
    public async Task JevEngine_TriagePrReviewComments_SuppressesLowSeverityAndEnforcesLifecycleInlineBudget()
    {
        var mockJev = new Mock<IJevClient>();

        var scoreHighResp = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["priority_score"] = new ScoreAnswer
                {
                    Score = 4.2,
                    Confidence = 0.95,
                    Legend = new Dictionary<int, System.Text.Json.Nodes.JsonNode?> { [1] = null },
                    Probabilities = new Dictionary<int, double> { [1] = 1.0 }
                },
                ["pr_route_choice"] = new ChoiceAnswer
                {
                    Choice = "PostInlineReview",
                    Confidence = 0.95,
                    Probabilities = new Dictionary<string, double> { ["PostInlineReview"] = 0.95 }
                }
            }
        };

        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.Is<IReadOnlyDictionary<string, Question>>(q => q.ContainsKey("pr_route_choice")),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(scoreHighResp);

        var engine = new TypeSafeJevEngine(mockJev.Object);

        var config = new GitGavelConfig
        {
            Github = new GitHubConfig
            {
                PullRequests = new PullRequestsConfig { MaxInlineCommentsPerPr = 2 }
            }
        };

        var findings = new List<FindingCandidate>
        {
            CreateFinding("Low severity style nitpick", "Refactoring", "Low", "src/App.cs"),
            CreateFinding("High severity bug 1", "Security", "High", "src/Auth.cs"),
            CreateFinding("High severity bug 2", "Security", "High", "src/Token.cs")
        };

        // 1 existing comment means remaining inline budget is 2 - 1 = 1
        var existingComments = new List<ExistingPrReviewComment>
        {
            new(100, "src/Other.cs", 12, "Prior review comment", "gitgavel", DateTime.UtcNow)
        };

        var decisions = await engine.TriagePrReviewCommentsAsync(findings, existingComments, config);

        decisions.Should().HaveCount(3);
        decisions.Single(d => d.Finding.Title == "Low severity style nitpick").Action.Should().Be(PrCommentAction.Discard);
        decisions.Count(d => d.Action == PrCommentAction.PostInlineReview).Should().Be(1);
        decisions.Count(d => d.Action == PrCommentAction.SummaryCommentOnly).Should().Be(1);
    }

    [Fact]
    public async Task MockTriageEngine_SubagentGating_WhenBypassConfigured_EnablesAllAgentsWithDeterministicOverride()
    {
        var engine = new MockTriageEngine();
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 50, 1000, "class App {}", false)],
            SarifFindings = []
        };

        var config = new GitGavelConfig
        {
            Triage = new TriageConfig
            {
                SubagentGating = new SubagentGatingConfig
                {
                    Enabled = true,
                    BypassInFullRepoMode = true
                }
            }
        };

        var candidateAgents = new[] { "SpecDriftAgent", "SecurityAuditor", "ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent" };
        var result = await engine.EvaluateSubagentGatingAsync(context, candidateAgents, config);

        result.Should().NotBeNull();
        result.Decisions.Should().HaveCount(5);
        result.ActiveAgentNames.Should().BeEquivalentTo(candidateAgents);
        result.SkippedAgentNames.Should().BeEmpty();
        result.Decisions.Should().OnlyContain(d => d.ShouldRun && d.IsDeterministicOverride);
        result.EstimatedTokensSaved.Should().Be(0);
    }

    [Fact]
    public async Task MockTriageEngine_SubagentGating_InFullRepoMode_WhenBypassIsFalse_SelectivelyGatesAgentsBasedOnRepoContext()
    {
        var engine = new MockTriageEngine();
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 50, 1000, "class App {}", false)],
            TestFiles = [new ScannedFile("tests/AppTests.cs", "/mock/tests/AppTests.cs", 20, 500, "class AppTests {}", false, true)],
            SarifFindings = [],
            AstSummaries = [new FileAstSummary("src/App.cs", [], 20, true)]
        };

        var config = new GitGavelConfig
        {
            Triage = new TriageConfig
            {
                SubagentGating = new SubagentGatingConfig
                {
                    Enabled = true,
                    BypassInFullRepoMode = false
                }
            }
        };

        var candidateAgents = new[] { "SpecDriftAgent", "SecurityAuditor", "ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent" };
        var result = await engine.EvaluateSubagentGatingAsync(context, candidateAgents, config);

        result.Should().NotBeNull();
        result.Decisions.Should().HaveCount(5);
        result.ActiveAgentNames.Should().BeEquivalentTo(["ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent"]);
        result.SkippedAgentNames.Should().BeEquivalentTo(["SpecDriftAgent", "SecurityAuditor"]);
        result.EstimatedTokensSaved.Should().Be(10000L);
    }

    [Fact]
    public async Task MockTriageEngine_SubagentGating_WhenMarkdownOnlyDiff_ActivatesSpecDriftAndSkipsCodeAgents()
    {
        var engine = new MockTriageEngine();
        var prContext = new PullRequestContext
        {
            PrNumber = 42,
            Title = "docs: update spec documentation",
            Body = "Update specs",
            BaseRef = "main",
            HeadRef = "docs/spec-update",
            HeadSha = "abc1234",
            ChangedFiles = [new PullRequestChangedFile("specs/api.md", "modified", 10, 2, "@@ -1,3 +1,5 @@")]
        };

        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [new ScannedFile("specs/api.md", "/mock/specs/api.md", 20, 200, "# API", true, DiffPatch: "@@ -1,3 +1,5 @@")],
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

        var candidateAgents = new[] { "SpecDriftAgent", "SecurityAuditor", "ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent" };
        var result = await engine.EvaluateSubagentGatingAsync(context, candidateAgents, config);

        result.Should().NotBeNull();
        result.ActiveAgentNames.Should().ContainSingle().Which.Should().Be("SpecDriftAgent");
        result.SkippedAgentNames.Should().BeEquivalentTo(["SecurityAuditor", "ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent"]);
        result.EstimatedTokensSaved.Should().Be(4 * 5000L);
    }

    [Fact]
    public async Task MockTriageEngine_SubagentGating_WhenSarifAlertsPresent_ForcesSecurityAuditor()
    {
        var engine = new MockTriageEngine();
        var prContext = new PullRequestContext { PrNumber = 42, Title = "fix: typo" };
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [],
            SarifFindings = [new SarifFinding("SEC001", "SQL injection", "High", "src/Db.cs", 10, 20)],
            ActivePullRequest = prContext
        };

        var config = new GitGavelConfig
        {
            Triage = new TriageConfig
            {
                SubagentGating = new SubagentGatingConfig
                {
                    Enabled = true,
                    AlwaysRunSecurityOnSarifAlerts = true
                }
            }
        };

        var result = await engine.EvaluateSubagentGatingAsync(context, ["SecurityAuditor", "SpecDriftAgent"], config);

        var secDecision = result.Decisions.Single(d => d.AgentName == "SecurityAuditor");
        secDecision.ShouldRun.Should().BeTrue();
        secDecision.IsDeterministicOverride.Should().BeTrue();
        secDecision.Confidence.Should().Be(1.0);
        secDecision.Reason.Should().Contain("SARIF static alerts loaded");
    }

    [Fact]
    public async Task MockTriageEngine_SubagentGating_WhenAstComplexityHigh_ForcesRefactoringAgent()
    {
        var engine = new MockTriageEngine();
        var prContext = new PullRequestContext { PrNumber = 42, Title = "perf: optimize loop" };
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/Heavy.cs", "/mock/src/Heavy.cs", 200, 3000, "class Heavy {}", false, DiffPatch: "@@ -1 +1 @@")],
            AstSummaries = [new FileAstSummary("src/Heavy.cs", [], 25, true)],
            SarifFindings = [],
            ActivePullRequest = prContext
        };

        var config = new GitGavelConfig
        {
            Triage = new TriageConfig
            {
                SubagentGating = new SubagentGatingConfig { Enabled = true }
            }
        };

        var result = await engine.EvaluateSubagentGatingAsync(context, ["RefactoringAgent"], config);

        var refactorDecision = result.Decisions.Single(d => d.AgentName == "RefactoringAgent");
        refactorDecision.ShouldRun.Should().BeTrue();
        refactorDecision.IsDeterministicOverride.Should().BeTrue();
        refactorDecision.Reason.Should().Contain("cyclomatic complexity");
    }

    [Fact]
    public async Task TypeSafeJevEngine_SubagentGating_WhenSystemOneNoulEvaluated_RespectsThresholdsAndCalculatesTokenSavings()
    {
        var mockJev = new Mock<IJevClient>();

        var gatingResponse = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["run_spec_drift"] = new NoulAnswer { Noul = 0.20 },
                ["run_security"] = new NoulAnswer { Noul = 0.15 },
                ["run_reliability"] = new NoulAnswer { Noul = 0.85 },
                ["run_refactoring"] = new NoulAnswer { Noul = 0.30 },
                ["run_tests"] = new NoulAnswer { Noul = 0.90 }
            }
        };

        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.IsAny<IReadOnlyDictionary<string, Question>>(),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(gatingResponse);

        var engine = new TypeSafeJevEngine(mockJev.Object);
        var prContext = new PullRequestContext { PrNumber = 10, Title = "feat: concurrency fix" };
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/Worker.cs", "/mock/src/Worker.cs", 50, 500, "class Worker {}", false, DiffPatch: "@@ -1 +1 @@")],
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
                    MinConfidence = 0.50,
                    AlwaysRunSecurityOnSarifAlerts = true,
                    AlwaysRunSpecDriftOnSpecChanges = true
                }
            }
        };

        var candidateAgents = new[] { "SpecDriftAgent", "SecurityAuditor", "ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent" };
        var result = await engine.EvaluateSubagentGatingAsync(context, candidateAgents, config);

        result.Should().NotBeNull();
        result.ActiveAgentNames.Should().BeEquivalentTo(["ReliabilityAgent", "TestSentinelAgent"]);
        result.SkippedAgentNames.Should().BeEquivalentTo(["SpecDriftAgent", "SecurityAuditor", "RefactoringAgent"]);
        result.EstimatedTokensSaved.Should().Be(3 * 5000L);
    }

    [Fact]
    public async Task TypeSafeJevEngine_SubagentGating_InFullRepoMode_WhenBypassIsFalse_EvaluatesJevSystemOne()
    {
        var mockJev = new Mock<IJevClient>();

        var gatingResponse = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["run_spec_drift"] = new NoulAnswer { Noul = 0.10 },
                ["run_security"] = new NoulAnswer { Noul = 0.20 },
                ["run_reliability"] = new NoulAnswer { Noul = 0.90 },
                ["run_refactoring"] = new NoulAnswer { Noul = 0.40 },
                ["run_tests"] = new NoulAnswer { Noul = 0.85 }
            }
        };

        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.IsAny<IReadOnlyDictionary<string, Question>>(),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(gatingResponse);

        var engine = new TypeSafeJevEngine(mockJev.Object);
        // Full repo mode (no PR)
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/Worker.cs", "/mock/src/Worker.cs", 50, 500, "class Worker {}", false)],
            SarifFindings = []
        };

        var config = new GitGavelConfig(); // SubagentGating defaults to Enabled = true, BypassInFullRepoMode = false

        var candidateAgents = new[] { "SpecDriftAgent", "SecurityAuditor", "ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent" };
        var result = await engine.EvaluateSubagentGatingAsync(context, candidateAgents, config);

        result.Should().NotBeNull();
        result.ActiveAgentNames.Should().BeEquivalentTo(["ReliabilityAgent", "TestSentinelAgent"]);
        result.SkippedAgentNames.Should().BeEquivalentTo(["SpecDriftAgent", "SecurityAuditor", "RefactoringAgent"]);
        result.EstimatedTokensSaved.Should().Be(15000L);
    }

    [Fact]
    public async Task TypeSafeJevEngine_SubagentGating_WhenDisabledInConfig_ActivatesAllAgents()
    {
        var mockJev = new Mock<IJevClient>();
        var engine = new TypeSafeJevEngine(mockJev.Object);

        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [],
            SarifFindings = []
        };

        var config = new GitGavelConfig
        {
            Triage = new TriageConfig
            {
                SubagentGating = new SubagentGatingConfig { Enabled = false }
            }
        };

        var candidates = new[] { "SpecDriftAgent", "SecurityAuditor" };
        var result = await engine.EvaluateSubagentGatingAsync(context, candidates, config);

        result.ActiveAgentNames.Should().BeEquivalentTo(candidates);
        result.SkippedAgentNames.Should().BeEmpty();
        result.EstimatedTokensSaved.Should().Be(0);
        mockJev.Verify(c => c.SystemOneAsync(It.IsAny<JsonContent>(), It.IsAny<IReadOnlyDictionary<string, Question>>(), It.IsAny<JevRequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TypeSafeJevEngine_SubagentGating_InFullRepoMode_WhenBypassIsTrue_ActivatesAllAgentsWithoutCallingJev()
    {
        var mockJev = new Mock<IJevClient>(MockBehavior.Strict);
        var engine = new TypeSafeJevEngine(mockJev.Object);

        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 20, 200, "class App {}", false)],
            SarifFindings = []
        };

        var config = new GitGavelConfig
        {
            Triage = new TriageConfig
            {
                SubagentGating = new SubagentGatingConfig
                {
                    Enabled = true,
                    BypassInFullRepoMode = true
                }
            }
        };

        var candidates = new[] { "SpecDriftAgent", "SecurityAuditor", "ReliabilityAgent" };
        var result = await engine.EvaluateSubagentGatingAsync(context, candidates, config);

        result.ActiveAgentNames.Should().BeEquivalentTo(candidates);
        result.SkippedAgentNames.Should().BeEmpty();
        result.Decisions.Should().OnlyContain(d => d.IsDeterministicOverride && d.Confidence == 1.0);
        mockJev.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TypeSafeJevEngine_SubagentGating_WhenCandidateAgentsEmpty_ReturnsEmptyWithoutCallingJev()
    {
        var mockJev = new Mock<IJevClient>(MockBehavior.Strict);
        var engine = new TypeSafeJevEngine(mockJev.Object);
        var context = new RepoContext { RootDirectory = "/mock", SpecFiles = [], SourceFiles = [], SarifFindings = [] };

        var result = await engine.EvaluateSubagentGatingAsync(context, [], new GitGavelConfig());

        result.Decisions.Should().BeEmpty();
        result.EstimatedTokensSaved.Should().Be(0);
        mockJev.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TypeSafeJevEngine_SubagentGating_CombinesDeterministicOverridesWithJevNoulAnswers()
    {
        var mockJev = new Mock<IJevClient>();
        var gatingResponse = new SystemOneResponse
        {
            Answers = new Dictionary<string, Answer>
            {
                ["run_reliability"] = new NoulAnswer { Noul = 0.80 },
                ["run_refactoring"] = new NoulAnswer { Noul = 0.20 },
                ["run_tests"] = new NoulAnswer { Noul = 0.30 }
            }
        };

        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.IsAny<IReadOnlyDictionary<string, Question>>(),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(gatingResponse);

        var engine = new TypeSafeJevEngine(mockJev.Object);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [new ScannedFile("SPECIFICATION.md", "/mock/SPECIFICATION.md", 50, 500, "# Spec", true, DiffPatch: "@@ -1 +1 @@")],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 50, 500, "class App {}", false, DiffPatch: "@@ -1 +1 @@")],
            SarifFindings = [new SarifFinding("sec-1", "Alert", "error", "src/App.cs", 10, 12)],
            ActivePullRequest = new PullRequestContext { PrNumber = 5, Title = "Update spec and app" }
        };

        var candidates = new[] { "SpecDriftAgent", "SecurityAuditor", "ReliabilityAgent", "RefactoringAgent", "TestSentinelAgent" };
        var result = await engine.EvaluateSubagentGatingAsync(context, candidates, new GitGavelConfig());

        // SpecDriftAgent and SecurityAuditor deterministically overridden; ReliabilityAgent passes Jev Noul (0.80 >= 0.50)
        result.ActiveAgentNames.Should().BeEquivalentTo(["SpecDriftAgent", "SecurityAuditor", "ReliabilityAgent"]);
        result.SkippedAgentNames.Should().BeEquivalentTo(["RefactoringAgent", "TestSentinelAgent"]);
        result.Decisions.Single(d => d.AgentName == "SpecDriftAgent").IsDeterministicOverride.Should().BeTrue();
        result.Decisions.Single(d => d.AgentName == "SecurityAuditor").IsDeterministicOverride.Should().BeTrue();
        result.Decisions.Single(d => d.AgentName == "ReliabilityAgent").IsDeterministicOverride.Should().BeFalse();
    }

    [Fact]
    public async Task TypeSafeJevEngine_SubagentGating_WhenJevClientThrows_FallsBackToActivatingAllRemainingAgents()
    {
        var mockJev = new Mock<IJevClient>();
        mockJev.Setup(c => c.SystemOneAsync(
            It.IsAny<JsonContent>(),
            It.IsAny<IReadOnlyDictionary<string, Question>>(),
            It.IsAny<JevRequestOptions>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Jev API unavailable"));

        var engine = new TypeSafeJevEngine(mockJev.Object);
        var context = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [],
            SourceFiles = [new ScannedFile("src/App.cs", "/mock/src/App.cs", 50, 500, "class App {}", false)],
            SarifFindings = []
        };

        var candidates = new[] { "ReliabilityAgent", "RefactoringAgent" };
        var result = await engine.EvaluateSubagentGatingAsync(context, candidates, new GitGavelConfig());

        result.ActiveAgentNames.Should().BeEquivalentTo(candidates);
        result.SkippedAgentNames.Should().BeEmpty();
        result.Decisions.Should().OnlyContain(d => d.ShouldRun && d.Reason.Contains("fail-open"));
    }

    [Fact]
    public void ExistingIssue_GetEffectiveFingerprint_And_TriageEngineHelper_BuildIssueSummary_HandleBoundaries()
    {
        var issueWithProp = new ExistingIssue(1, "Title", "Body", Fingerprint: "explicit_fp");
        issueWithProp.GetEffectiveFingerprint().Should().Be("explicit_fp");

        var issueWithEmbedded = new ExistingIssue(2, "Title", "Text\n<!-- gitgavel-metadata\nfingerprint: embedded_fp_123\n-->");
        issueWithEmbedded.GetEffectiveFingerprint().Should().Be("embedded_fp_123");

        var issueWithoutFp = new ExistingIssue(3, "Title", "Plain body without metadata");
        issueWithoutFp.GetEffectiveFingerprint().Should().BeNull();

        TriageEngineHelper.BuildIssueSummary(null).Should().Be("");
        TriageEngineHelper.BuildIssueSummary("Short body").Should().Be("Short body");

        var body500 = new string('X', 500);
        TriageEngineHelper.BuildIssueSummary(body500).Should().Be(body500);

        var body505 = new string('X', 505);
        var truncated = TriageEngineHelper.BuildIssueSummary(body505);
        truncated.Should().HaveLength(503);
        truncated.Should().EndWith("...");
    }
}
