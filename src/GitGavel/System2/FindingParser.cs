using System.Text.Json;
using System.Text.RegularExpressions;
using GitGavel.System2.Models;

namespace GitGavel.System2;

public static partial class FindingParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.Compiled)]
    private static partial Regex MarkdownJsonFence();

    public static List<FindingCandidate> ParseFindings(string rawText, string agentName, string defaultCategory)
    {
        var findings = new List<FindingCandidate>();
        if (string.IsNullOrWhiteSpace(rawText)) return findings;

        var jsonToParse = rawText.Trim();

        // Extract JSON if wrapped in markdown fences
        var match = MarkdownJsonFence().Match(jsonToParse);
        if (match.Success)
        {
            jsonToParse = match.Groups[1].Value.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonToParse);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var finding = ParseSingleFinding(item, agentName, defaultCategory);
                    if (finding != null) findings.Add(finding);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Might be wrapped in { "findings": [...] }
                if (doc.RootElement.TryGetProperty("findings", out var array) && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        var finding = ParseSingleFinding(item, agentName, defaultCategory);
                        if (finding != null) findings.Add(finding);
                    }
                }
                else
                {
                    var single = ParseSingleFinding(doc.RootElement, agentName, defaultCategory);
                    if (single != null) findings.Add(single);
                }
            }
        }
        catch (JsonException)
        {
            // Fallback: If JSON is slightly truncated or malformed, attempt best-effort
        }

        return findings;
    }

    private static FindingCandidate? ParseSingleFinding(JsonElement elem, string agentName, string defaultCategory)
    {
        if (elem.ValueKind != JsonValueKind.Object) return null;

        var title = GetString(elem, "title") ?? "Untitled Finding";
        var summary = GetString(elem, "summary") ?? "";
        var category = GetString(elem, "category") ?? defaultCategory;
        var severity = GetString(elem, "severity") ?? "Medium";
        var filePath = GetString(elem, "filePath") ?? "Unknown";
        var remediation = GetString(elem, "suggestedRemediation") ?? GetString(elem, "remediation") ?? "Review and remediate.";
        var details = GetString(elem, "details");
        var attackVector = GetString(elem, "attackVector");
        var owasp = GetString(elem, "owaspCategory") ?? GetString(elem, "owasp");

        var startLine = GetInt(elem, "startLine", 1);
        var endLine = GetInt(elem, "endLine", startLine);

        return new FindingCandidate
        {
            Title = title,
            Summary = summary,
            Category = category,
            Severity = severity,
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            AgentName = agentName,
            SuggestedRemediation = remediation,
            AttackVector = attackVector,
            OwaspCategory = owasp,
            Details = details
        };
    }

    private static string? GetString(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p) && p.ValueKind == JsonValueKind.String)
        {
            return p.GetString();
        }
        return null;
    }

    private static int GetInt(JsonElement elem, string propName, int defaultValue)
    {
        if (elem.TryGetProperty(propName, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var val))
        {
            return val;
        }
        return defaultValue;
    }
}
