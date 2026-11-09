using System.Text;
using System.Xml.Linq;
using GitGavel.Configuration;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace GitGavel.Discovery;

public record FailedTestRecord(
    string TestName,
    string? ErrorMessage,
    string? StackTrace);

public record TestRunArtifactSummary(
    int TotalTests,
    int PassedTests,
    int FailedTests,
    int SkippedTests,
    IReadOnlyList<FailedTestRecord> FailedTestDetails);

public record UncoveredFileCoverageSummary(
    string FilePath,
    double LineCoveragePercent,
    IReadOnlyList<int> UncoveredLines);

public record CoverageArtifactSummary(
    double LineCoveragePercent,
    double BranchCoveragePercent,
    IReadOnlyList<UncoveredFileCoverageSummary> UncoveredFiles);

public record IngestedPipelineArtifact(
    string Id,
    string Type,
    IReadOnlyList<string> MatchedFiles,
    string FormattedSummary,
    TestRunArtifactSummary? TestSummary = null,
    CoverageArtifactSummary? CoverageSummary = null,
    IReadOnlyList<SarifFinding>? SarifFindings = null);

public record PipelineArtifactsContext
{
    public IReadOnlyList<IngestedPipelineArtifact> Artifacts { get; init; } = [];
    public int TotalTestFailures => Artifacts.Where(a => a.TestSummary != null).Sum(a => a.TestSummary!.FailedTests);
    public int TotalTestsExecuted => Artifacts.Where(a => a.TestSummary != null).Sum(a => a.TestSummary!.TotalTests);
    public double? LowestLineCoveragePercent =>
        Artifacts.Where(a => a.CoverageSummary != null).Select(a => (double?)a.CoverageSummary!.LineCoveragePercent).Min();
    public int AdditionalSarifAlertsCount =>
        Artifacts.Where(a => a.SarifFindings != null).Sum(a => a.SarifFindings!.Count);

    public IngestedPipelineArtifact? GetById(string id) =>
        Artifacts.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public string BuildPromptSectionForAgent(IEnumerable<string>? artifactIds, int maxChars = 12000)
    {
        if (Artifacts.Count == 0)
        {
            return string.Empty;
        }

        var requestedIds = artifactIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = requestedIds == null || requestedIds.Count == 0
            ? []
            : Artifacts.Where(a => requestedIds.Contains(a.Id)).ToList();

        if (selected.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("### Current CI Pipeline Run Artifacts:");
        foreach (var artifact in selected)
        {
            sb.AppendLine($"--- Pipeline Artifact [{artifact.Id}] (type: {artifact.Type}) ---");
            sb.AppendLine(artifact.FormattedSummary);
        }

        var result = sb.ToString();
        if (maxChars > 0 && result.Length > maxChars)
        {
            return result[..maxChars] + "\n...(artifact context truncated)";
        }

        return result;
    }
}

public static class PipelineArtifactIngestor
{
    public static async Task<PipelineArtifactsContext> IngestAsync(
        string rootDir,
        ArtifactsConfig? config,
        bool allowExternalPaths = false,
        CancellationToken ct = default)
    {
        if (config == null || config.Sources.Count == 0)
        {
            return new PipelineArtifactsContext();
        }

        var fullRoot = Path.GetFullPath(rootDir);
        PathSecurity.ValidateRootDirectory(fullRoot);

        var ingested = new List<IngestedPipelineArtifact>();

        foreach (var source in config.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id))
            {
                continue;
            }

            var matchedPaths = ResolveArtifactFiles(fullRoot, source.Paths, allowExternalPaths);
            if (matchedPaths.Count == 0)
            {
                continue;
            }

            var type = (source.Type ?? "text").Trim().ToLowerInvariant();
            var artifact = await ParseArtifactSourceAsync(source.Id, type, fullRoot, matchedPaths, config.MaxArtifactContextChars, ct);
            if (artifact != null)
            {
                ingested.Add(artifact);
            }
        }

        return new PipelineArtifactsContext
        {
            Artifacts = ingested
        };
    }

    private static List<string> ResolveArtifactFiles(string rootDir, List<string>? patterns, bool allowExternalPaths)
    {
        if (patterns == null || patterns.Count == 0)
        {
            return [];
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var directFiles = new List<string>();

        foreach (var pattern in patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (!allowExternalPaths && !PathSecurity.IsSafeRelativePath(pattern))
            {
                continue;
            }

            if (!pattern.Contains('*') && !pattern.Contains('?'))
            {
                try
                {
                    var resolved = PathSecurity.ResolveAndVerifyWithinRoot(pattern, rootDir, allowExternalPaths);
                    if (File.Exists(resolved))
                    {
                        directFiles.Add(resolved);
                        continue;
                    }
                }
                catch
                {
                    // Ignore invalid path
                }
            }

            if (PathSecurity.IsSafeRelativePath(pattern))
            {
                matcher.AddInclude(pattern);
            }
        }

        var dirInfo = new DirectoryInfo(rootDir);
        if (dirInfo.Exists)
        {
            var matchResult = matcher.Execute(new DirectoryInfoWrapper(dirInfo));
            foreach (var match in matchResult.Files)
            {
                try
                {
                    var fullPath = PathSecurity.ResolveAndVerifyWithinRoot(match.Path, rootDir, allowExternalPaths);
                    if (File.Exists(fullPath))
                    {
                        directFiles.Add(fullPath);
                    }
                }
                catch
                {
                    // Ignore unsafe path
                }
            }
        }

        return directFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<IngestedPipelineArtifact?> ParseArtifactSourceAsync(
        string id,
        string type,
        string rootDir,
        List<string> filePaths,
        int maxChars,
        CancellationToken ct)
    {
        var relPaths = filePaths.Select(p => Path.GetRelativePath(rootDir, p).Replace('\\', '/')).ToList();

        switch (type)
        {
            case "trx":
            case "junit":
            {
                var summary = await ParseTestResultsAsync(filePaths, type, ct);
                var sb = new StringBuilder();
                sb.AppendLine($"Test Execution Summary: {summary.TotalTests} total, {summary.PassedTests} passed, {summary.FailedTests} failed, {summary.SkippedTests} skipped.");
                foreach (var fail in summary.FailedTestDetails.Take(15))
                {
                    sb.AppendLine($"- FAILED `{fail.TestName}`: {fail.ErrorMessage}");
                    if (!string.IsNullOrWhiteSpace(fail.StackTrace))
                    {
                        var trace = fail.StackTrace.Length > 400 ? fail.StackTrace[..400] + "..." : fail.StackTrace;
                        sb.AppendLine($"  Stack: {trace}");
                    }
                }

                return new IngestedPipelineArtifact(id, type, relPaths, TruncateText(sb.ToString().Trim(), maxChars), TestSummary: summary);
            }

            case "cobertura":
            case "lcov":
            case "opencover":
            {
                var cov = await ParseCoverageAsync(filePaths, type, ct);
                var sb = new StringBuilder();
                sb.AppendLine($"Code Coverage Summary: Line Coverage = {cov.LineCoveragePercent:F1}%, Branch Coverage = {cov.BranchCoveragePercent:F1}%.");
                foreach (var file in cov.UncoveredFiles.Take(15))
                {
                    var lines = string.Join(", ", file.UncoveredLines.Take(20));
                    sb.AppendLine($"- `{file.FilePath}` ({file.LineCoveragePercent:F1}% line coverage) — Uncovered lines: [{lines}]");
                }

                return new IngestedPipelineArtifact(id, type, relPaths, TruncateText(sb.ToString().Trim(), maxChars), CoverageSummary: cov);
            }

            case "sarif":
            {
                var findings = new List<SarifFinding>();
                foreach (var fp in filePaths)
                {
                    var parsed = await SarifParser.ParseFileAsync(fp, ct);
                    findings.AddRange(parsed);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"SARIF Static Scan Summary: {findings.Count} alert(s).");
                foreach (var f in findings.Take(15))
                {
                    sb.AppendLine($"- [{f.Level}] {f.RuleId} at `{f.FilePath}:{f.StartLine}` — {f.Message}");
                }

                return new IngestedPipelineArtifact(id, type, relPaths, TruncateText(sb.ToString().Trim(), maxChars), SarifFindings: findings);
            }

            default:
            {
                var sb = new StringBuilder();
                foreach (var fp in filePaths.Take(5))
                {
                    var content = await File.ReadAllTextAsync(fp, ct);
                    var rel = Path.GetRelativePath(rootDir, fp).Replace('\\', '/');
                    sb.AppendLine($"File `{rel}`:");
                    sb.AppendLine(TruncateText(content, Math.Max(1000, maxChars / Math.Max(1, filePaths.Count))));
                }

                return new IngestedPipelineArtifact(id, type, relPaths, TruncateText(sb.ToString().Trim(), maxChars));
            }
        }
    }

    public static async Task<TestRunArtifactSummary> ParseTestResultsAsync(
        IEnumerable<string> filePaths,
        string type,
        CancellationToken ct = default)
    {
        var total = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<FailedTestRecord>();

        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;
            var xmlText = await File.ReadAllTextAsync(path, ct);
            if (string.IsNullOrWhiteSpace(xmlText)) continue;

            try
            {
                var doc = XDocument.Parse(xmlText);
                if (type == "trx" || doc.Root?.Name.LocalName == "TestRun")
                {
                    var counters = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");
                    if (counters != null)
                    {
                        total += ParseIntAttr(counters, "total");
                        passed += ParseIntAttr(counters, "passed");
                        failed += ParseIntAttr(counters, "failed");
                        skipped += ParseIntAttr(counters, "notExecuted");
                    }

                    var unitTestResults = doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult").ToList();
                    if (counters == null)
                    {
                        total += unitTestResults.Count;
                        passed += unitTestResults.Count(r => string.Equals(r.Attribute("outcome")?.Value, "Passed", StringComparison.OrdinalIgnoreCase));
                        failed += unitTestResults.Count(r => string.Equals(r.Attribute("outcome")?.Value, "Failed", StringComparison.OrdinalIgnoreCase));
                        skipped += unitTestResults.Count(r => string.Equals(r.Attribute("outcome")?.Value, "NotExecuted", StringComparison.OrdinalIgnoreCase));
                    }

                    foreach (var res in unitTestResults.Where(r => string.Equals(r.Attribute("outcome")?.Value, "Failed", StringComparison.OrdinalIgnoreCase)))
                    {
                        var testName = res.Attribute("testName")?.Value ?? "UnknownTest";
                        var msg = res.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value?.Trim();
                        var stack = res.Descendants().FirstOrDefault(e => e.Name.LocalName == "StackTrace")?.Value?.Trim();
                        failures.Add(new FailedTestRecord(testName, msg, stack));
                    }
                }
                else
                {
                    // JUnit XML format
                    var testCases = doc.Descendants().Where(e => e.Name.LocalName == "testcase").ToList();
                    total += testCases.Count;
                    foreach (var tc in testCases)
                    {
                        var className = tc.Attribute("classname")?.Value;
                        var name = tc.Attribute("name")?.Value ?? "UnknownTest";
                        var fullName = !string.IsNullOrWhiteSpace(className) ? $"{className}.{name}" : name;

                        var failureEl = tc.Elements().FirstOrDefault(e => e.Name.LocalName is "failure" or "error");
                        var skippedEl = tc.Elements().FirstOrDefault(e => e.Name.LocalName == "skipped");

                        if (failureEl != null)
                        {
                            failed++;
                            var msg = failureEl.Attribute("message")?.Value ?? failureEl.Value.Trim();
                            failures.Add(new FailedTestRecord(fullName, msg, failureEl.Value.Trim()));
                        }
                        else if (skippedEl != null)
                        {
                            skipped++;
                        }
                        else
                        {
                            passed++;
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed XML test files
            }
        }

        return new TestRunArtifactSummary(total, passed, failed, skipped, failures);
    }

    public static async Task<CoverageArtifactSummary> ParseCoverageAsync(
        IEnumerable<string> filePaths,
        string type,
        CancellationToken ct = default)
    {
        var lineRates = new List<double>();
        var branchRates = new List<double>();
        var uncoveredFiles = new List<UncoveredFileCoverageSummary>();

        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;
            var content = await File.ReadAllTextAsync(path, ct);
            if (string.IsNullOrWhiteSpace(content)) continue;

            try
            {
                if (type == "lcov" || path.EndsWith(".info", StringComparison.OrdinalIgnoreCase))
                {
                    var totalLines = 0;
                    var hitLines = 0;
                    string? currentFile = null;
                    var fileUncovered = new List<int>();
                    var fileTotal = 0;
                    var fileHits = 0;

                    foreach (var rawLine in content.Split('\n'))
                    {
                        var line = rawLine.Trim();
                        if (line.StartsWith("SF:", StringComparison.Ordinal))
                        {
                            currentFile = line[3..].Trim();
                            fileUncovered = [];
                            fileTotal = 0;
                            fileHits = 0;
                        }
                        else if (line.StartsWith("DA:", StringComparison.Ordinal))
                        {
                            var parts = line[3..].Split(',');
                            if (parts.Length >= 2 && int.TryParse(parts[0], out var lineNo) && int.TryParse(parts[1], out var hits))
                            {
                                totalLines++;
                                fileTotal++;
                                if (hits > 0)
                                {
                                    hitLines++;
                                    fileHits++;
                                }
                                else
                                {
                                    fileUncovered.Add(lineNo);
                                }
                            }
                        }
                        else if (line == "end_of_record" && currentFile != null)
                        {
                            if (fileUncovered.Count > 0 && fileTotal > 0)
                            {
                                var pct = (double)fileHits / fileTotal * 100.0;
                                uncoveredFiles.Add(new UncoveredFileCoverageSummary(currentFile, pct, fileUncovered));
                            }
                            currentFile = null;
                        }
                    }

                    if (totalLines > 0)
                    {
                        lineRates.Add((double)hitLines / totalLines * 100.0);
                    }
                }
                else
                {
                    // Cobertura XML
                    var doc = XDocument.Parse(content);
                    var root = doc.Root;
                    if (root != null)
                    {
                        if (double.TryParse(root.Attribute("line-rate")?.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lr))
                        {
                            lineRates.Add(lr <= 1.0 ? lr * 100.0 : lr);
                        }
                        if (double.TryParse(root.Attribute("branch-rate")?.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var br))
                        {
                            branchRates.Add(br <= 1.0 ? br * 100.0 : br);
                        }

                        foreach (var cls in doc.Descendants().Where(e => e.Name.LocalName == "class"))
                        {
                            var fileName = cls.Attribute("filename")?.Value;
                            if (string.IsNullOrWhiteSpace(fileName)) continue;

                            var lines = cls.Descendants().Where(e => e.Name.LocalName == "line").ToList();
                            if (lines.Count == 0) continue;

                            var zeroHitLines = lines
                                .Where(l => ParseIntAttr(l, "hits") == 0)
                                .Select(l => ParseIntAttr(l, "number"))
                                .Where(n => n > 0)
                                .Distinct()
                                .OrderBy(n => n)
                                .ToList();

                            if (zeroHitLines.Count > 0)
                            {
                                var covered = lines.Count - zeroHitLines.Count;
                                var pct = (double)covered / lines.Count * 100.0;
                                uncoveredFiles.Add(new UncoveredFileCoverageSummary(fileName, pct, zeroHitLines));
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed coverage files
            }
        }

        var avgLine = lineRates.Count > 0 ? lineRates.Average() : 100.0;
        var avgBranch = branchRates.Count > 0 ? branchRates.Average() : avgLine;
        var mergedFiles = uncoveredFiles
            .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UncoveredFileCoverageSummary(
                g.Key,
                g.Min(x => x.LineCoveragePercent),
                g.SelectMany(x => x.UncoveredLines).Distinct().OrderBy(n => n).ToList()))
            .OrderBy(f => f.LineCoveragePercent)
            .ToList();

        return new CoverageArtifactSummary(avgLine, avgBranch, mergedFiles);
    }

    private static int ParseIntAttr(XElement el, string attrName) =>
        int.TryParse(el.Attribute(attrName)?.Value, out var v) ? v : 0;

    private static string TruncateText(string text, int maxChars) =>
        maxChars > 0 && text.Length > maxChars
            ? text[..maxChars] + "\n...(truncated)"
            : text;
}
