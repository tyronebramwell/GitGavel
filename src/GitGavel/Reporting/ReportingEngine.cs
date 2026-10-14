using GitGavel.Reporting.Reporters;

namespace GitGavel.Reporting;

public class ReportingEngine
{
    public async Task<List<string>> GenerateReportsAsync(ReportContext ctx, CancellationToken ct = default)
    {
        var generatedFiles = new List<string>();
        var outputDir = Path.GetFullPath(ctx.Config.Reporting.OutputDir);
        Directory.CreateDirectory(outputDir);

        var repConfig = ctx.Config.Reporting;
        var isMarkdown = repConfig.Formats.Any(f => f.Equals("markdown", StringComparison.OrdinalIgnoreCase));
        var isJson = repConfig.Formats.Any(f => f.Equals("json", StringComparison.OrdinalIgnoreCase));

        // 1. Audit Run Log
        if (repConfig.Reports.AuditRun && isMarkdown)
        {
            var path = Path.Combine(outputDir, "1_audit-run.md");
            var content = AuditRunReporter.Generate(ctx);
            await File.WriteAllTextAsync(path, content, ct);
            generatedFiles.Add(path);
        }

        // 2. Tickets Report (Markdown & JSON)
        if (repConfig.Reports.Tickets)
        {
            if (isMarkdown)
            {
                var mdPath = Path.Combine(outputDir, "2_tickets-report.md");
                var mdContent = TicketsReporter.GenerateMarkdown(ctx);
                await File.WriteAllTextAsync(mdPath, mdContent, ct);
                generatedFiles.Add(mdPath);
            }

            if (isJson)
            {
                var jsonPath = Path.Combine(outputDir, "2_tickets-report.json");
                var jsonContent = TicketsReporter.GenerateJson(ctx);
                await File.WriteAllTextAsync(jsonPath, jsonContent, ct);
                generatedFiles.Add(jsonPath);
            }
        }

        // 3. Security Report
        if (repConfig.Reports.Security && isMarkdown)
        {
            var path = Path.Combine(outputDir, "3_security-report.md");
            var content = SecurityReporter.Generate(ctx);
            await File.WriteAllTextAsync(path, content, ct);
            generatedFiles.Add(path);
        }

        // 4. Code Quality & Spec Report
        if (repConfig.Reports.CodeQuality && isMarkdown)
        {
            var path = Path.Combine(outputDir, "4_code-quality-report.md");
            var content = CodeQualityReporter.Generate(ctx);
            await File.WriteAllTextAsync(path, content, ct);
            generatedFiles.Add(path);
        }

        // 5. GitHub Step Summary (if configured)
        if (repConfig.GithubStepSummary)
        {
            await StepSummaryWriter.WriteAsync(ctx, ct);
        }

        return generatedFiles;
    }
}
