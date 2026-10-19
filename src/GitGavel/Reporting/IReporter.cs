namespace GitGavel.Reporting;

public interface IReporter
{
    string ReportName { get; }
    Task<IEnumerable<string>> GenerateReportsAsync(ReportContext context, string outputDirectory, CancellationToken ct = default);
}
