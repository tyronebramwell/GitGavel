using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System1.Models;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace GitGavel.System1.Engines;

public static partial class NonJevGateEvaluator
{
    [GeneratedRegex(@"^files\.changedMatches\(\s*['""](?<glob>[^'""]+)['""]\s*\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ChangedMatchesRegex();

    [GeneratedRegex(@"^inputs\.findingsCount\(\s*severity\s*(?<op>>=|==|>)\s*['""](?<sev>[^'""]+)['""]\s*\)\s*(?<cmpOp>>=|<=|==|!=|>|<)\s*(?<rhs>-?\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FindingsSeverityComparisonRegex();

    [GeneratedRegex(@"^(?<lhs>[a-zA-Z0-9_.]+)\s*(?<op>>=|<=|==|!=|>|<)\s*(?<rhs>-?\d+(?:\.\d+)?|true|false)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NumericOrBoolComparisonRegex();

    public static bool EvaluateExpression(
        string? expression,
        RepoContext context,
        IReadOnlyList<FindingCandidate>? upstreamFindings = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        var trimmed = expression.Trim();

        // Split top-level OR (||)
        var orParts = trimmed.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (orParts.Length > 1)
        {
            return orParts.Any(part => EvaluateExpression(part, context, upstreamFindings));
        }

        // Split top-level AND (&&)
        var andParts = trimmed.Split("&&", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (andParts.Length > 1)
        {
            return andParts.All(part => EvaluateExpression(part, context, upstreamFindings));
        }

        if (trimmed.StartsWith('!'))
        {
            return !EvaluateExpression(trimmed[1..], context, upstreamFindings);
        }

        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(trimmed, "git.isPullRequest", StringComparison.OrdinalIgnoreCase)) return context.IsPullRequestMode;

        // 1. files.changedMatches('glob')
        var globMatch = ChangedMatchesRegex().Match(trimmed);
        if (globMatch.Success)
        {
            var glob = globMatch.Groups["glob"].Value;
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(glob);

            var allFiles = (context.SourceFiles ?? [])
                .Concat(context.SpecFiles ?? [])
                .Concat(context.TestFiles ?? []);

            return allFiles.Any(f => matcher.Match(f.RelativePath.Replace('\\', '/')).HasMatches);
        }

        // 2. inputs.findingsCount(severity >= 'High') > 0
        var sevMatch = FindingsSeverityComparisonRegex().Match(trimmed);
        if (sevMatch.Success)
        {
            var sevOp = sevMatch.Groups["op"].Value;
            var targetSevRank = SeverityRank(sevMatch.Groups["sev"].Value);
            var cmpOp = sevMatch.Groups["cmpOp"].Value;
            var rhs = double.Parse(sevMatch.Groups["rhs"].Value, CultureInfo.InvariantCulture);

            var findings = upstreamFindings ?? context.UpstreamFindings ?? [];
            var count = findings.Count(f =>
            {
                var rank = SeverityRank(f.Severity);
                return sevOp switch
                {
                    ">=" => rank >= targetSevRank,
                    ">" => rank > targetSevRank,
                    "==" => rank == targetSevRank,
                    _ => false
                };
            });

            return CompareNumbers(count, cmpOp, rhs);
        }

        // 3. Standard LHS <op> RHS comparison
        var cmpMatch = NumericOrBoolComparisonRegex().Match(trimmed);
        if (cmpMatch.Success)
        {
            var lhsName = cmpMatch.Groups["lhs"].Value;
            var op = cmpMatch.Groups["op"].Value;
            var rhsRaw = cmpMatch.Groups["rhs"].Value;

            var lhsValue = ResolveMetricValue(lhsName, context, upstreamFindings);
            if (bool.TryParse(rhsRaw, out var rhsBool))
            {
                var lhsBool = Math.Abs(lhsValue) > 0.0001;
                return op switch
                {
                    "==" => lhsBool == rhsBool,
                    "!=" => lhsBool != rhsBool,
                    _ => false
                };
            }

            if (double.TryParse(rhsRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var rhsNum))
            {
                return CompareNumbers(lhsValue, op, rhsNum);
            }
        }

        return false;
    }

    public static SubagentGatingDecision? EvaluateDeterministicRuleSet(
        string agentName,
        DeterministicGateRuleSet? ruleSet,
        RepoContext context,
        IReadOnlyList<FindingCandidate>? upstreamFindings = null)
    {
        if (ruleSet == null)
        {
            return null;
        }

        foreach (var skipExpr in ruleSet.SkipIf.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            if (EvaluateExpression(skipExpr, context, upstreamFindings))
            {
                return new SubagentGatingDecision(
                    agentName,
                    ShouldRun: false,
                    Confidence: 0.0,
                    Reason: $"Skipped by deterministic rule: `{skipExpr}`.",
                    IsDeterministicOverride: true);
            }
        }

        if (ruleSet.AllOf.Count > 0)
        {
            var allMet = ruleSet.AllOf.All(e => EvaluateExpression(e, context, upstreamFindings));
            if (allMet)
            {
                return new SubagentGatingDecision(
                    agentName,
                    ShouldRun: true,
                    Confidence: 1.0,
                    Reason: $"Activated by deterministic allOf rules ({ruleSet.AllOf.Count} matched).",
                    IsDeterministicOverride: true);
            }
        }

        foreach (var anyExpr in ruleSet.AnyOf.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            if (EvaluateExpression(anyExpr, context, upstreamFindings))
            {
                return new SubagentGatingDecision(
                    agentName,
                    ShouldRun: true,
                    Confidence: 1.0,
                    Reason: $"Activated by deterministic rule: `{anyExpr}`.",
                    IsDeterministicOverride: true);
            }
        }

        return null;
    }

    public static async Task<SubagentGatingDecision> EvaluateFastLlmGateAsync(
        string agentName,
        string question,
        RepoContext context,
        ILlmClient llmClient,
        string model,
        double minConfidence = 0.50,
        CancellationToken ct = default)
    {
        var signals = GatingSignals.FromRepoContext(context);
        var signalsJson = JsonSerializer.Serialize(signals);

        var prompt = $$"""
        Evaluate whether subagent '{{agentName}}' should run based on the following repository/PR signals:
        {{signalsJson}}

        Gating Question: {{question}}

        Respond strictly with a JSON object:
        {
          "shouldRun": true,
          "confidence": 0.85,
          "reason": "Brief 1-sentence reason"
        }
        """;

        try
        {
            var response = await llmClient.GenerateAsync(new LlmRequest
            {
                AgentName = $"{agentName}.LlmGate",
                Prompt = prompt,
                SystemPrompt = "You are a fast pre-execution gating router. Respond strictly in JSON.",
                Model = model,
                ExpectJson = true
            }, ct);

            using var doc = JsonDocument.Parse(ExtractJsonObject(response.Content));
            var root = doc.RootElement;
            var conf = root.TryGetProperty("confidence", out var cProp) && cProp.TryGetDouble(out var d) ? d : 0.75;
            var shouldRun = root.TryGetProperty("shouldRun", out var sProp) && sProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? sProp.GetBoolean() && conf >= minConfidence
                : conf >= minConfidence;
            var reason = root.TryGetProperty("reason", out var rProp) && rProp.ValueKind == JsonValueKind.String
                ? rProp.GetString() ?? "Evaluated by Fast-LLM gate."
                : $"Fast-LLM gate confidence {conf:P0}.";

            return new SubagentGatingDecision(agentName, shouldRun, conf, reason, IsDeterministicOverride: false);
        }
        catch
        {
            return new SubagentGatingDecision(
                agentName,
                ShouldRun: true,
                Confidence: 1.0,
                Reason: "Fast-LLM gate fallback: activated on error.",
                IsDeterministicOverride: false);
        }
    }

    private static double ResolveMetricValue(
        string metricName,
        RepoContext context,
        IReadOnlyList<FindingCandidate>? upstreamFindings)
    {
        var signals = GatingSignals.FromRepoContext(context);
        var findings = upstreamFindings ?? context.UpstreamFindings ?? [];

        return metricName.ToLowerInvariant() switch
        {
            "artifacts.sarif.alertcount" or "sarif.alertcount" =>
                (context.SarifFindings?.Count ?? 0) + (context.PipelineArtifacts?.AdditionalSarifAlertsCount ?? 0),
            "artifacts.testresults.failedcount" or "tests.failedcount" =>
                context.PipelineArtifacts?.TotalTestFailures ?? 0,
            "artifacts.testresults.totalcount" or "tests.totalcount" =>
                context.PipelineArtifacts?.TotalTestsExecuted ?? 0,
            "artifacts.coverage.linecoveragepercent" or "artifacts.coverage.linerate" or "coverage.linecoveragepercent" =>
                context.PipelineArtifacts?.LowestLineCoveragePercent ?? 100.0,
            "files.specfileschanged" or "files.specfilescount" =>
                signals.SpecFilesCount,
            "files.sourcefileschanged" or "files.sourcefilescount" or "files.changedcount" =>
                signals.SourceFilesCount,
            "files.testfileschanged" or "files.testfilescount" =>
                signals.TestFilesCount,
            "ast.maxcomplexity" or "ast.maxcyclomaticcomplexity" =>
                signals.MaxCyclomaticComplexity,
            "inputs.findingscount" =>
                findings.Count,
            "git.ispullrequest" =>
                context.IsPullRequestMode ? 1.0 : 0.0,
            _ => 0.0
        };
    }

    private static bool CompareNumbers(double lhs, string op, double rhs) =>
        op switch
        {
            ">" => lhs > rhs,
            ">=" => lhs >= rhs - 1e-9,
            "<" => lhs < rhs,
            "<=" => lhs <= rhs + 1e-9,
            "==" => Math.Abs(lhs - rhs) < 1e-6,
            "!=" => Math.Abs(lhs - rhs) >= 1e-6,
            _ => false
        };

    private static int SeverityRank(string? severity) =>
        (severity ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "critical" or "p0" => 4,
            "high" or "p1" => 3,
            "medium" or "p2" => 2,
            "low" or "p3" => 1,
            _ => 0
        };

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : "{}";
    }
}
