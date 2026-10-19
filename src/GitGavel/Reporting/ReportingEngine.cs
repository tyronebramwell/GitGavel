using GitGavel.Reporting.Reporters;

namespace GitGavel.Reporting;

public class ReportingEngine
{
    private readonly List<IReporter> _reporters;

    public ReportingEngine(IEnumerable<IReporter>? reporters = null)
    {
        _reporters = (reporters ?? CreateDefaultReporters()).ToList();
    }

    public static IReadOnlyList<IReporter> CreateDefaultReporters() =>
    [
        new AuditRunReporter(),
        new TicketsReporter(),
        new SecurityReporter(),
        new CodeQualityReporter()
    ];

    public async Task<List<string>> GenerateReportsAsync(ReportContext ctx, CancellationToken ct = default)
    {
        var generatedFiles = new List<string>();
        var outputDir = Path.GetFullPath(ctx.Config.Reporting.OutputDir);
        Directory.CreateDirectory(outputDir);

        foreach (var reporter in _reporters)
        {
            var reports = await reporter.GenerateReportsAsync(ctx, outputDir, ct);
            generatedFiles.AddRange(reports);
        }

        // 5. GitHub Step Summary if running in GitHub Actions
        if (ctx.Config.Reporting.GithubStepSummary)
        {
            await StepSummaryWriter.WriteAsync(ctx, ct: ct);
        }

        return generatedFiles;
    }
}
