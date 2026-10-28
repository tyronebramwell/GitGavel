using System.Text.Json;

namespace GitGavel.System2;

public static class FindingReferenceEnricher
{
    public static List<string> ExtractAndEnrichReferences(
        JsonElement item,
        string category,
        string? owaspCategory,
        string title)
    {
        var references = ParseExplicitReferences(item);
        if (references.Count == 0)
        {
            references = BuildDefaultReferences(category, owaspCategory, title);
        }

        return references;
    }

    public static List<string> ParseExplicitReferences(JsonElement item)
    {
        var refs = new List<string>();

        if (!TryGetPropertyCaseInsensitive(item, "references", out var refsElement) ||
            refsElement.ValueKind != JsonValueKind.Array)
        {
            return refs;
        }

        foreach (var refItem in refsElement.EnumerateArray())
        {
            if (refItem.ValueKind == JsonValueKind.String)
            {
                var str = refItem.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    refs.Add(FormatReferenceEntry(str));
                }
            }
            else if (refItem.ValueKind == JsonValueKind.Object)
            {
                var refTitle = GetString(refItem, "title") ?? GetString(refItem, "name") ?? "Reference";
                var refUrl = GetString(refItem, "url") ?? GetString(refItem, "link") ?? "";
                if (!string.IsNullOrWhiteSpace(refUrl))
                {
                    refs.Add($"[{refTitle}]({refUrl})");
                }
                else if (!string.IsNullOrWhiteSpace(refTitle))
                {
                    refs.Add(refTitle);
                }
            }
        }

        return refs;
    }

    public static string FormatReferenceEntry(string entry)
    {
        if (entry.Contains("](") && entry.StartsWith('['))
        {
            return entry;
        }

        if (entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var label = InferDomainLabel(entry);
            return $"[{label}]({entry})";
        }

        return entry;
    }

    public static string InferDomainLabel(string url)
    {
        if (url.Contains("stackoverflow.com", StringComparison.OrdinalIgnoreCase))
            return "Stack Overflow Discussion";
        if (url.Contains("owasp.org", StringComparison.OrdinalIgnoreCase))
            return "OWASP Security Guidance";
        if (url.Contains("learn.microsoft.com", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Learn Documentation";
        if (url.Contains("cwe.mitre.org", StringComparison.OrdinalIgnoreCase))
            return "MITRE CWE Entry";
        if (url.Contains("refactoring.guru", StringComparison.OrdinalIgnoreCase))
            return "Refactoring.Guru Catalog";
        if (url.Contains("martinfowler.com", StringComparison.OrdinalIgnoreCase))
            return "Martin Fowler Architecture Guide";

        return "External Reference";
    }

    public static List<string> BuildDefaultReferences(string category, string? owaspCategory, string title)
    {
        if (category.Equals("Security", StringComparison.OrdinalIgnoreCase))
        {
            return BuildDefaultSecurityReferences(owaspCategory);
        }

        if (category.Equals("Reliability", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "[Microsoft Learn: .NET Reliability and Exception Handling Best Practices](https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions)",
                "[Microsoft Learn: Performance and Memory Optimization in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/high-performance-logging)",
                "[Stack Overflow: Common Async/Await and Threading Pitfalls in C#](https://stackoverflow.com/questions/tagged/c%23+async-await+multithreading)"
            ];
        }

        if (category.Equals("Refactoring", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "[Refactoring.Guru: Code Smells & Refactoring Catalog](https://refactoring.guru/refactoring/smells)",
                "[Microsoft Learn: Code Analysis & Maintainability Rules](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/maintainability-warnings)",
                "[Stack Overflow: Reducing Cyclomatic Complexity in C#](https://stackoverflow.com/questions/tagged/c%23+refactoring+cyclomatic-complexity)"
            ];
        }

        if (category.Equals("Testing", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "[Microsoft Learn: Unit Testing Best Practices with xUnit and .NET](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)",
                "[Martin Fowler: The Practical Test Pyramid](https://martinfowler.com/articles/practical-test-pyramid.html)",
                "[Stack Overflow: Mocking & Integration Testing Strategies in C#](https://stackoverflow.com/questions/tagged/c%23+xunit+integration-testing)"
            ];
        }

        return
        [
            "[Microsoft Learn: .NET Architecture & API Design Guidelines](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/)",
            "[Stack Overflow: API Contract & Specification Alignment](https://stackoverflow.com/questions/tagged/c%23+api-design)"
        ];
    }

    private static List<string> BuildDefaultSecurityReferences(string? owaspCategory)
    {
        var refs = new List<string>();
        var owasp = owaspCategory ?? "";

        if (owasp.Contains("A01", StringComparison.OrdinalIgnoreCase) || owasp.Contains("Access Control", StringComparison.OrdinalIgnoreCase))
        {
            refs.Add("[OWASP Top 10 A01:2021 – Broken Access Control](https://owasp.org/Top10/A01_2021-Broken_Access_Control/)");
            refs.Add("[OWASP Authorization Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html)");
        }
        else if (owasp.Contains("A02", StringComparison.OrdinalIgnoreCase) || owasp.Contains("Cryptographic", StringComparison.OrdinalIgnoreCase))
        {
            refs.Add("[OWASP Top 10 A02:2021 – Cryptographic Failures](https://owasp.org/Top10/A02_2021-Cryptographic_Failures/)");
        }
        else if (owasp.Contains("A03", StringComparison.OrdinalIgnoreCase) || owasp.Contains("Injection", StringComparison.OrdinalIgnoreCase))
        {
            refs.Add("[OWASP Top 10 A03:2021 – Injection](https://owasp.org/Top10/A03_2021-Injection/)");
            refs.Add("[OWASP Injection Prevention Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Injection_Prevention_Cheat_Sheet.html)");
        }
        else if (owasp.Contains("A07", StringComparison.OrdinalIgnoreCase) || owasp.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
        {
            refs.Add("[OWASP Top 10 A07:2021 – Identification and Authentication Failures](https://owasp.org/Top10/A07_2021-Identification_and_Authentication_Failures/)");
            refs.Add("[OWASP Secrets Management Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html)");
        }
        else
        {
            refs.Add("[OWASP Top 10:2021 Security Risks](https://owasp.org/Top10/)");
            refs.Add("[OWASP DotNet Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/DotNet_Security_Cheat_Sheet.html)");
        }

        refs.Add("[Stack Overflow: .NET Security Best Practices](https://stackoverflow.com/questions/tagged/.net+security)");
        return refs;
    }

    internal static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement prop)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out prop))
            {
                return true;
            }

            foreach (var p in element.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    prop = p.Value;
                    return true;
                }
            }
        }

        prop = default;
        return false;
    }

    internal static string? GetString(JsonElement element, string propertyName)
    {
        if (TryGetPropertyCaseInsensitive(element, propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }
}
