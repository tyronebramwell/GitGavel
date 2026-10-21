using GitGavel.GitHub;
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
        new CodeQualityReporter(),
        new TestQualityReporter()
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

    public async Task<IReadOnlyList<string>> PublishPullRequestDomainReportsAsync(
        ReportContext ctx,
        IGitHubService gitHubService,
        string owner,
        string repo,
        bool dryRun = false,
        bool useMock = false,
        CancellationToken ct = default)
    {
        if (!ctx.ActivePullRequestNumber.HasValue || dryRun || (!useMock && owner == "local-owner"))
        {
            return [];
        }

        var prNumber = ctx.ActivePullRequestNumber.Value;
        var postedDomains = new List<string>();
        var discardedSet = ctx.PrCommentDecisions
            .Where(d => d.Action == GitGavel.System1.Models.PrCommentAction.Discard)
            .Select(d => d.Finding)
            .ToHashSet();

        var activeFindings = ctx.Orchestration.AllFindings
            .Where(f => !discardedSet.Contains(f))
            .ToList();

        var filteredCtx = ctx with
        {
            Orchestration = ctx.Orchestration with { AllFindings = activeFindings }
        };

        if (activeFindings.Any(f => f.Category.Equals("Security", StringComparison.OrdinalIgnoreCase)))
        {
            var secBody = SecurityReporter.Generate(filteredCtx) + "\n<!-- gitgavel-sticky-comment: security-report -->\n";
            await gitHubService.PostPullRequestCommentAsync(owner, repo, prNumber, secBody, ct);
            postedDomains.Add("Security");
        }

        if (activeFindings.Any(f => f.Category.Equals("SpecDrift", StringComparison.OrdinalIgnoreCase) ||
                                    f.Category.Equals("Refactoring", StringComparison.OrdinalIgnoreCase)))
        {
            var qualBody = CodeQualityReporter.Generate(filteredCtx) + "\n<!-- gitgavel-sticky-comment: code-quality-report -->\n";
            await gitHubService.PostPullRequestCommentAsync(owner, repo, prNumber, qualBody, ct);
            postedDomains.Add("CodeQuality");
        }

        if (activeFindings.Any(f => f.Category.Equals("Testing", StringComparison.OrdinalIgnoreCase) ||
                                    f.AgentName.Contains("TestSentinel", StringComparison.OrdinalIgnoreCase)))
        {
            var testBody = TestQualityReporter.Generate(filteredCtx) + "\n<!-- gitgavel-sticky-comment: test-quality-report -->\n";
            await gitHubService.PostPullRequestCommentAsync(owner, repo, prNumber, testBody, ct);
            postedDomains.Add("TestQuality");
        }

        return postedDomains;
    }
}
