using System.Text.Json;
using System.Text.RegularExpressions;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2;

public sealed class FindingParserService : IFindingParser
{
    public List<FindingCandidate> ParseFindings(string rawResponse, string agentName, string defaultCategory, Action<string>? onWarning = null)
        => FindingParser.ParseFindings(rawResponse, agentName, defaultCategory, onWarning);

    public List<IssueResolutionCandidate> ParseIssueResolutions(string rawResponse, Action<string>? onWarning = null)
        => FindingParser.ParseIssueResolutions(rawResponse, onWarning);
}

public static partial class FindingParser
{
    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CodeFenceRegex();

    public static List<FindingCandidate> ParseFindings(
        string rawResponse,
        string agentName,
        string defaultCategory,
        Action<string>? onWarning = null)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return [];
        }

        var jsonText = ExtractJsonArrayOrObject(rawResponse);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return [];
        }

        var results = new List<FindingCandidate>();
        try
        {
            using var doc = ParseJsonDocumentWithSalvage(jsonText, agentName, onWarning);
            if (doc == null)
            {
                return results;
            }

            var arrayElement = ResolveArrayElement(doc.RootElement, "findings", "issues");
            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in arrayElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var candidate = ParseSingleFinding(item, agentName, defaultCategory);
                if (candidate != null)
                {
                    results.Add(candidate);
                }
            }
        }
        catch (JsonException ex)
        {
            onWarning?.Invoke($"Failed to parse findings JSON for {agentName}: {ex.Message}");
        }

        return results;
    }

    public static List<IssueResolutionCandidate> ParseIssueResolutions(string rawResponse, Action<string>? onWarning = null)
        => IssueResolutionParser.ParseIssueResolutions(rawResponse, onWarning);

    private static FindingCandidate? ParseSingleFinding(JsonElement item, string agentName, string defaultCategory)
    {
        var title = FindingReferenceEnricher.GetString(item, "title")
                    ?? FindingReferenceEnricher.GetString(item, "name");
        var summary = FindingReferenceEnricher.GetString(item, "summary")
                      ?? FindingReferenceEnricher.GetString(item, "description");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var category = FindingReferenceEnricher.GetString(item, "category") ?? defaultCategory;
        var severity = FindingReferenceEnricher.GetString(item, "severity") ?? "Medium";
        var filePath = FindingReferenceEnricher.GetString(item, "filePath")
                       ?? FindingReferenceEnricher.GetString(item, "file")
                       ?? FindingReferenceEnricher.GetString(item, "location")
                       ?? "Unknown";
        var startLine = GetInt(item, "startLine") ?? GetInt(item, "line") ?? 1;
        var endLine = GetInt(item, "endLine") ?? startLine;
        var remediation = FindingReferenceEnricher.GetString(item, "suggestedRemediation")
                          ?? FindingReferenceEnricher.GetString(item, "remediation")
                          ?? FindingReferenceEnricher.GetString(item, "fix")
                          ?? "";
        var owasp = FindingReferenceEnricher.GetString(item, "owaspCategory")
                    ?? FindingReferenceEnricher.GetString(item, "owasp");
        var attackVector = FindingReferenceEnricher.GetString(item, "attackVector")
                           ?? FindingReferenceEnricher.GetString(item, "exploitVector");
        var details = FindingReferenceEnricher.GetString(item, "details");
        var testTier = FindingReferenceEnricher.GetString(item, "testTier")
                       ?? FindingReferenceEnricher.GetString(item, "tier");
        var defectType = FindingReferenceEnricher.GetString(item, "defectType");
        var testDefectType = FindingReferenceEnricher.GetString(item, "testDefectType");

        var references = FindingReferenceEnricher.ExtractAndEnrichReferences(item, category, owasp, title);

        return new FindingCandidate
        {
            Title = title,
            Summary = summary,
            Details = details,
            Category = category,
            Severity = severity,
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            SuggestedRemediation = remediation,
            AgentName = agentName,
            OwaspCategory = owasp,
            AttackVector = attackVector,
            TestTier = testTier,
            DefectType = defectType,
            TestDefectType = testDefectType,
            References = references
        };
    }

    internal static JsonDocument? ParseJsonDocumentWithSalvage(string jsonText, string agentName, Action<string>? onWarning)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        try
        {
            return JsonDocument.Parse(jsonText, options);
        }
        catch (JsonException firstEx)
        {
            // Attempt salvage if truncated array response
            var salvaged = TrySalvageTruncatedJsonArray(jsonText);
            if (salvaged != null)
            {
                try
                {
                    var doc = JsonDocument.Parse(salvaged, options);
                    onWarning?.Invoke($"Salvaged truncated JSON response from {agentName} after parse error: {firstEx.Message}");
                    return doc;
                }
                catch (JsonException)
                {
                    // Fall through to report original exception
                }
            }

            onWarning?.Invoke($"Failed to parse JSON response from {agentName}: {firstEx.Message}");
            return null;
        }
    }

    internal static string? TrySalvageTruncatedJsonArray(string jsonText)
    {
        var startIdx = jsonText.IndexOf('[');
        if (startIdx < 0)
        {
            return null;
        }

        // Walk backwards from end to find the last closing brace '}' of a completed object
        var searchEnd = jsonText.Length - 1;
        while (searchEnd > startIdx)
        {
            var lastBrace = jsonText.LastIndexOf('}', searchEnd);
            if (lastBrace <= startIdx)
            {
                break;
            }

            var candidate = jsonText[startIdx..(lastBrace + 1)] + "]";
            try
            {
                using var _ = JsonDocument.Parse(candidate, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                return candidate;
            }
            catch (JsonException)
            {
                searchEnd = lastBrace - 1;
            }
        }

        return null;
    }

    internal static JsonElement ResolveArrayElement(JsonElement root, string primaryProperty, string secondaryProperty)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (FindingReferenceEnricher.TryGetPropertyCaseInsensitive(root, primaryProperty, out var prop1) &&
                prop1.ValueKind == JsonValueKind.Array)
            {
                return prop1;
            }

            if (FindingReferenceEnricher.TryGetPropertyCaseInsensitive(root, secondaryProperty, out var prop2) &&
                prop2.ValueKind == JsonValueKind.Array)
            {
                return prop2;
            }
        }

        return root;
    }

    internal static string ExtractJsonArrayOrObject(string raw)
    {
        var fenceMatch = CodeFenceRegex().Match(raw);
        if (fenceMatch.Success)
        {
            return fenceMatch.Groups[1].Value.Trim();
        }

        var trimmed = raw.Trim();
        var startBracket = trimmed.IndexOf('[');
        var endBracket = trimmed.LastIndexOf(']');
        if (startBracket >= 0 && endBracket > startBracket)
        {
            return trimmed[startBracket..(endBracket + 1)];
        }

        var startBrace = trimmed.IndexOf('{');
        var endBrace = trimmed.LastIndexOf('}');
        if (startBracket >= 0 && (startBrace < 0 || startBracket < startBrace))
        {
            // Starts as a JSON array '[' without a matching ']'; return from '[' so ParseJsonDocumentWithSalvage handles truncation and emits warning
            return trimmed[startBracket..];
        }

        if (startBrace >= 0 && endBrace > startBrace)
        {
            return trimmed[startBrace..(endBrace + 1)];
        }

        return trimmed;
    }

    internal static int? GetInt(JsonElement element, string propertyName)
    {
        if (FindingReferenceEnricher.TryGetPropertyCaseInsensitive(element, propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
            {
                return val;
            }

            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    internal static bool? GetBool(JsonElement element, string propertyName)
    {
        if (FindingReferenceEnricher.TryGetPropertyCaseInsensitive(element, propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
