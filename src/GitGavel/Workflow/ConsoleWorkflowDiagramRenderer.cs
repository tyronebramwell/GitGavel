using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GitGavel.Workflow;

public static class ConsoleWorkflowDiagramRenderer
{
    public static IRenderable BuildRenderable(WorkflowSnapshot snapshot)
    {
        var markup = FormatDiagramMarkup(snapshot);
        var headerTitle = $"[bold cyan]GitGavel Workflow State Machine ({Markup.Escape(snapshot.WorkflowName)})[/] [grey]──[/] [yellow]Active Slots: {snapshot.ActiveConcurrencySlots}/{snapshot.MaxConcurrencySlots}[/]";

        return new Panel(new Markup(markup))
        {
            Header = new PanelHeader(headerTitle),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0, 1, 0)
        };
    }

    public static string FormatDiagramMarkup(WorkflowSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var groupedByLevel = snapshot.Nodes
            .GroupBy(n => n.TopologicalLevel)
            .OrderBy(g => g.Key)
            .ToList();

        for (var gIdx = 0; gIdx < groupedByLevel.Count; gIdx++)
        {
            var group = groupedByLevel[gIdx];
            var level = group.Key;
            var nodesInLevel = group.ToList();

            var levelLabel = level == 0
                ? "Level 0 (Root)"
                : nodesInLevel.Count > 1
                    ? $"Level {level} (Auto-Parallel)"
                    : $"Level {level} (Reactive Series)";

            sb.AppendLine($"[bold underline]{Markup.Escape(levelLabel)}[/]");

            for (var i = 0; i < nodesInLevel.Count; i++)
            {
                var node = nodesInLevel[i];
                var branchPrefix = i == nodesInLevel.Count - 1 ? " └──" : " ├──";
                var badge = GetStatusBadgeMarkup(node.Status);
                var sysIcon = string.Equals(node.System, "system1", StringComparison.OrdinalIgnoreCase) ? "⚡" : "🤖";
                var modelBadge = !string.IsNullOrWhiteSpace(node.Model)
                    ? $"{node.System}: {node.ProviderOrEngine}/{node.Model}"
                    : $"{node.System}: {node.ProviderOrEngine}";

                var edgeInfo = BuildDependencyEdgeText(node);
                var metaTags = BuildMetaTagsText(node);
                var statusDetail = BuildStatusDetailMarkup(node);

                sb.AppendLine($"{branchPrefix} {badge} {sysIcon} [bold]{Markup.Escape(node.Name)}[/] [grey]({Markup.Escape(modelBadge)})[/]{edgeInfo} [grey]─[/] {statusDetail}{metaTags}");
            }

            if (gIdx < groupedByLevel.Count - 1)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string GetStatusBadgeMarkup(WorkflowNodeStatus status) =>
        status switch
        {
            WorkflowNodeStatus.NotUsed => "[grey]⚪ Not Used[/]",
            WorkflowNodeStatus.Waiting => "[yellow]⏳ Waiting[/]",
            WorkflowNodeStatus.Queued => "[green]🟢 Queued[/]",
            WorkflowNodeStatus.Running => "[cyan bold]🔄 Running[/]",
            WorkflowNodeStatus.Skipped => "[grey]⏭️ Skipped[/]",
            WorkflowNodeStatus.Completed => "[green bold]✅ Completed[/]",
            WorkflowNodeStatus.Failed => "[red bold]❌ Failed[/]",
            _ => "[grey]•[/]"
        };

    private static string BuildDependencyEdgeText(WorkflowNodeRuntimeState node)
    {
        if (node.InputsFrom.Count > 0)
        {
            return $" [cyan]══► inputsFrom: [[{Markup.Escape(string.Join(", ", node.InputsFrom))}]][/]";
        }

        if (node.DependsOn.Count > 0)
        {
            return $" [grey]───► dependsOn: [[{Markup.Escape(string.Join(", ", node.DependsOn))}]][/]";
        }

        return string.Empty;
    }

    private static string BuildMetaTagsText(WorkflowNodeRuntimeState node)
    {
        var tags = new List<string>();
        if (node.SkillsCount > 0)
        {
            tags.Add($"{node.SkillsCount} skill(s)");
        }
        if (node.ArtifactsCount > 0)
        {
            tags.Add($"{node.ArtifactsCount} artifact(s)");
        }
        if (node.CriticalPathWeight > 0 && node.Status is WorkflowNodeStatus.Queued or WorkflowNodeStatus.Running)
        {
            tags.Add("critical-path");
        }
        if (!node.PassResults && node.Status == WorkflowNodeStatus.Completed)
        {
            tags.Add("passResults: false");
        }

        return tags.Count > 0
            ? $" [grey][[{Markup.Escape(string.Join(", ", tags))}]][/]"
            : string.Empty;
    }

    private static string BuildStatusDetailMarkup(WorkflowNodeRuntimeState node)
    {
        return node.Status switch
        {
            WorkflowNodeStatus.NotUsed => $"[grey]{Markup.Escape(node.StatusReason ?? "Disabled in configuration")}[/]",
            WorkflowNodeStatus.Waiting => node.WaitingOnNodeIds.Count > 0
                ? $"[yellow]Waiting on [[{Markup.Escape(string.Join(", ", node.WaitingOnNodeIds))}]][/]"
                : "[yellow]Waiting on upstream dependencies[/]",
            WorkflowNodeStatus.Queued => $"[green]{Markup.Escape(node.StatusReason ?? "Ready (awaiting concurrency slot)")}[/]",
            WorkflowNodeStatus.Running => $"[cyan]{node.ElapsedMs / 1000.0:F1}s elapsed...[/]",
            WorkflowNodeStatus.Skipped => $"[grey]{Markup.Escape(node.StatusReason ?? "Skipped by gate")}[/]",
            WorkflowNodeStatus.Completed => string.Equals(node.System, "system1", StringComparison.OrdinalIgnoreCase)
                ? $"[green]{node.ElapsedMs / 1000.0:F1}s[/] [grey]({Markup.Escape(node.StatusReason ?? "Gate passed")})[/]"
                : $"[green]{node.ElapsedMs / 1000.0:F1}s[/] [grey]({node.FindingsCount} finding(s){(node.TokensUsed > 0 ? $", {node.TokensUsed:N0} tokens" : "")})[/]",
            WorkflowNodeStatus.Failed => $"[red]{Markup.Escape(node.StatusReason ?? "Execution failed")}[/]",
            _ => string.Empty
        };
    }
}
