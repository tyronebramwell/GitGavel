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
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("runs", out var runs) ||
                runs.ValueKind != JsonValueKind.Array)
            {
                return findings;
            }

            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind != JsonValueKind.Object ||
                    !run.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var result in results.EnumerateArray())
                {
                    if (result.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var ruleId = result.TryGetProperty("ruleId", out var ruleIdProp) && ruleIdProp.ValueKind == JsonValueKind.String
                        ? ruleIdProp.GetString() ?? ""
                        : "";
                    var level = result.TryGetProperty("level", out var levelProp) && levelProp.ValueKind == JsonValueKind.String
                        ? levelProp.GetString() ?? "warning"
                        : "warning";

                    var message = "";
                    if (result.TryGetProperty("message", out var msgObj))
                    {
                        if (msgObj.ValueKind == JsonValueKind.String)
                        {
                            message = msgObj.GetString() ?? "";
                        }
                        else if (msgObj.ValueKind == JsonValueKind.Object &&
                                 msgObj.TryGetProperty("text", out var textProp) &&
                                 textProp.ValueKind == JsonValueKind.String)
                        {
                            message = textProp.GetString() ?? "";
                        }
                    }

                    var filePath = "";
                    var startLine = 1;
                    var endLine = 1;

                    if (result.TryGetProperty("locations", out var locs) &&
                        locs.ValueKind == JsonValueKind.Array &&
                        locs.GetArrayLength() > 0)
                    {
                        var firstLoc = locs[0];
                        if (firstLoc.ValueKind == JsonValueKind.Object)
                        {
                            if (firstLoc.TryGetProperty("physicalLocation", out var physLoc) &&
                                physLoc.ValueKind == JsonValueKind.Object)
                            {
                                if (physLoc.TryGetProperty("artifactLocation", out var artLoc) &&
                                    artLoc.ValueKind == JsonValueKind.Object &&
                                    artLoc.TryGetProperty("uri", out var uriProp) &&
                                    uriProp.ValueKind == JsonValueKind.String)
                                {
                                    filePath = uriProp.GetString() ?? "";
                                }
                                else if (physLoc.TryGetProperty("uri", out var legacyUriProp) &&
                                         legacyUriProp.ValueKind == JsonValueKind.String)
                                {
                                    filePath = legacyUriProp.GetString() ?? "";
                                }

                                if (physLoc.TryGetProperty("region", out var reg) &&
                                    reg.ValueKind == JsonValueKind.Object)
                                {
                                    if (reg.TryGetProperty("startLine", out var sLine) && sLine.TryGetInt32(out var sVal))
                                        startLine = sVal;
                                    if (reg.TryGetProperty("endLine", out var eLine) && eLine.TryGetInt32(out var eVal))
                                        endLine = eVal;
                                    else
                                        endLine = startLine;
                                }
                            }
                            else if (firstLoc.TryGetProperty("resultFile", out var resFile) &&
                                     resFile.ValueKind == JsonValueKind.Object)
                            {
                                if (resFile.TryGetProperty("uri", out var uriProp) &&
                                    uriProp.ValueKind == JsonValueKind.String)
                                {
                                    filePath = uriProp.GetString() ?? "";
                                }

                                if (resFile.TryGetProperty("region", out var reg) &&
                                    reg.ValueKind == JsonValueKind.Object)
                                {
                                    if (reg.TryGetProperty("startLine", out var sLine) && sLine.TryGetInt32(out var sVal))
                                        startLine = sVal;
                                    if (reg.TryGetProperty("endLine", out var eLine) && eLine.TryGetInt32(out var eVal))
                                        endLine = eVal;
                                    else
                                        endLine = startLine;
                                }
                            }
                        }
                    }

                    findings.Add(new SarifFinding(ruleId, message, level, filePath, startLine, endLine));
                }
            }
        }
        catch (Exception)
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
