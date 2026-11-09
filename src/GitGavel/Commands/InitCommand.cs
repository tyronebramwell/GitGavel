using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using GitGavel.Configuration;
using GitGavel.System2.Agents;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GitGavel.Commands;

public class InitCommand : Command<InitCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-f|--force")]
        [Description("Overwrite existing .gitgavel.yml or ejected agent files if present.")]
        public bool Force { get; set; }

        [CommandOption("-d|--dir <DIR>")]
        [Description("Target directory in which to create .gitgavel.yml (defaults to current directory).")]
        [DefaultValue(".")]
        public string TargetDirectory { get; set; } = ".";

        [CommandOption("--eject-agents")]
        [Description("Export the 6 standard agent YAML definitions into .gitgavel/agents/ for customization.")]
        public bool EjectAgents { get; set; }
    }

    public override int Execute([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
    {
        var rawDir = string.IsNullOrWhiteSpace(settings.TargetDirectory) ? "." : settings.TargetDirectory;
        if (rawDir.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains(".."))
        {
            AnsiConsole.MarkupLine("[red]✗[/] Invalid target directory: path traversal ('..') is not allowed.");
            return 1;
        }

        var resolvedDir = Path.GetFullPath(rawDir);
        PathSecurity.ValidateRootDirectory(resolvedDir);
        var targetFile = PathSecurity.ResolveAndVerifyWithinRoot(
            Path.Combine(resolvedDir, ".gitgavel.yml"),
            resolvedDir,
            allowExternal: false);

        if (File.Exists(targetFile) && !settings.Force && !settings.EjectAgents)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] File '[cyan]{targetFile}[/]' already exists. Use [bold]--force[/] to overwrite.");
            return 1;
        }

        if (!File.Exists(targetFile) || settings.Force)
        {
            var sampleConfig = new GitGavelConfig();
            var yaml = ConfigLoader.Serialize(sampleConfig);
            File.WriteAllText(targetFile, yaml);
            AnsiConsole.MarkupLine($"[green]✓[/] Successfully initialized [cyan]{targetFile}[/]!");
        }

        if (settings.EjectAgents)
        {
            var agentsDir = Path.Combine(resolvedDir, ".gitgavel", "agents");
            var ejected = AgentDefinitionRegistry.EjectStandardAgents(agentsDir, resolvedDir, settings.Force);
            AnsiConsole.MarkupLine($"[green]✓[/] Exported [cyan]{ejected.Count}[/] standard agent YAML file(s) to [cyan]{Markup.Escape(agentsDir)}[/]!");
        }

        return 0;
    }
}
