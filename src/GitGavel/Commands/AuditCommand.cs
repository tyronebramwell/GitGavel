using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.Git;
using GitGavel.System2;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;
using GitGavel.System2.Providers;
using GitGavel.System1.Abstractions;
using GitGavel.System1.Engines;
using GitGavel.System1.Models;
using GitGavel.Reporting;
using GitGavel.GitHub;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GitGavel.Commands;

public class AuditCommand : AsyncCommand<AuditCommand.Settings>
{
    private readonly IGitService _gitService;
    private readonly ISubagentOrchestrator? _injectedOrchestrator;

    public AuditCommand(IGitService? gitService = null, ISubagentOrchestrator? orchestrator = null)
    {
        _gitService = gitService ?? new GitService();
        _injectedOrchestrator = orchestrator;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("-c|--config <PATH>")]
        [Description("Path to .gitgavel.yml configuration file.")]
        [DefaultValue(".gitgavel.yml")]
        public string ConfigPath { get; set; } = ".gitgavel.yml";

        [CommandOption("-d|--dir <PATH>")]
        [Description("Path to repository root directory to audit.")]
        [DefaultValue(".")]
        public string RepoDirectory { get; set; } = ".";

        [CommandOption("--dry-run")]
        [Description("Perform audit without writing to GitHub or modifying tickets.")]
        public bool DryRun { get; set; }

        [CommandOption("--mock")]
        [Description("Use simulated mock AI subagents for offline execution.")]
        public bool UseMock { get; set; }

        [CommandOption("--mock-llm")]
        [Description("Use simulated mock LLM subagents while preserving live TypeSafe Jev triage.")]
        public bool UseMockLlm { get; set; }

        [CommandOption("-r|--repo <OWNER/REPO>")]
        [Description("GitHub repository coordinates (e.g. 'owner/repo'). Overrides git remote origin and config.")]
        public string? Repository { get; set; }

        [CommandOption("--pr <NUMBER>")]
        [Description("Optional Pull Request number to audit for PR reviews and inline comments.")]
        public int? PullRequestNumber { get; set; }
    }

    public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        AnsiConsole.Write(
            new FigletText("GitGavel")
                .Color(Color.Cyan1));

        AnsiConsole.MarkupLine("[bold grey]Multi-Agent AI Repo Auditor & Backlog Sentinel[/]\n");

        // Support GitHub Action workflow inputs passed via INPUT_* environment variables
        if (!settings.DryRun && string.Equals(Environment.GetEnvironmentVariable("INPUT_DRY_RUN"), "true", StringComparison.OrdinalIgnoreCase))
            settings.DryRun = true;
        if (!settings.UseMock && string.Equals(Environment.GetEnvironmentVariable("INPUT_MOCK"), "true", StringComparison.OrdinalIgnoreCase))
            settings.UseMock = true;
        if (!settings.UseMockLlm && string.Equals(Environment.GetEnvironmentVariable("INPUT_MOCK_LLM"), "true", StringComparison.OrdinalIgnoreCase))
            settings.UseMockLlm = true;
        if (settings.RepoDirectory == "." && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_REPO_DIRECTORY")))
            settings.RepoDirectory = Environment.GetEnvironmentVariable("INPUT_REPO_DIRECTORY")!;
        if (settings.ConfigPath == ".gitgavel.yml" && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_CONFIG_PATH")))
            settings.ConfigPath = Environment.GetEnvironmentVariable("INPUT_CONFIG_PATH")!;
        if (string.IsNullOrWhiteSpace(settings.Repository) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_REPOSITORY")))
            settings.Repository = Environment.GetEnvironmentVariable("INPUT_REPOSITORY");

        var activePrNumber = _gitService.ResolvePullRequestNumber(settings.PullRequestNumber);

        var repoDir = Path.GetFullPath(settings.RepoDirectory);
        DotEnvLoader.Load(repoDir);
        var (commitSha, branch) = _gitService.GetGitMetadata(repoDir);
        var configPath = Path.IsPathRooted(settings.ConfigPath)
            ? settings.ConfigPath
            : Path.Combine(repoDir, settings.ConfigPath);

        GitGavelConfig config;
        if (File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Loaded configuration from: [cyan]{Markup.Escape(configPath)}[/]");
            config = await ConfigLoader.LoadFromFileAsync(configPath);
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]![/] Configuration file not found at [cyan]{Markup.Escape(configPath)}[/]. Using defaults.");
            config = new GitGavelConfig();
        }

        var explicitRepo = settings.Repository ?? config.Github.Repository;
        var (owner, repo) = _gitService.ResolveRepoCoordinates(repoDir, explicitRepo);
        var mockGh = new MockGitHubService();
        if (settings.UseMock)
        {
            mockGh.ExistingIssues.Add(new ExistingIssue(
                Number: 101,
                Title: "Isolate CI environment variables in GitService fallback test",
                Body: "GitServiceTests fallback test should clear GITHUB_SHA and GITHUB_REF_NAME.",
                State: "open",
                Labels: ["ai-backlog", "testing"]));
        }

        IGitHubService gitHubService = settings.UseMock || settings.DryRun
            ? mockGh
            : new GitHubService();

        // 1. File & Context Scanning
        var scanner = new FileScanner(_gitService);
        RepoContext repoContext = null!;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Scanning repository files, specs, tests, and commit history...", async ctx =>
            {
                repoContext = await scanner.ScanAsync(repoDir, config);
            });

        PullRequestContext? prContext = null;
        if (activePrNumber.HasValue && config.Github.PullRequests.Enabled)
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Fetching Pull Request #{activePrNumber.Value} changed files, unified diffs, and existing review threads...", async ctx =>
                {
                    prContext = await gitHubService.GetPullRequestContextAsync(owner, repo, activePrNumber.Value, cancellationToken);
                });

            if (prContext != null)
            {
                repoContext = config.Github.PullRequests.OnlyChangedFiles
                    ? repoContext.ScopeToPullRequest(prContext)
                    : (repoContext with { ActivePullRequest = prContext });

                AnsiConsole.MarkupLine($"[bold cyan]🔍 Pull Request Review Mode Active (PR #{prContext.PrNumber}):[/] Scoped audit strictly to [yellow]{prContext.ChangedFiles.Count}[/] changed file(s) (`{Markup.Escape(prContext.HeadRef)}` → `{Markup.Escape(prContext.BaseRef)}`).");
            }
        }

        var scopeTable = new Table().Border(TableBorder.Rounded);
        scopeTable.AddColumn("[bold]Category[/]");
        scopeTable.AddColumn("[bold]Count[/]");
        scopeTable.AddColumn("[bold]Details[/]");

        scopeTable.AddRow("Specification Files", repoContext.SpecFiles.Count.ToString(), string.Join(", ", repoContext.SpecFiles.Select(f => f.RelativePath)));
        scopeTable.AddRow("Source Files", repoContext.SourceFiles.Count.ToString(), repoContext.IsPullRequestMode ? $"{repoContext.TotalLinesOfCode:N0} LOC (PR diff-scoped)" : $"{repoContext.TotalLinesOfCode:N0} total LOC");
        scopeTable.AddRow("Automated Test Files", repoContext.TestFiles.Count.ToString(), repoContext.IsPullRequestMode ? $"{repoContext.TotalTestLinesOfCode:N0} test LOC (PR diff-scoped)" : $"{repoContext.TotalTestLinesOfCode:N0} test LOC");
        scopeTable.AddRow("Recent Git Commits", repoContext.RecentCommits.Count.ToString(), $"Lookback window: {config.Subagents.IssueResolution.CommitLookbackCount}");
        scopeTable.AddRow("SARIF Findings", repoContext.SarifFindings.Count.ToString(), $"{repoContext.SarifFindings.Count} static security alerts loaded");

        AnsiConsole.Write(scopeTable);
        AnsiConsole.WriteLine();

        // 2. System 2 Discovery Execution
        var apiKey = KeyResolver.Resolve(config.Subagents.ApiKey, config.Subagents.ApiKeyEnv, "GEMINI_API_KEY", "AI_API_KEY");

        var llmClientFactory = new LlmClientFactory(
            forceMock: settings.UseMock || settings.UseMockLlm,
            fallbackClient: new GeminiLlmClient(apiKey));

        ISubagentOrchestrator orchestrator = _injectedOrchestrator ?? new SubagentOrchestrator(llmClientFactory);
        OrchestrationResult orchestrationResult = null!;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.BouncingBar)
            .StartAsync("Executing System 2 Generative Subagents (SpecDrift, Security, Refactoring, TestSentinel)...", async ctx =>
            {
                orchestrationResult = await orchestrator.RunAllAsync(repoContext, config);
            });

        AnsiConsole.MarkupLine($"[green]✓[/] System 2 Discovery complete in [cyan]{orchestrationResult.TotalDurationMs}ms[/] (Tokens: [yellow]{orchestrationResult.TotalTokens}[/])\n");

        foreach (var err in orchestrationResult.SubagentResults.Where(r => !r.IsSuccess))
        {
            AnsiConsole.MarkupLine($"[red]✗ Subagent {err.AgentName} failed:[/] {Markup.Escape(err.ErrorMessage ?? "Unknown error")}");
        }

        // Display Findings Table
        if (orchestrationResult.AllFindings.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No candidate issues detected across all subagents.[/]");
            return 0;
        }

        var findingsTable = new Table().Border(TableBorder.Rounded);
        findingsTable.AddColumn("[bold]#[/]");
        findingsTable.AddColumn("[bold]Category[/]");
        findingsTable.AddColumn("[bold]Severity[/]");
        findingsTable.AddColumn("[bold]Title[/]");
        findingsTable.AddColumn("[bold]Location[/]");
        findingsTable.AddColumn("[bold]Agent[/]");

        for (var i = 0; i < orchestrationResult.AllFindings.Count; i++)
        {
            var f = orchestrationResult.AllFindings[i];
            var sevColor = f.Severity switch
            {
                "Critical" => "red bold",
                "High" => "red",
                "Medium" => "yellow",
                _ => "grey"
            };

            findingsTable.AddRow(
                (i + 1).ToString(),
                f.Category,
                $"[{sevColor}]{f.Severity}[/]",
                Markup.Escape(f.Title),
                $"{Markup.Escape(f.FilePath)}:{f.StartLine}",
                f.AgentName
            );
        }

        AnsiConsole.Write(findingsTable);
        AnsiConsole.WriteLine();

        List<ExistingIssue> existingIssues = [];
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Fetching existing issues from {owner}/{repo} for deduplication & resolution tracking...", async ctx =>
            {
                existingIssues = await gitHubService.GetExistingIssuesAsync(owner, repo, cancellationToken);
            });

        // 3. System 1 Jev Decision Engine (Triage Gatekeeper)
        var jevApiKey = KeyResolver.Resolve(config.Triage.Jev.ApiKey, config.Triage.Jev.ApiKeyEnv, "JEV_API_KEY");
        ITriageEngine triageEngine = settings.UseMock || config.Triage.Engine == "llm-synthesizer"
            ? new MockTriageEngine()
            : new TypeSafeJevEngine(jevApiKey);

        List<TriageDecision> triageDecisions = null!;
        List<PrCommentTriageDecision> prCommentDecisions = [];
        PostedPrReviewResult? postedPrReview = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots12)
            .StartAsync("Executing System 1 Jev Decision Engine (Noul dedupe, Score calibration, Choice routing)...", async ctx =>
            {
                triageDecisions = await triageEngine.TriageAllAsync(orchestrationResult.AllFindings, existingIssues, config, cancellationToken);
                if (repoContext.IsPullRequestMode && prContext != null)
                {
                    prCommentDecisions = await triageEngine.TriagePrReviewCommentsAsync(
                        orchestrationResult.AllFindings,
                        prContext.ExistingReviewComments,
                        config,
                        cancellationToken);
                }
            });

        AnsiConsole.MarkupLine("[bold cyan]⚖️ Jev Decision Engine Triage Results[/]\n");

        var triageTable = new Table().Border(TableBorder.Rounded);
        triageTable.AddColumn("[bold]#[/]");
        triageTable.AddColumn("[bold]Priority[/]");
        triageTable.AddColumn("[bold]Jev Score[/]");
        triageTable.AddColumn("[bold]Target Column[/]");
        triageTable.AddColumn("[bold]Action / Status[/]");
        triageTable.AddColumn("[bold]Title[/]");
        triageTable.AddColumn("[bold]Triage Reason[/]");

        for (var i = 0; i < triageDecisions.Count; i++)
        {
            var d = triageDecisions[i];
            var prioColor = d.PriorityLabel switch
            {
                "P0" => "bold red",
                "P1" => "red",
                "P2" => "yellow",
                _ => "grey"
            };

            var statusColor = d.Status switch
            {
                TriageStatus.AcceptedToBacklog => "green bold",
                TriageStatus.RoutedToHumanReview => "cyan",
                TriageStatus.SuppressedDuplicate => "yellow",
                _ => "grey"
            };

            triageTable.AddRow(
                (i + 1).ToString(),
                $"[{prioColor}]{d.PriorityLabel}[/]",
                $"{d.JevScore:F1}/5.0",
                $"[bold]{d.TargetColumn}[/]",
                $"[{statusColor}]{d.Status}[/]",
                Markup.Escape(d.Finding.Title),
                Markup.Escape(d.Reason)
            );
        }

        AnsiConsole.Write(triageTable);
        AnsiConsole.WriteLine();

        var acceptedCount = triageDecisions.Count(d => d.Status == TriageStatus.AcceptedToBacklog);
        var reviewCount = triageDecisions.Count(d => d.Status == TriageStatus.RoutedToHumanReview);
        var dupCount = triageDecisions.Count(d => d.Status == TriageStatus.SuppressedDuplicate);
        var noiseCount = triageDecisions.Count(d => d.Status == TriageStatus.DiscardedNoise);

        AnsiConsole.MarkupLine($"[bold]Triage Summary:[/] [green]{acceptedCount} Accepted to Backlog[/] | [cyan]{reviewCount} Routed to Review[/] | [yellow]{dupCount} Duplicates Suppressed[/] | [grey]{noiseCount} Discarded[/]\n");

        // 4. GitHub Publishing (PR Review Mode vs Standard Repository Backlog Mode)
        if (repoContext.IsPullRequestMode && prContext != null)
        {
            var inlineCount = prCommentDecisions.Count(d => d.Action == PrCommentAction.PostInlineReview);
            var dupPrCount = prCommentDecisions.Count(d => d.IsDuplicate);
            AnsiConsole.MarkupLine($"[bold cyan]🔍 Pull Request #{prContext.PrNumber} Review Triage:[/] [green]{inlineCount} inline diff comment(s)[/] | [yellow]{dupPrCount} duplicate thread(s) suppressed[/]");

            if (settings.DryRun)
            {
                AnsiConsole.MarkupLine("[bold yellow]⚡ Dry Run Mode:[/] Skipping GitHub Pull Request review submission.\n");
            }
            else if (!settings.UseMock && owner == "local-owner")
            {
                AnsiConsole.MarkupLine("[bold yellow]⚠ Skipping PR Review Publishing:[/] No remote repository configured.\n");
            }
            else
            {
                try
                {
                    postedPrReview = await gitHubService.PostPullRequestReviewAsync(
                        owner,
                        repo,
                        prContext,
                        prCommentDecisions,
                        config,
                        cancellationToken);
                    AnsiConsole.MarkupLine($"  [green]✓[/] Posted PR review ([bold]{postedPrReview.Verdict}[/]) to Pull Request #{prContext.PrNumber}.\n");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [red]✗ Failed to post PR review on #{prContext.PrNumber}:[/] {Markup.Escape(ex.Message)}\n");
                }
            }
        }
        else
        {
            var actionable = triageDecisions
                .Where(d => d.Status is TriageStatus.AcceptedToBacklog or TriageStatus.RoutedToHumanReview)
                .ToList();

            if (actionable.Count > 0)
            {
                if (settings.DryRun)
                {
                    AnsiConsole.MarkupLine("[bold yellow]⚡ Dry Run Mode:[/] Skipping GitHub issue creation and Project v2 column mutations.\n");
                }
                else if (owner == "local-owner")
                {
                    AnsiConsole.MarkupLine("[bold yellow]⚠ Skipping GitHub Publishing:[/] No remote repository configured (resolved to placeholder 'local-owner/GitGavel').");
                    AnsiConsole.MarkupLine("  [grey]To publish issues to GitHub, specify a repository via [cyan]--repo <owner/repo>[/], set [cyan]repository: <owner/repo>[/] under [cyan]github:[/] in .gitgavel.yml, or configure a git remote origin (e.g. `git remote add origin https://github.com/<owner>/<repo>.git`). Run with [cyan]--dry-run[/] to suppress this warning.[/]\n");
                }
                else
                {
                    try
                    {
                        await AnsiConsole.Status()
                            .Spinner(Spinner.Known.Aesthetic)
                            .StartAsync($"Publishing {actionable.Count} issues to GitHub ({owner}/{repo}) and updating Projects v2...", async ctx =>
                            {
                                foreach (var d in actionable)
                                {
                                    var created = await gitHubService.CreateOrUpdateIssueAsync(owner, repo, d, config, cancellationToken);
                                    await gitHubService.AssignToProjectV2Async(owner, repo, created, d, config, cancellationToken);
                                }
                            });
                        AnsiConsole.MarkupLine($"[bold green]✓[/] Published [cyan]{actionable.Count}[/] issues to GitHub ([bold]{owner}/{repo}[/]) and synced to Projects v2.\n");
                    }
                    catch (Octokit.NotFoundException)
                    {
                        AnsiConsole.MarkupLine($"[bold red]✗ GitHub API Error:[/] Repository [bold]{owner}/{repo}[/] was not found on GitHub (404 Not Found).");
                        AnsiConsole.MarkupLine("  [grey]Please verify that the repository exists on GitHub, your GH_TOKEN has access to it, or run with [cyan]--dry-run[/].[/]\n");
                    }
                    catch (Octokit.AuthorizationException)
                    {
                        AnsiConsole.MarkupLine($"[bold red]✗ GitHub API Error:[/] Authentication failed (401/403 Unauthorized).");
                        AnsiConsole.MarkupLine("  [grey]Please verify that your GH_TOKEN has [bold]issues:write[/] and [bold]project:write[/] permissions.[/]\n");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[bold red]✗ Failed to publish issues to GitHub:[/] {Markup.Escape(ex.Message)}\n");
                    }
                }
            }
        }

        // 4b. Phase 7: Automated Completed Issue Resolution & Closure
        List<IssueResolutionDecision> resolutionDecisions = [];
        List<ClosedIssueResult> closedIssues = [];
        var openIssues = existingIssues.Where(i => string.Equals(i.State, "open", StringComparison.OrdinalIgnoreCase)).ToList();

        if (config.Subagents.IssueResolution.Enabled && config.Github.IssueClosure.Enabled && openIssues.Count > 0)
        {
            IssueResolutionAgentResult resolutionResult = null!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Evaluating {openIssues.Count} open issue(s) against recent commits for automated closure...", async ctx =>
                {
                    resolutionResult = await orchestrator.EvaluateIssueResolutionsAsync(openIssues, repoContext, config, cancellationToken);
                    resolutionDecisions = await triageEngine.VerifyIssueResolutionsAsync(resolutionResult.Candidates, openIssues, config, cancellationToken);
                });

            orchestrationResult = orchestrationResult with { IssueResolution = resolutionResult };

            var verifiedClosures = resolutionDecisions.Where(d => d.Action == IssueResolutionAction.CloseResolved).ToList();
            if (verifiedClosures.Count > 0)
            {
                AnsiConsole.MarkupLine($"[bold green]✅ Issue Resolution Verification:[/] [cyan]{verifiedClosures.Count}[/] open issue(s) confirmed fixed by recent commits.");
                foreach (var vc in verifiedClosures)
                {
                    var shas = string.Join(", ", vc.Candidate.ResolvingCommitShas.Select(s => s.Length > 7 ? s[..7] : s));
                    AnsiConsole.MarkupLine($"  [green]•[/] Issue [bold]#{vc.Candidate.IssueNumber}[/] ({Markup.Escape(vc.Candidate.IssueTitle)}) — Fixed in [cyan]{Markup.Escape(shas)}[/] (Jev Noul: {vc.ResolutionProbability:P0})");

                    if (!settings.DryRun && (settings.UseMock || owner != "local-owner") && config.Subagents.IssueResolution.AutoCloseCompleted)
                    {
                        try
                        {
                            var closed = await gitHubService.CloseResolvedIssueAsync(owner, repo, vc, config, cancellationToken);
                            closedIssues.Add(closed);
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"  [red]✗ Failed to close issue #{vc.Candidate.IssueNumber}:[/] {Markup.Escape(ex.Message)}");
                        }
                    }
                }
                AnsiConsole.WriteLine();
            }
        }

        // 5. Developer Reporting Engine
        var reportCtx = new ReportContext
        {
            Config = config,
            RepoContext = repoContext,
            Orchestration = orchestrationResult,
            TriageDecisions = triageDecisions,
            IssueResolutionDecisions = resolutionDecisions,
            ClosedIssues = closedIssues,
            PrCommentDecisions = prCommentDecisions,
            PostedPrReview = postedPrReview,
            ActivePullRequestNumber = activePrNumber,
            StartTimeUtc = startTime,
            EndTimeUtc = DateTime.UtcNow,
            GitCommitSha = commitSha,
            GitBranch = branch
        };

        var reportingEngine = new ReportingEngine();
        var generatedReports = await reportingEngine.GenerateReportsAsync(reportCtx, cancellationToken);

        if (repoContext.IsPullRequestMode && prContext != null && !settings.DryRun && (settings.UseMock || owner != "local-owner"))
        {
            try
            {
                var postedDomains = await gitHubService.PostPullRequestDomainReportsAsync(
                    owner,
                    repo,
                    prContext.PrNumber,
                    reportCtx,
                    cancellationToken);

                if (postedDomains.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[bold green]✓[/] Posted domain report comment(s) ([cyan]{string.Join(", ", postedDomains)}[/]) to Pull Request #{prContext.PrNumber}.\n");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Failed to post domain report comments on PR #{prContext.PrNumber}:[/] {Markup.Escape(ex.Message)}\n");
            }
        }

        AnsiConsole.MarkupLine("[bold green]📁 Developer Reports Generated[/]\n");
        foreach (var reportFile in generatedReports)
        {
            var relPath = Path.GetRelativePath(repoDir, reportFile);
            AnsiConsole.MarkupLine($"  [green]✓[/] [cyan]{Markup.Escape(relPath)}[/]");
        }
        AnsiConsole.WriteLine();

        return 0;
    }
}
