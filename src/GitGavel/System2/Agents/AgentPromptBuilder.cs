using System.Text;
using GitGavel.Discovery;

namespace GitGavel.System2.Agents;

public static class AgentPromptBuilder
{
    public const int MaxSpecLength = 32000;
    public const int MaxSourceLength = 8000;
    public const int MaxDiffPatchLength = 4000;
    public const int MaxIssueBodyLength = 1500;

    public static string Truncate(string? content, int maxLength)
    {
        if (string.IsNullOrEmpty(content))
        {
            return "";
        }

        return content.Length > maxLength
            ? content[..maxLength] + "\n...(truncated)"
            : content;
    }

    public static void AppendPullRequestGuidance(
        StringBuilder sb,
        RepoContext context,
        string antiScopeCreepRule)
    {
        if (context.IsPullRequestMode && context.ActivePullRequest != null)
        {
            sb.AppendLine($"PULL REQUEST REVIEW MODE (PR #{context.ActivePullRequest.PrNumber}: {context.ActivePullRequest.Title}): Audit ONLY the changes introduced in this Pull Request's modified files and diff patches.");
            sb.AppendLine(antiScopeCreepRule);
        }
    }

    public static IReadOnlyList<FileAstSummary> ResolveAstSummaries(RepoContext context)
    {
        return context.AstSummaries != null && context.AstSummaries.Count > 0
            ? context.AstSummaries
            : AstAnalyzer.AnalyzeAll(context.SourceFiles ?? []);
    }

    public static void AppendSymbolMap(
        StringBuilder sb,
        IReadOnlyList<FileAstSummary> summaries,
        bool includeComplexity = false,
        string heading = "### Repository Architecture & Symbol Map:")
    {
        sb.AppendLine();
        sb.AppendLine(heading);
        foreach (var sum in summaries)
        {
            var symbolList = sum.Symbols.Count > 0
                ? string.Join(", ", sum.Symbols.Select(s => $"{s.Kind} {s.Name}"))
                : "None";

            if (includeComplexity)
            {
                sb.AppendLine($"- File: `{sum.RelativePath}` | Symbols: {symbolList} | Complexity: {sum.TotalComplexityScore}");
            }
            else
            {
                sb.AppendLine($"- File: `{sum.RelativePath}` | Symbols: {symbolList}");
            }
        }
    }

    public static void AppendFilesSection(
        StringBuilder sb,
        IEnumerable<ScannedFile> files,
        string heading,
        string fileLabelPrefix = "File",
        bool omitContentWhenDiffPresent = false)
    {
        sb.AppendLine();
        sb.AppendLine(heading);
        foreach (var file in files)
        {
            sb.AppendLine($"--- {fileLabelPrefix}: {file.RelativePath} ---");
            if (!string.IsNullOrWhiteSpace(file.DiffPatch))
            {
                sb.AppendLine("PR Diff Patch:");
                sb.AppendLine(Truncate(file.DiffPatch, MaxDiffPatchLength));
                if (omitContentWhenDiffPresent)
                {
                    continue;
                }
            }

            sb.AppendLine(Truncate(file.Content, MaxSourceLength));
        }
    }
}
