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
    public string? Details { get; init; }
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
