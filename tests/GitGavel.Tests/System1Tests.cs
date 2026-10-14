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
}
