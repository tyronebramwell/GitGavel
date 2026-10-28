using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitGavel.GitHub;

public static partial class IssueMetadataParser
{
    [GeneratedRegex(@"<!--\s*gitgavel-metadata\s*([\s\S]*?)-->", RegexOptions.Compiled)]
    private static partial Regex MetadataCommentRegex();

    [GeneratedRegex(@"<!--\s*gitgavel-resolution\s*([\s\S]*?)-->", RegexOptions.Compiled)]
    private static partial Regex ResolutionCommentRegex();

    public static GitGavelIssueMetadata? ParseMetadata(string? issueBody)
    {
        if (string.IsNullOrWhiteSpace(issueBody)) return null;

        var match = MetadataCommentRegex().Match(issueBody);
        if (!match.Success) return null;

        var yaml = match.Groups[1].Value;
        var lines = yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? fingerprint = null;
        string? category = null;
        var score = 3.0;
        var confidence = 0.85;
        string? agent = null;
        var createdAt = DateTime.UtcNow;

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            var key = parts[0].ToLowerInvariant();
            var val = parts[1];

            switch (key)
            {
                case "fingerprint":
                    fingerprint = val;
                    break;
                case "category":
                    category = val;
                    break;
                case "jev_score":
                    if (double.TryParse(val, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var s))
                    {
                        score = s;
                    }
                    break;
                case "jev_confidence":
                    if (double.TryParse(val, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var c))
                    {
                        confidence = c;
                    }
                    break;
                case "agent":
                    agent = val;
                    break;
                case "created_at":
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    {
                        createdAt = dt;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        return new GitGavelIssueMetadata(
            fingerprint,
            category,
            score,
            confidence,
            agent ?? "Unknown",
            createdAt);
    }

    public static GitGavelResolutionMetadata? ParseResolutionMetadata(string? commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody)) return null;

        var match = ResolutionCommentRegex().Match(commentBody);
        if (!match.Success) return null;

        var yaml = match.Groups[1].Value;
        var lines = yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var commits = new List<string>();
        var confidence = 0.90;
        string? agent = null;
        var closedAt = DateTime.UtcNow;

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            var key = parts[0].ToLowerInvariant();
            var val = parts[1];

            switch (key)
            {
                case "resolved_by_commits":
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<string>>(val);
                        if (parsed != null) commits = parsed;
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed commit list JSON
                    }
                    break;
                case "jev_resolution_confidence":
                    if (double.TryParse(val, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var conf))
                    {
                        confidence = conf;
                    }
                    break;
                case "agent":
                    agent = val;
                    break;
                case "closed_at":
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    {
                        closedAt = dt;
                    }
                    break;
            }
        }

        return new GitGavelResolutionMetadata(
            commits,
            confidence,
            agent ?? "IssueResolutionAuditor",
            closedAt);
    }

    public static string? ExtractStickyCommentMarker(string? commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody)) return null;
        const string prefix = "<!-- gitgavel-sticky-comment:";
        var idx = commentBody.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var endIdx = commentBody.IndexOf("-->", idx, StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0) return null;
        return commentBody[idx..(endIdx + 3)].Trim();
    }
}
