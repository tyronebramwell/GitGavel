using GitGavel.Commands;
using Spectre.Console.Cli;

var app = new CommandApp<AuditCommand>();

app.Configure(config =>
{
    config.SetApplicationName("gitgavel");

    config.AddCommand<AuditCommand>("audit")
        .WithDescription("Audits the repository using hybrid System 1 + System 2 multi-agent architecture.")
        .WithExample("audit", "--config", ".gitgavel.yml")
        .WithExample("audit", "--mock");

    config.AddCommand<InitCommand>("init")
        .WithDescription("Initializes a new .gitgavel.yml configuration file.")
        .WithExample("init", "--force");

    config.AddCommand<ValidateCommand>("validate")
        .WithDescription("Validates GitGavel configuration and required environment variables.")
        .WithExample("validate", "--config", ".gitgavel.yml");
});

return await app.RunAsync(args);