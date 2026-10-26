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

        [CommandOption("--allow-external-paths")]
        [Description("Explicitly allow configuration or reporting paths outside the current directory.")]
        public bool AllowExternalPaths { get; set; }
    }

    public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold]GitGavel Configuration & Environment Validation[/]\n");

        string configPath;
        try
        {
            configPath = PathSecurity.ResolveAndVerifyWithinRoot(settings.ConfigPath, Directory.GetCurrentDirectory(), settings.AllowExternalPaths);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Configuration path security validation failed: {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Configuration file not found at: [cyan]{Markup.Escape(configPath)}[/]");
            return 1;
        }

        GitGavelConfig config;
        try
        {
            config = await ConfigLoader.LoadFromFileAsync(configPath, settings.AllowExternalPaths, cancellationToken);
            AnsiConsole.MarkupLine($"[green]✓[/] Configuration syntax, scope globs, and thresholds are valid in [cyan]{Markup.Escape(configPath)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Configuration validation failed: {Markup.Escape(ex.Message)}");
            return 1;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(configPath));
        DotEnvLoader.Load(dir, settings.AllowExternalPaths);

        var envTable = new Table().Border(TableBorder.Rounded);
        envTable.AddColumn("[bold]Variable / Setting[/]");
        envTable.AddColumn("[bold]Purpose[/]");
        envTable.AddColumn("[bold]Status[/]");

        // 1. LLM Key Check
        var llmKey = KeyResolver.Resolve(config.Subagents.ApiKey, config.Subagents.ApiKeyEnv, "GEMINI_API_KEY", "AI_API_KEY");
        var llmStatus = !string.IsNullOrWhiteSpace(llmKey)
            ? "[green]Configured[/]"
            : "[red]Missing (Required)[/]";
        var llmLabel = !string.IsNullOrWhiteSpace(config.Subagents.ApiKey) ? "subagents.apiKey" : config.Subagents.ApiKeyEnv;
        envTable.AddRow(llmLabel, "Generative LLM subagents (Gemini/Claude)", llmStatus);

        // 2. Jev Key Check
        var jevKey = KeyResolver.Resolve(config.Triage.Jev.ApiKey, config.Triage.Jev.ApiKeyEnv, "JEV_API_KEY");
        var jevStatus = !string.IsNullOrWhiteSpace(jevKey)
            ? "[green]Configured[/]"
            : config.Triage.Engine == "jev" ? "[red]Missing (Required)[/]" : "[yellow]Not Set (Optional)[/]";
        var jevLabel = !string.IsNullOrWhiteSpace(config.Triage.Jev.ApiKey) ? "triage.jev.apiKey" : config.Triage.Jev.ApiKeyEnv;
        envTable.AddRow(jevLabel, "Jev Decision Engine (Noul, Score, Choice)", jevStatus);

        // 3. GitHub Token Check
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var ghStatus = !string.IsNullOrWhiteSpace(ghToken) ? "[green]Configured[/]" : "[red]Missing (Required)[/]";
        envTable.AddRow("GH_TOKEN", "GitHub Issues and Projects v2 integration", ghStatus);

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
