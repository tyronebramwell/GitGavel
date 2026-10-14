using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using GitGavel.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GitGavel.Commands;

public class ValidateCommand : AsyncCommand<ValidateCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-c|--config <PATH>")]
        [Description("Path to .gitgavel.yml configuration file to validate.")]
        [DefaultValue(".gitgavel.yml")]
        public string ConfigPath { get; set; } = ".gitgavel.yml";
    }

    public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold]GitGavel Configuration & Environment Validation[/]\n");

        if (!File.Exists(settings.ConfigPath))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Configuration file not found at: [cyan]{Markup.Escape(settings.ConfigPath)}[/]");
            return 1;
        }

        GitGavelConfig config;
        try
        {
            config = await ConfigLoader.LoadFromFileAsync(settings.ConfigPath);
            AnsiConsole.MarkupLine($"[green]✓[/] Configuration syntax and thresholds are valid in [cyan]{Markup.Escape(settings.ConfigPath)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Configuration validation failed: {Markup.Escape(ex.Message)}");
            return 1;
        }

        var envTable = new Table().Border(TableBorder.Rounded);
        envTable.AddColumn("[bold]Variable[/]");
        envTable.AddColumn("[bold]Purpose[/]");
        envTable.AddColumn("[bold]Status[/]");

        CheckEnv(envTable, "AI_API_KEY", "Generative LLM subagents (Gemini/Claude)", false);
        CheckEnv(envTable, config.Triage.Jev.ApiKeyEnv, "Jev Decision Engine (Noul, Score, Choice)", config.Triage.Engine == "jev");
        CheckEnv(envTable, "GITHUB_TOKEN", "GitHub Issues and Projects v2 integration", true);

        AnsiConsole.Write(envTable);
        return 0;
    }

    private static void CheckEnv(Table table, string varName, string purpose, bool isRequired)
    {
        var val = Environment.GetEnvironmentVariable(varName);
        var isSet = !string.IsNullOrWhiteSpace(val);
        var status = isSet
            ? "[green]Configured[/]"
            : isRequired ? "[red]Missing (Required)[/]" : "[yellow]Not Set (Optional)[/]";

        table.AddRow(varName, purpose, status);
    }
}
