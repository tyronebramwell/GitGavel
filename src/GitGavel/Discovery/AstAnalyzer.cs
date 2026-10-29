using System.Text.RegularExpressions;

namespace GitGavel.Discovery;

public record CodeSymbol(
    string Kind,
    string Name,
    int LineNumber,
    int EstimatedComplexity);

public record FileAstSummary(
    string RelativePath,
    List<CodeSymbol> Symbols,
    int TotalComplexityScore,
    bool IsComplexityHotspot);

public static partial class AstAnalyzer
{
    // Regex heuristics for class/interface/record/struct/enum declarations (including record class / record struct and generics)
    [GeneratedRegex(@"\b(record\s+struct|record\s+class|class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled)]
    private static partial Regex TypeDeclarationRegex();

    // Regex heuristics for method declarations (supporting attributes, generics, tuples, nullable return types, etc.)
    [GeneratedRegex(@"\b(?:public|protected|internal|private)\s+(?:(?:static|async|virtual|override|abstract|sealed|partial|new|unsafe|extern)\s+)*[A-Za-z0-9_<>,\[\]\?\.\(\)\s]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]+>)?\s*\(", RegexOptions.Compiled)]
    private static partial Regex MethodDeclarationRegex();

    // Branching keywords and operators contributing to cyclomatic complexity
    [GeneratedRegex(@"(\b(?:if|else\s+if|switch|case|while|for|foreach|catch)\b|&&|\|\||\?\?)", RegexOptions.Compiled)]
    private static partial Regex BranchingRegex();

    // Strips block comments, single-line comments, verbatim strings, interpolated/standard string literals, and char literals
    [GeneratedRegex(@"(/\*[\s\S]*?\*/)|(//[^\r\n]*)|(@""(?:""""|[^""])*"")|(""(?:\\.|[^\\""])*"")|('(?:\\.|[^\\'])*')", RegexOptions.Compiled)]
    private static partial Regex CommentAndStringStripRegex();

    private static readonly HashSet<string> NonMethodNames = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "fixed", "when", "nameof", "typeof", "sizeof"
    };

    public static FileAstSummary Analyze(ScannedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var symbols = new List<CodeSymbol>();
        var content = file.Content ?? string.Empty;

        using (var reader = new StringReader(content))
        {
            string? rawLine;
            var lineNumber = 0;
            var inBlockComment = false;

            while ((rawLine = reader.ReadLine()) != null)
            {
                lineNumber++;
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (inBlockComment)
                {
                    var endIdx = line.IndexOf("*/", StringComparison.Ordinal);
                    if (endIdx < 0) continue;
                    inBlockComment = false;
                    line = line[(endIdx + 2)..].Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                }

                if (line.StartsWith("/*", StringComparison.Ordinal))
                {
                    if (!line.Contains("*/", StringComparison.Ordinal))
                    {
                        inBlockComment = true;
                    }
                    continue;
                }

                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*'))
                {
                    continue;
                }

                var typeMatch = TypeDeclarationRegex().Match(line);
                if (typeMatch.Success)
                {
                    var rawKind = typeMatch.Groups[1].Value;
                    var kind = rawKind.StartsWith("record", StringComparison.Ordinal) ? "record" : rawKind;
                    var name = typeMatch.Groups[2].Value;
                    symbols.Add(new CodeSymbol(kind, name, lineNumber, 1));
                    continue;
                }

                var methodMatch = MethodDeclarationRegex().Match(line);
                if (methodMatch.Success)
                {
                    var name = methodMatch.Groups[1].Value;
                    if (!NonMethodNames.Contains(name))
                    {
                        symbols.Add(new CodeSymbol("method", name, lineNumber, 1));
                    }
                }
            }
        }

        var sanitizedContent = StripCommentsAndStrings(content);
        var branches = BranchingRegex().Matches(sanitizedContent);
        var totalComplexity = branches.Count;

        // A file is considered a hotspot if it has high branch density
        var isHotspot = totalComplexity > 25 || (file.LineCount > 0 && (double)totalComplexity / file.LineCount > 0.15);

        return new FileAstSummary(
            RelativePath: file.RelativePath,
            Symbols: symbols,
            TotalComplexityScore: totalComplexity,
            IsComplexityHotspot: isHotspot);
    }

    public static string StripCommentsAndStrings(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        return CommentAndStringStripRegex().Replace(content, " ");
    }

    public static List<FileAstSummary> AnalyzeAll(IEnumerable<ScannedFile> files)
    {
        return (files ?? []).Select(Analyze).ToList();
    }
}
