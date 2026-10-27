namespace GitGavel.System1.Models;

public record SubagentGatingDecision(
    string AgentName,
    bool ShouldRun,
    double Confidence,
    string Reason,
    bool IsDeterministicOverride = false);

public record SubagentGatingResult(
    IReadOnlyList<SubagentGatingDecision> Decisions,
    long EstimatedTokensSaved = 0)
{
    public IReadOnlyList<string> ActiveAgentNames =>
        Decisions.Where(d => d.ShouldRun).Select(d => d.AgentName).ToList();

    public IReadOnlyList<string> SkippedAgentNames =>
        Decisions.Where(d => !d.ShouldRun).Select(d => d.AgentName).ToList();

    public bool HasSkippedAgents => SkippedAgentNames.Count > 0;
}
