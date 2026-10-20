using System.Security.Cryptography;
using System.Text;

namespace GitGavel.System2.Models;

public record FindingCandidate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Category { get; init; } // Security, SpecDrift, Refactoring
    public required string Severity { get; init; } // Critical, High, Medium, Low
    public required string FilePath { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public required string AgentName { get; init; }
    public required string SuggestedRemediation { get; init; }
    public string? AttackVector { get; init; }
    public string? OwaspCategory { get; init; }
    public string? TestTier { get; init; } // Unit, Integration, E2E
    public string? TestDefectType { get; init; } // MissingTest, ExtraScenario, TestQuality
    public string? Details { get; init; }
    public List<string> References { get; init; } = [];
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    private string? _fingerprint;
    public string Fingerprint
    {
        get => _fingerprint ??= ComputeFingerprint();
        init => _fingerprint = value;
    }

    private string ComputeFingerprint()
    {
        var raw = $"{Category}:{FilePath}:{StartLine}:{Title}:{Summary}".ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

public record PullRequestChangedFile(
    string FilePath,
    string Status,
    int Additions,
    int Deletions,
    string Patch);

public record ExistingPrReviewComment(
    long Id,
    string FilePath,
    int? Line,
    string Body,
    string Author,
    DateTime CreatedAtUtc);

public record ExistingPrReviewSummary(
    long Id,
    string State,
    string Body,
    string Author,
    DateTime SubmittedAtUtc);

public record PullRequestContext
{
    public required int PrNumber { get; init; }
    public required string Title { get; init; }
    public string Body { get; init; } = "";
    public string BaseRef { get; init; } = "main";
    public string HeadRef { get; init; } = "feature";
    public string HeadSha { get; init; } = "";
    public List<PullRequestChangedFile> ChangedFiles { get; init; } = [];
    public List<ExistingPrReviewComment> ExistingReviewComments { get; init; } = [];
    public List<ExistingPrReviewSummary> ExistingReviews { get; init; } = [];
}

public record PrReviewCommentCandidate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string FilePath { get; init; }
    public int Line { get; init; } = 1;
    public required string Category { get; init; } // Security, SpecDrift, Refactoring, Testing
    public required string Severity { get; init; } // Critical, High, Medium, Low
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string? SuggestedCode { get; init; }
}

public record PrReviewAuditResult
{
    public int PrNumber { get; init; }
    public string HeadSha { get; init; } = "";
    public string SummaryObservations { get; init; } = "";
    public List<string> ResolvedThreadsNotes { get; init; } = [];
    public List<PrReviewCommentCandidate> InlineComments { get; init; } = [];
    public string RecommendedEvent { get; init; } = "COMMENT"; // COMMENT, REQUEST_CHANGES, APPROVE
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public long DurationMs { get; init; }
    public bool IsSuccess { get; init; } = true;
    public string? ErrorMessage { get; init; }
}

public record IssueResolutionCandidate
{
    public required int IssueNumber { get; init; }
    public required string IssueTitle { get; init; }
    public bool IsResolved { get; init; }
    public required string HowItWasFixed { get; init; }
    public List<string> ResolvingCommitShas { get; init; } = [];
    public List<string> ResolvingCommitSummaries { get; init; } = [];
    public string AgentName { get; init; } = "IssueResolutionAuditor";
}

public record IssueResolutionAgentResult
{
    public string AgentName { get; init; } = "IssueResolutionAuditor";
    public List<IssueResolutionCandidate> Candidates { get; init; } = [];
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public long DurationMs { get; init; }
    public bool IsSuccess { get; init; } = true;
    public string? ErrorMessage { get; init; }
}
