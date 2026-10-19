using System.Text;
using GitGavel.Configuration;

namespace GitGavel.Reporting.Reporters;

public abstract class BaseMarkdownReporter : IReporter
{
    public abstract string ReportName { get; }
    protected abstract string FileName { get; }
    protected abstract bool IsEnabled(ReportsSelection reports);
    protected abstract string GenerateReportMarkdown(ReportContext context);

    public virtual async Task<IEnumerable<string>> GenerateReportsAsync(ReportContext context, string outputDirectory, CancellationToken ct = default)
    {
        var repConfig = context.Config.Reporting;
        var isMarkdown = repConfig.Formats.Any(f => f.Equals("markdown", StringComparison.OrdinalIgnoreCase));

        if (!IsEnabled(repConfig.Reports) || !isMarkdown)
        {
            return [];
        }

        var content = GenerateReportMarkdown(context);
        var path = await WriteReportFileAsync(outputDirectory, FileName, content, ct);
        return [path];
    }

    protected static async Task<string> WriteReportFileAsync(string outputDirectory, string fileName, string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, fileName);
        await File.WriteAllTextAsync(path, content, ct);
        return path;
    }

    protected static void AppendReportMetadataHeader(StringBuilder sb, string title, ReportContext ctx)
    {
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"> Generated at **{ctx.EndTimeUtc:yyyy-MM-dd HH:mm:ss} UTC** | Commit: `{ctx.GitCommitSha}`{(string.IsNullOrWhiteSpace(ctx.GitBranch) ? "" : $" | Branch: `{ctx.GitBranch}`")}");
        sb.AppendLine();
    }

    protected static string EscapeCell(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
