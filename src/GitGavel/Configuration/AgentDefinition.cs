namespace GitGavel.Configuration;

public class AgentDefinition : SubagentSettings
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Architecture";
    public int Version { get; set; } = 1;
    public AgentScopeConfig? Scope { get; set; }
    public AgentGateConfig? Gate { get; set; }
    public string? UserPromptIntro { get; set; }
    public string? AdditionalPromptBody { get; set; }
    public string? AntiScopeCreepRule { get; set; }
    public string? JsonSchemaInstructions { get; set; }
    public string? UserPromptTemplate { get; set; }
    public string? PromptFile { get; set; }
    public List<string>? Tiers { get; set; }
}

public class AgentScopeConfig
{
    public List<string> Include { get; set; } = [];
    public List<string> Exclude { get; set; } = [];
    public bool RequireSpecs { get; set; } = false;
    public bool RequireSourceOrTests { get; set; } = false;
    public bool IncludeSpecs { get; set; } = false;
    public bool IncludeTests { get; set; } = false;
    public bool IncludeAst { get; set; } = true;
    public bool IncludeAstComplexity { get; set; } = false;
    public string? AstHeading { get; set; }
    public bool IncludeComplexityHotspots { get; set; } = false;
    public string? HotspotsHeading { get; set; }
    public string? HotspotItemPrefix { get; set; }
    public bool IncludeSarif { get; set; } = false;
    public bool ExcludeTestSourceFiles { get; set; } = false;
    public bool PrioritizeHotspots { get; set; } = false;
    public int? MaxSourceFiles { get; set; }
    public bool OmitDiffContentInPrMode { get; set; } = false;
    public string? SourceHeading { get; set; }
    public string? SourceLabelPrefix { get; set; }
}

public class AgentGateConfig
{
    public string Engine { get; set; } = "deterministic-then-jev"; // "deterministic" | "jev" | "llm" | "deterministic-then-jev"
    public DeterministicGateRuleSet? Deterministic { get; set; }
    public JevAgentGateConfig? Jev { get; set; }
    public LlmAgentGateConfig? Llm { get; set; }
}

public class DeterministicGateRuleSet
{
    public List<string> AnyOf { get; set; } = [];
    public List<string> AllOf { get; set; } = [];
    public List<string> SkipIf { get; set; } = [];
}

public class JevAgentGateConfig
{
    public string Primitive { get; set; } = "noul";
    public string? Question { get; set; }
    public double MinConfidence { get; set; } = 0.50;
}

public class LlmAgentGateConfig
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Question { get; set; }
}
