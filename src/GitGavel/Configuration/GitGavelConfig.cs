namespace GitGavel.Configuration;

public class GitGavelConfig
{
    public int Version { get; set; } = 1;
    public ScopeConfig Scope { get; set; } = new();
    public SubagentsConfig Subagents { get; set; } = new();
    public TriageConfig Triage { get; set; } = new();
    public ReportingConfig Reporting { get; set; } = new();
    public GitHubConfig Github { get; set; } = new();
}

public class ScopeConfig
{
    public SpecsScopeConfig Specs { get; set; } = new();
    public SourceScopeConfig Source { get; set; } = new();
}

public class SpecsScopeConfig
{
    public List<string> Include { get; set; } = ["SPECIFICATION.md", "Dockerfile", "action.yml", "docs/**/*.md", "specs/**/*.md", "api/openapi.json"];
}

public class SourceScopeConfig
{
    public List<string> Include { get; set; } = ["**/*.cs", "**/*.ts", "**/*.py"];
    public List<string> Exclude { get; set; } = ["**/bin/**", "**/obj/**", "**/*.Tests/**", "**/.git/**"];
}

public class SubagentsConfig
{
    public string ApiKeyEnv { get; set; } = "AI_API_KEY";
    public string? ApiKey { get; set; }

    public SubagentSettings SpecDrift { get; set; } = new()
    {
        Enabled = true,
        Provider = "gemini",
        Model = "gemini-2.5-pro"
    };

    public SecuritySubagentSettings Security { get; set; } = new()
    {
        Enabled = true,
        Provider = "gemini",
        Model = "gemini-2.5-pro",
        SarifReports = ["reports/semgrep.sarif"]
    };

    public SubagentSettings Refactoring { get; set; } = new()
    {
        Enabled = true,
        Provider = "gemini",
        Model = "gemini-2.5-flash"
    };
}

public class SubagentSettings
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "gemini";
    public string Model { get; set; } = "gemini-2.5-pro";
}

public class SecuritySubagentSettings : SubagentSettings
{
    public List<string> SarifReports { get; set; } = [];
}

public class TriageConfig
{
    public string Engine { get; set; } = "jev"; // "jev" | "llm-synthesizer"
    public JevConfig Jev { get; set; } = new();
    public int MaxIssuesPerRun { get; set; } = 4;
}

public class JevConfig
{
    public string ApiKeyEnv { get; set; } = "JEV_API_KEY";
    public string? ApiKey { get; set; }
    public JevThresholds Thresholds { get; set; } = new();
    public bool DedupeWithNoul { get; set; } = true;
}

public class JevThresholds
{
    public double AutoBacklogConfidence { get; set; } = 0.85;
    public double HumanReviewConfidence { get; set; } = 0.50;
}

public class ReportingConfig
{
    public string OutputDir { get; set; } = "./reports";
    public bool GithubStepSummary { get; set; } = true;
    public List<string> Formats { get; set; } = ["markdown", "json"];
    public ReportsSelection Reports { get; set; } = new();
}

public class ReportsSelection
{
    public bool AuditRun { get; set; } = true;
    public bool Tickets { get; set; } = true;
    public bool Security { get; set; } = true;
    public bool CodeQuality { get; set; } = true;
}

public class GitHubConfig
{
    public string? Repository { get; set; }
    public List<string> Labels { get; set; } = ["ai-backlog"];
    public ProjectsV2Config ProjectsV2 { get; set; } = new();
}

public class ProjectsV2Config
{
    public bool Enabled { get; set; } = true;
    public int ProjectNumber { get; set; } = 2;
    public ProjectColumnsConfig Columns { get; set; } = new();
    public ProjectFieldsConfig Fields { get; set; } = new();
}

public class ProjectColumnsConfig
{
    public string Backlog { get; set; } = "Backlog";
    public string HumanReview { get; set; } = "AI Triage";
    public string Discard { get; set; } = "Discarded";
}

public class ProjectFieldsConfig
{
    public string Priority { get; set; } = "Priority";
}
