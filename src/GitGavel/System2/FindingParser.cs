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
        var testTier = GetString(elem, "testTier") ?? GetString(elem, "tier");
        var testDefectType = GetString(elem, "testDefectType");
        var defectType = GetString(elem, "defectType");

        var startLine = GetInt(elem, "startLine", 1);
        var endLine = GetInt(elem, "endLine", startLine);

        var rawReferences = ParseReferences(elem);
        var references = EnsureDefaultReferences(rawReferences, category, owasp, title);

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
            TestTier = testTier,
            TestDefectType = testDefectType,
            DefectType = defectType,
            Details = details,
            References = references
        };
    }

    public static List<IssueResolutionCandidate> ParseIssueResolutions(string rawText)
    {
        var list = new List<IssueResolutionCandidate>();
        if (string.IsNullOrWhiteSpace(rawText)) return list;

        var jsonToParse = ExtractJson(rawText);

        try
        {
            using var doc = JsonDocument.Parse(jsonToParse);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var res = ParseSingleIssueResolution(item);
                    if (res != null) list.Add(res);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("resolutions", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var res = ParseSingleIssueResolution(item);
                        if (res != null) list.Add(res);
                    }
                }
                else
                {
                    var res = ParseSingleIssueResolution(root);
                    if (res != null) list.Add(res);
                }
            }
        }
        catch (JsonException)
        {
            // Fallback on malformed JSON
        }

        return list;
    }

    private static IssueResolutionCandidate? ParseSingleIssueResolution(JsonElement elem)
    {
        if (elem.ValueKind != JsonValueKind.Object) return null;

        var issueNumber = GetInt(elem, "issueNumber", GetInt(elem, "number", 0));
        if (issueNumber <= 0) return null;

        var title = GetString(elem, "issueTitle") ?? GetString(elem, "title") ?? $"Issue #{issueNumber}";
        var isResolved = true;
        if (elem.TryGetProperty("isResolved", out var irProp) && (irProp.ValueKind == JsonValueKind.True || irProp.ValueKind == JsonValueKind.False))
        {
            isResolved = irProp.GetBoolean();
        }

        var howFixed = GetString(elem, "howItWasFixed") ?? GetString(elem, "explanation") ?? GetString(elem, "summary") ?? "Resolved in recent commits.";

        var commitShas = new List<string>();
        if (elem.TryGetProperty("resolvingCommitShas", out var shasProp) && shasProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in shasProp.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                {
                    commitShas.Add(s.GetString()!);
                }
            }
        }

        var commitSummaries = new List<string>();
        if (elem.TryGetProperty("resolvingCommitSummaries", out var sumProp) && sumProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sumProp.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                {
                    commitSummaries.Add(s.GetString()!);
                }
            }
        }

        return new IssueResolutionCandidate
        {
            IssueNumber = issueNumber,
            IssueTitle = title,
            IsResolved = isResolved,
            HowItWasFixed = howFixed,
            ResolvingCommitShas = commitShas,
            ResolvingCommitSummaries = commitSummaries
        };
    }

    private static string ExtractJson(string rawText)
    {
        var trimmed = rawText.Trim();
        var match = MarkdownJsonFence().Match(trimmed);
        return match.Success ? match.Groups[1].Value.Trim() : trimmed;
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

    private static List<string> ParseReferences(JsonElement elem)
    {
        var list = new List<string>();
        if (!elem.TryGetProperty("references", out var prop) &&
            !elem.TryGetProperty("learningReferences", out prop) &&
            !elem.TryGetProperty("links", out prop))
        {
            return list;
        }

        if (prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var str = item.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        list.Add(FormatReferenceLink(str.Trim()));
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var title = GetString(item, "title") ?? GetString(item, "name") ?? "Documentation Reference";
                    var url = GetString(item, "url") ?? GetString(item, "link") ?? GetString(item, "href");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        list.Add($"[{title}]({url})");
                    }
                }
            }
        }
        else if (prop.ValueKind == JsonValueKind.String)
        {
            var str = prop.GetString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                list.Add(FormatReferenceLink(str.Trim()));
            }
        }

        return list;
    }

    private static string FormatReferenceLink(string input)
    {
        if (input.StartsWith('[') && input.Contains("](") && input.EndsWith(')'))
        {
            return input;
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Contains("stackoverflow.com", StringComparison.OrdinalIgnoreCase))
            {
                return $"[Stack Overflow Discussion]({input})";
            }
            if (uri.Host.Contains("microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                return $"[Microsoft Learn Documentation]({input})";
            }
            if (uri.Host.Contains("owasp.org", StringComparison.OrdinalIgnoreCase))
            {
                return $"[OWASP Security Guidance]({input})";
            }
            if (uri.Host.Contains("refactoring.guru", StringComparison.OrdinalIgnoreCase))
            {
                return $"[Refactoring Guru Guide]({input})";
            }
            return $"[Reference Documentation]({input})";
        }

        return input;
    }

    private static List<string> EnsureDefaultReferences(List<string> references, string category, string? owaspCategory, string title)
    {
        if (references.Count > 0) return references;

        var defaults = new List<string>();

        if (category.Equals("Security", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(owaspCategory) && owaspCategory.StartsWith("A01", StringComparison.OrdinalIgnoreCase))
            {
                defaults.Add("[OWASP Top 10 - Broken Access Control](https://owasp.org/Top10/A01_2021-Broken_Access_Control/)");
            }
            else if (!string.IsNullOrWhiteSpace(owaspCategory) && (owaspCategory.StartsWith("A02", StringComparison.OrdinalIgnoreCase) || owaspCategory.Contains("Crypto", StringComparison.OrdinalIgnoreCase)))
            {
                defaults.Add("[OWASP Top 10 - Cryptographic Failures](https://owasp.org/Top10/A02_2021-Cryptographic_Failures/)");
            }
            else if (!string.IsNullOrWhiteSpace(owaspCategory) && (owaspCategory.StartsWith("A03", StringComparison.OrdinalIgnoreCase) || owaspCategory.Contains("Injection", StringComparison.OrdinalIgnoreCase)))
            {
                defaults.Add("[OWASP Top 10 - Injection](https://owasp.org/Top10/A03_2021-Injection/)");
            }
            else if (!string.IsNullOrWhiteSpace(owaspCategory) && owaspCategory.StartsWith("A07", StringComparison.OrdinalIgnoreCase))
            {
                defaults.Add("[OWASP Top 10 - Identification and Authentication Failures](https://owasp.org/Top10/A07_2021-Identification_and_Authentication_Failures/)");
                defaults.Add("[Microsoft Learn: Safe storage of app secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)");
            }
            else
            {
                defaults.Add("[OWASP Top 10 Security Risks Overview](https://owasp.org/www-project-top-ten/)");
            }

            defaults.Add("[Microsoft Learn: Secure Coding Guidelines](https://learn.microsoft.com/en-us/dotnet/standard/security/secure-coding-guidelines)");
            defaults.Add("[Stack Overflow: Secure development practices](https://stackoverflow.com/questions/tagged/security)");
        }
        else if (category.Equals("SpecDrift", StringComparison.OrdinalIgnoreCase))
        {
            defaults.Add("[Microsoft Learn: RESTful Web API Design Best Practices](https://learn.microsoft.com/en-us/azure/architecture/best-practices/api-design)");
            defaults.Add("[OpenAPI Specification Standard](https://spec.openapis.org/oas/latest.html)");
            defaults.Add("[Stack Overflow: API specification and contract testing](https://stackoverflow.com/questions/tagged/contract-testing)");
        }
        else if (category.Equals("Refactoring", StringComparison.OrdinalIgnoreCase) || category.Contains("Debt", StringComparison.OrdinalIgnoreCase) || category.Contains("Coupling", StringComparison.OrdinalIgnoreCase))
        {
            defaults.Add("[Refactoring Guru: Code Smells and Refactoring Techniques](https://refactoring.guru/refactoring)");
            defaults.Add("[Microsoft Learn: Common Web Application Architectures](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/common-web-application-architectures)");
            defaults.Add("[Stack Overflow: Software design patterns and clean architecture](https://stackoverflow.com/questions/tagged/design-patterns)");
        }
        else if (category.Equals("Testing", StringComparison.OrdinalIgnoreCase) || category.Contains("Test", StringComparison.OrdinalIgnoreCase))
        {
            defaults.Add("[Microsoft Learn: Unit testing best practices with .NET Core and .NET Standard](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)");
            defaults.Add("[Martin Fowler: Practical Test Pyramid](https://martinfowler.com/articles/practical-test-pyramid.html)");
            defaults.Add("[Stack Overflow: Automated test design patterns and assertions](https://stackoverflow.com/questions/tagged/unit-testing)");
        }
        else if (category.Equals("Reliability", StringComparison.OrdinalIgnoreCase) || category.Contains("Bug", StringComparison.OrdinalIgnoreCase) || category.Contains("Performance", StringComparison.OrdinalIgnoreCase))
        {
            defaults.Add("[Microsoft Learn: Reliability and Exception Handling Best Practices](https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions)");
            defaults.Add("[Microsoft Learn: Async/Await Best Practices and Concurrency](https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)");
            defaults.Add("[Stack Overflow: Performance optimization and resource management in .NET](https://stackoverflow.com/questions/tagged/performance)");
        }

        return defaults;
    }
}
