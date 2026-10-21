using FluentAssertions;
using GitGavel.Configuration;
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
    public async Task TriageAll_EnforcesMaxIssuesPerRunCap()
    {
        var engine = new MockTriageEngine();
        var config = new GitGavelConfig
        {
            Triage = new TriageConfig { MaxIssuesPerRun = 2 }
        };

        var findings = new List<FindingCandidate>
        {
            CreateFinding("Finding 1", "Security", "Critical", "File1.cs"),
            CreateFinding("Finding 2", "Security", "High", "File2.cs"),
            CreateFinding("Finding 3", "Security", "Medium", "File3.cs")
        };

        var decisions = await engine.TriageAllAsync(findings, [], config);

        decisions.Should().HaveCount(3);
        decisions.Count(d => d.Status == TriageStatus.AcceptedToBacklog).Should().Be(2);
        decisions.Count(d => d.Status == TriageStatus.RoutedToHumanReview).Should().Be(1);
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
}
