using GitGavel.System2.Models;

namespace GitGavel.System1.Models;

public record ExistingIssue(
    int Number,
    string Title,
    string Body,
    string State = "open",
    List<string>? Labels = null);

public enum TriageStatus
{
    AcceptedToBacklog,
    RoutedToHumanReview,
    SuppressedDuplicate,
    DiscardedNoise
}

public record TriageDecision
{
    public required FindingCandidate Finding { get; init; }
    public bool IsDuplicate { get; init; }
    public int? DuplicateOfIssueNumber { get; init; }
    public double DuplicateProbability { get; init; }
    public double JevScore { get; init; } // 1.0 to 5.0
    public string PriorityLabel { get; init; } = "P2"; // P0, P1, P2, P3
    public string TargetColumn { get; init; } = "Backlog"; // "Backlog", "NeedsHumanReview", "Discard"
    public double Confidence { get; init; } = 0.90;
    public TriageStatus Status { get; init; } = TriageStatus.AcceptedToBacklog;
    public string Reason { get; init; } = "";
}
