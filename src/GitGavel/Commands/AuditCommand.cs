using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Providers;
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
        AnsiConsole.Write(
            new FigletText("GitGavel")
                .Color(Color.Cyan1));

        AnsiConsole.MarkupLine("[bold grey]Multi-Agent AI Repo Auditor & Backlog Sentinel[/]\n");

        var repoDir = Path.GetFullPath(settings.RepoDirectory);
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

        return 0;
    }
}
