using System.Text.Json;
using GitGavel.System2.Models;

namespace GitGavel.System2;

public static class IssueResolutionParser
{
    public static List<IssueResolutionCandidate> ParseIssueResolutions(string rawResponse, Action<string>? onWarning = null)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return [];
        }

        var jsonText = FindingParser.ExtractJsonArrayOrObject(rawResponse);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return [];
        }

        var results = new List<IssueResolutionCandidate>();
        try
        {
            using var doc = FindingParser.ParseJsonDocumentWithSalvage(jsonText, "IssueResolutionAuditor", onWarning);
            if (doc == null)
            {
                return results;
            }

            var arrayElement = FindingParser.ResolveArrayElement(doc.RootElement, "resolutions", "issues");
            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in arrayElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var candidate = ParseSingleResolution(item);
                if (candidate != null)
                {
                    results.Add(candidate);
                }
            }
        }
        catch (JsonException ex)
        {
            onWarning?.Invoke($"Failed to parse issue resolution JSON: {ex.Message}");
        }

        return results;
    }

    private static IssueResolutionCandidate? ParseSingleResolution(JsonElement item)
    {
        var issueNumber = FindingParser.GetInt(item, "issueNumber") ?? FindingParser.GetInt(item, "number") ?? 0;
        if (issueNumber <= 0)
        {
            return null;
        }

        var issueTitle = FindingReferenceEnricher.GetString(item, "issueTitle")
                         ?? FindingReferenceEnricher.GetString(item, "title")
                         ?? $"Issue #{issueNumber}";
        var isResolved = FindingParser.GetBool(item, "isResolved")
                         ?? FindingParser.GetBool(item, "resolved")
                         ?? false;
        var howItWasFixed = FindingReferenceEnricher.GetString(item, "howItWasFixed")
                            ?? FindingReferenceEnricher.GetString(item, "resolutionSummary")
                            ?? FindingReferenceEnricher.GetString(item, "explanation")
                            ?? "";

        var commitShas = ParseCommitShas(item);

        return new IssueResolutionCandidate
        {
            IssueNumber = issueNumber,
            IssueTitle = issueTitle,
            IsResolved = isResolved,
            HowItWasFixed = howItWasFixed,
            ResolvingCommitShas = commitShas
        };
    }

    private static List<string> ParseCommitShas(JsonElement item)
    {
        var commitShas = new List<string>();
        if (FindingReferenceEnricher.TryGetPropertyCaseInsensitive(item, "resolvingCommitShas", out var shasElement) &&
            shasElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var shaItem in shasElement.EnumerateArray())
            {
                if (shaItem.ValueKind == JsonValueKind.String)
                {
                    var sha = shaItem.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(sha))
                    {
                        commitShas.Add(sha);
                    }
                }
            }
        }
        else if (FindingReferenceEnricher.TryGetPropertyCaseInsensitive(item, "commitSha", out var singleSha) &&
                 singleSha.ValueKind == JsonValueKind.String)
        {
            var sha = singleSha.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(sha))
            {
                commitShas.Add(sha);
            }
        }

        return commitShas;
    }
}
