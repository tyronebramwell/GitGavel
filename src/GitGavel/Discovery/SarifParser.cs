using System.Text.Json;

namespace GitGavel.Discovery;

public static class SarifParser
{
    public static List<SarifFinding> Parse(string jsonContent)
    {
        var findings = new List<SarifFinding>();
        if (string.IsNullOrWhiteSpace(jsonContent)) return findings;

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (!doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            {
                return findings;
            }

            foreach (var run in runs.EnumerateArray())
            {
                if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var result in results.EnumerateArray())
                {
                    var ruleId = result.TryGetProperty("ruleId", out var ruleIdProp) ? ruleIdProp.GetString() ?? "" : "";
                    var level = result.TryGetProperty("level", out var levelProp) ? levelProp.GetString() ?? "warning" : "warning";

                    var message = "";
                    if (result.TryGetProperty("message", out var msgObj) && msgObj.TryGetProperty("text", out var textProp))
                    {
                        message = textProp.GetString() ?? "";
                    }

                    var filePath = "";
                    var startLine = 1;
                    var endLine = 1;

                    if (result.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array && locs.GetArrayLength() > 0)
                    {
                        var firstLoc = locs[0];
                        if (firstLoc.TryGetProperty("physicalLocation", out var physLoc))
                        {
                            if (physLoc.TryGetProperty("artifactLocation", out var artLoc) && artLoc.TryGetProperty("uri", out var uriProp))
                            {
                                filePath = uriProp.GetString() ?? "";
                            }

                            if (physLoc.TryGetProperty("region", out var reg))
                            {
                                if (reg.TryGetProperty("startLine", out var sLine)) startLine = sLine.GetInt32();
                                if (reg.TryGetProperty("endLine", out var eLine)) endLine = eLine.GetInt32();
                                else endLine = startLine;
                            }
                        }
                    }

                    findings.Add(new SarifFinding(ruleId, message, level, filePath, startLine, endLine));
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed SARIF gracefully
        }

        return findings;
    }

    public static async Task<List<SarifFinding>> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return [];
        var content = await File.ReadAllTextAsync(filePath, ct);
        return Parse(content);
    }
}
