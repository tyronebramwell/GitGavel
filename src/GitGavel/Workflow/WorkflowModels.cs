using GitGavel.Configuration;

namespace GitGavel.Workflow;

public class WorkflowConfig
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Default GitGavel DAG Workflow";
    public WorkflowConcurrencyConfig Concurrency { get; set; } = new();
    public List<WorkflowNodeConfig> Nodes { get; set; } = [];
}

public class WorkflowConcurrencyConfig
{
    public int MaxParallelSteps { get; set; } = 4;
    public string SchedulingPolicy { get; set; } = "critical-path"; // "critical-path" | "fifo"
    public Dictionary<string, int> MaxPerProvider { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini"] = 4,
        ["openai"] = 4,
        ["anthropic"] = 3
    };
}

public class WorkflowNodeConfig
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Agent { get; set; }
    public string System { get; set; } = "system2"; // "system1" | "system2"
    public string? Engine { get; set; }             // "jev" | "deterministic" | "llm" | "deterministic-then-jev"
    public string? Type { get; set; }               // "subagent-gating" | "finding-and-closure-triage" | "gate"
    public string? Condition { get; set; }
    public WorkflowGateRulesConfig? Rules { get; set; }
    public JevAgentGateConfig? Jev { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public List<string> InputsFrom { get; set; } = [];
    public bool PassResults { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public class WorkflowGateRulesConfig
{
    public Dictionary<string, string> ForceActivate { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SkipIf { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum WorkflowNodeStatus
{
    NotUsed,
    Waiting,
    Queued,
    Running,
    Skipped,
    Completed,
    Failed
}

public record WorkflowNodeRuntimeState
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string System { get; init; }
    public string ProviderOrEngine { get; init; } = "gemini";
    public string? Model { get; init; }
    public int TopologicalLevel { get; init; }
    public int CriticalPathWeight { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> InputsFrom { get; init; } = [];
    public bool PassResults { get; init; } = true;
    public int SkillsCount { get; init; }
    public int ArtifactsCount { get; init; }
    public WorkflowNodeStatus Status { get; init; } = WorkflowNodeStatus.Waiting;
    public long ElapsedMs { get; init; }
    public int FindingsCount { get; init; }
    public int TokensUsed { get; init; }
    public string? StatusReason { get; init; }
    public IReadOnlyList<string> WaitingOnNodeIds { get; init; } = [];
}

public record WorkflowSnapshot(
    string WorkflowName,
    int ActiveConcurrencySlots,
    int MaxConcurrencySlots,
    IReadOnlyList<WorkflowNodeRuntimeState> Nodes);
