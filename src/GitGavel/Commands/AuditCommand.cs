using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Providers;
using GitGavel.System1.Abstractions;
using GitGavel.System1.Engines;
using GitGavel.System1.Models;
using GitGavel.Reporting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GitGavel.Commands;

public class AuditCommand : AsyncCommand<AuditCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-c|--config <PATH>")]
        [Description("Path to .gitgavel.yml configuration file.")]
        [DefaultValue(".gitgavel.yml")]
        public string ConfigPath { get; set; } = ".gitgavel.yml";

        [CommandOption("-d|--dir <PATH>")]
        [Description("Path to repository root directory to audit.")]
        [DefaultValue(".")]
        public string RepoDirectory { get; set; } = ".";

        [CommandOption("--dry-run")]
        [Description("Perform audit without writing to GitHub or modifying tickets.")]
        public bool DryRun { get; set; }

        [CommandOption("--mock")]
        [Description("Use simulated mock AI subagents for offline execution.")]
        public bool UseMock { get; set; }
    }

    public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        AnsiConsole.Write(
            new FigletText("GitGavel")
                .Color(Color.Cyan1));

        AnsiConsole.MarkupLine("[bold grey]Multi-Agent AI Repo Auditor & Backlog Sentinel[/]\n");

        var repoDir = Path.GetFullPath(settings.RepoDirectory);
        var (commitSha, branch) = GitMetadataHelper.GetGitMetadata(repoDir);
        var configPath = Path.IsPathRooted(settings.ConfigPath)
            ? settings.ConfigPath
            : Path.Combine(repoDir, settings.ConfigPath);

        GitGavelConfig config;
        if (File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Loaded configuration from: [cyan]{Markup.Escape(configPath)}[/]");
            config = await ConfigLoader.LoadFromFileAsync(configPath);
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]![/] Configuration file not found at [cyan]{Markup.Escape(configPath)}[/]. Using defaults.");
            config = new GitGavelConfig();
        }

        // 1. File & Context Scanning
        var scanner = new FileScanner();
        RepoContext repoContext;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Scanning repository files and specs...", async ctx =>
            {
                repoContext = await scanner.ScanAsync(repoDir, config);
            });

        // Re-obtain context for outside closure
        repoContext = await scanner.ScanAsync(repoDir, config);

        var scopeTable = new Table().Border(TableBorder.Rounded);
        scopeTable.AddColumn("[bold]Category[/]");
        scopeTable.AddColumn("[bold]Count[/]");
        scopeTable.AddColumn("[bold]Details[/]");

        scopeTable.AddRow("Specification Files", repoContext.SpecFiles.Count.ToString(), string.Join(", ", repoContext.SpecFiles.Select(f => f.RelativePath)));
        scopeTable.AddRow("Source Files", repoContext.SourceFiles.Count.ToString(), $"{repoContext.TotalLinesOfCode:N0} total LOC");
        scopeTable.AddRow("SARIF Findings", repoContext.SarifFindings.Count.ToString(), $"{repoContext.SarifFindings.Count} static security alerts loaded");

        AnsiConsole.Write(scopeTable);
        AnsiConsole.WriteLine();

        // 2. System 2 Discovery Execution
        ILlmClient llmClient = settings.UseMock
            ? new MockLlmClient()
            : new GeminiLlmClient();

        var orchestrator = new SubagentOrchestrator(llmClient);
        OrchestrationResult orchestrationResult = null!;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.BouncingBar)
            .StartAsync("Executing System 2 Generative Subagents (SpecDrift, Security, Refactoring)...", async ctx =>
            {
                orchestrationResult = await orchestrator.RunAllAsync(repoContext, config);
            });

        AnsiConsole.MarkupLine($"[green]✓[/] System 2 Discovery complete in [cyan]{orchestrationResult.TotalDurationMs}ms[/] (Tokens: [yellow]{orchestrationResult.TotalTokens}[/])\n");

        // Display Findings Table
        if (orchestrationResult.AllFindings.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No candidate issues detected across all subagents.[/]");
            return 0;
        }

        var findingsTable = new Table().Border(TableBorder.Rounded);
        findingsTable.AddColumn("[bold]#[/]");
        findingsTable.AddColumn("[bold]Category[/]");
        findingsTable.AddColumn("[bold]Severity[/]");
        findingsTable.AddColumn("[bold]Title[/]");
        findingsTable.AddColumn("[bold]Location[/]");
        findingsTable.AddColumn("[bold]Agent[/]");

        for (var i = 0; i < orchestrationResult.AllFindings.Count; i++)
        {
            var f = orchestrationResult.AllFindings[i];
            var sevColor = f.Severity switch
            {
                "Critical" => "red bold",
                "High" => "red",
                "Medium" => "yellow",
                _ => "grey"
            };

            findingsTable.AddRow(
                (i + 1).ToString(),
                f.Category,
                $"[{sevColor}]{f.Severity}[/]",
                Markup.Escape(f.Title),
                $"{Markup.Escape(f.FilePath)}:{f.StartLine}",
                f.AgentName
            );
        }

        AnsiConsole.Write(findingsTable);
        AnsiConsole.WriteLine();

        // 3. System 1 Jev Decision Engine (Triage Gatekeeper)
        ITriageEngine triageEngine = settings.UseMock || config.Triage.Engine == "llm-synthesizer"
            ? new MockTriageEngine()
            : new TypeSafeJevEngine();

        List<TriageDecision> triageDecisions = null!;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots12)
            .StartAsync("Executing System 1 Jev Decision Engine (Noul dedupe, Score calibration, Choice routing)...", async ctx =>
            {
                triageDecisions = await triageEngine.TriageAllAsync(orchestrationResult.AllFindings, [], config, cancellationToken);
            });

        AnsiConsole.MarkupLine("[bold cyan]⚖️ Jev Decision Engine Triage Results[/]\n");

        var triageTable = new Table().Border(TableBorder.Rounded);
        triageTable.AddColumn("[bold]#[/]");
        triageTable.AddColumn("[bold]Priority[/]");
        triageTable.AddColumn("[bold]Jev Score[/]");
        triageTable.AddColumn("[bold]Target Column[/]");
        triageTable.AddColumn("[bold]Action / Status[/]");
        triageTable.AddColumn("[bold]Title[/]");
        triageTable.AddColumn("[bold]Triage Reason[/]");

        for (var i = 0; i < triageDecisions.Count; i++)
        {
            var d = triageDecisions[i];
            var prioColor = d.PriorityLabel switch
            {
                "P0" => "bold red",
                "P1" => "red",
                "P2" => "yellow",
                _ => "grey"
            };

            var statusColor = d.Status switch
            {
                TriageStatus.AcceptedToBacklog => "green bold",
                TriageStatus.RoutedToHumanReview => "cyan",
                TriageStatus.SuppressedDuplicate => "yellow",
                _ => "grey"
            };

            triageTable.AddRow(
                (i + 1).ToString(),
                $"[{prioColor}]{d.PriorityLabel}[/]",
                $"{d.JevScore:F1}/5.0",
                $"[bold]{d.TargetColumn}[/]",
                $"[{statusColor}]{d.Status}[/]",
                Markup.Escape(d.Finding.Title),
                Markup.Escape(d.Reason)
            );
        }

        AnsiConsole.Write(triageTable);
        AnsiConsole.WriteLine();

        var acceptedCount = triageDecisions.Count(d => d.Status == TriageStatus.AcceptedToBacklog);
        var reviewCount = triageDecisions.Count(d => d.Status == TriageStatus.RoutedToHumanReview);
        var dupCount = triageDecisions.Count(d => d.Status == TriageStatus.SuppressedDuplicate);
        var noiseCount = triageDecisions.Count(d => d.Status == TriageStatus.DiscardedNoise);

        AnsiConsole.MarkupLine($"[bold]Triage Summary:[/] [green]{acceptedCount} Accepted to Backlog[/] | [cyan]{reviewCount} Routed to Review[/] | [yellow]{dupCount} Duplicates Suppressed[/] | [grey]{noiseCount} Discarded[/]\n");

        // 4. Developer Reporting Engine
        var reportCtx = new ReportContext
        {
            Config = config,
            RepoContext = repoContext,
            Orchestration = orchestrationResult,
            TriageDecisions = triageDecisions,
            StartTimeUtc = startTime,
            EndTimeUtc = DateTime.UtcNow,
            GitCommitSha = commitSha,
            GitBranch = branch
        };

        var reportingEngine = new ReportingEngine();
        var generatedReports = await reportingEngine.GenerateReportsAsync(reportCtx, cancellationToken);

        AnsiConsole.MarkupLine("[bold green]📁 Developer Reports Generated[/]\n");
        foreach (var reportFile in generatedReports)
        {
            var relPath = Path.GetRelativePath(repoDir, reportFile);
            AnsiConsole.MarkupLine($"  [green]✓[/] [cyan]{Markup.Escape(relPath)}[/]");
        }
        AnsiConsole.WriteLine();

        return 0;
    }
}
