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
    // Regex heuristics for class/interface/record/struct declarations
    [GeneratedRegex(@"\b(class|interface|record|struct|enum)\s+([A-Za-z0-9_]+)", RegexOptions.Compiled)]
    private static partial Regex TypeDeclarationRegex();

    // Regex heuristics for method declarations
    [GeneratedRegex(@"(public|protected|internal|private)\s+(?:static\s+|async\s+|virtual\s+|override\s+)*[A-Za-z0-9_<>,\[\]\?]+\s+([A-Za-z0-9_]+)\s*\((.*?)\)", RegexOptions.Compiled)]
    private static partial Regex MethodDeclarationRegex();

    // Branching keywords contributing to cyclomatic complexity
    [GeneratedRegex(@"\b(if|else if|switch|case|while|for|foreach|catch|&&|\|\||\?\?)\b", RegexOptions.Compiled)]
    private static partial Regex BranchingRegex();

    public static FileAstSummary Analyze(ScannedFile file)
    {
        var symbols = new List<CodeSymbol>();
        var lines = file.Content.Split('\n');
        var totalComplexity = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith("*"))
            {
                continue;
            }

            var typeMatch = TypeDeclarationRegex().Match(line);
            if (typeMatch.Success)
            {
                var kind = typeMatch.Groups[1].Value;
                var name = typeMatch.Groups[2].Value;
                symbols.Add(new CodeSymbol(kind, name, i + 1, 1));
                continue;
            }

            var methodMatch = MethodDeclarationRegex().Match(line);
            if (methodMatch.Success)
            {
                var name = methodMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol("method", name, i + 1, 1));
            }
        }

        var branches = BranchingRegex().Matches(file.Content);
        totalComplexity = branches.Count;

        // A file is considered a hotspot if it has high branch density
        var isHotspot = totalComplexity > 25 || (file.LineCount > 0 && (double)totalComplexity / file.LineCount > 0.15);

        return new FileAstSummary(
            RelativePath: file.RelativePath,
            Symbols: symbols,
            TotalComplexityScore: totalComplexity,
            IsComplexityHotspot: isHotspot);
    }

    public static List<FileAstSummary> AnalyzeAll(IEnumerable<ScannedFile> files)
    {
        return files.Select(Analyze).ToList();
    }
}
