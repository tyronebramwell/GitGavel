using GitGavel.Commands;
using GitGavel.Git;
using GitGavel.System2;
using GitGavel.System2.Abstractions;
using Spectre.Console.Cli;

// GitGavel CLI Entrypoint
// Orchestrates System 2 Generative Subagents (SpecDriftAgent, SecurityAuditorAgent,
// ReliabilityAgent, RefactoringAgent, TestSentinelAgent, IssueResolutionAgent) via ISubagentOrchestrator
// and System 1 Probabilistic Triage via TypeSafe Jev (ITriageEngine).
var app = new CommandApp<AuditCommand>();

app.Configure(config =>
{
    config.SetApplicationName("gitgavel");

    config.AddCommand<AuditCommand>("audit")
        .WithDescription("Audits the repository using hybrid System 1 (TypeSafe Jev) + System 2 (SpecDrift, Security, Reliability, Refactoring, TestSentinel, IssueResolution) multi-agent architecture.")
        .WithExample("audit", "--config", ".gitgavel.yml")
        .WithExample("audit", "--mock")
        .WithExample("audit", "--pr", "42");

    config.AddCommand<InitCommand>("init")
        .WithDescription("Initializes a new .gitgavel.yml configuration file.")
        .WithExample("init", "--force");

    config.AddCommand<ValidateCommand>("validate")
        .WithDescription("Validates GitGavel configuration and required environment variables.")
        .WithExample("validate", "--config", ".gitgavel.yml");
});

return await app.RunAsync(args);
