using System.Text;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace GitGavel.System2.Agents;

public class DeclarativeYamlAgent : BaseLlmAgent
{
    private readonly AgentDefinition _definition;

    public override string Name => !string.IsNullOrWhiteSpace(_definition.Name) ? _definition.Name : _definition.Id;
    protected override string DefaultCategory => !string.IsNullOrWhiteSpace(_definition.Category) ? _definition.Category : "Architecture";
    protected override string SystemPrompt =>
        !string.IsNullOrWhiteSpace(_definition.SystemPrompt)
            ? _definition.SystemPrompt
            : $"You are {Name}, an expert software auditor for category '{DefaultCategory}'. Return strictly a JSON array of findings.";

    public DeclarativeYamlAgent(
        ILlmClient llmClient,
        AgentDefinition definition,
        string? modelOverride = null,
        string? fallbackModel = null)
        : base(
            llmClient,
            LlmModelDefaults.ResolveSubagentModel(
                !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride : definition.Model,
                fallbackModel ?? LlmModelDefaults.DefaultGeminiProModel),
            definition)
    {
        _definition = definition;
    }

    protected override bool ShouldSkipExecution(RepoContext context)
    {
        var scope = _definition.Scope;
        if (scope == null)
        {
            return false;
        }

        if (scope.RequireSpecs && (context.SpecFiles?.Count ?? 0) == 0)
        {
            return true;
        }

        if (scope.RequireSourceOrTests &&
            (context.SourceFiles?.Count ?? 0) == 0 &&
            (context.TestFiles?.Count ?? 0) == 0)
        {
            return true;
        }

        return false;
    }

    protected override Task<string> BuildPromptAsync(RepoContext context, CancellationToken ct)
    {
        var scope = _definition.Scope;
        var scopedFiles = FilterFilesByAgentScope(context.SourceFiles ?? [], scope);

        if (!string.IsNullOrWhiteSpace(_definition.UserPromptTemplate))
        {
            return Task.FromResult(RenderTemplate(_definition.UserPromptTemplate, context, scopedFiles));
        }

        var sb = new StringBuilder();
        var intro = !string.IsNullOrWhiteSpace(_definition.UserPromptIntro)
            ? _definition.UserPromptIntro
            : $"You are {Name}. Audit the scoped files and context for '{DefaultCategory}' issues.";
        sb.AppendLine(intro.Trim());

        if (_definition.Tiers is { Count: > 0 })
        {
            sb.AppendLine($"Target Test Tiers: {string.Join(", ", _definition.Tiers)}");
        }

        AgentPromptBuilder.AppendPullRequestGuidance(
            sb,
            context,
            _definition.AntiScopeCreepRule ?? "ANTI-SCOPE-CREEP RULE: Only report high-confidence findings directly introduced in this PR diff; return [] if no issues exist.");

        if (!string.IsNullOrWhiteSpace(_definition.AdditionalPromptBody))
        {
            sb.AppendLine(_definition.AdditionalPromptBody.Trim());
        }

        AgentPromptBuilder.AppendAgentEnrichments(sb, context, _definition);

        if (scope?.IncludeSpecs == true && (context.SpecFiles?.Count ?? 0) > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Specification Documents:");
            foreach (var spec in context.SpecFiles!)
            {
                sb.AppendLine($"--- File: {spec.RelativePath} ---");
                sb.AppendLine(AgentPromptBuilder.Truncate(spec.Content, AgentPromptBuilder.MaxSpecLength));
            }
        }

        if (scope?.IncludeSarif == true && (context.SarifFindings?.Count ?? 0) > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Static Security Scan Findings (SARIF):");
            foreach (var sarif in context.SarifFindings!.Take(15))
            {
                sb.AppendLine($"- Rule: {sarif.RuleId} | Level: {sarif.Level} | File: {sarif.FilePath}:{sarif.StartLine} | {sarif.Message}");
            }
        }

        IReadOnlyList<FileAstSummary>? astSummaries = null;
        if (scope?.IncludeAst == true || scope?.IncludeComplexityHotspots == true || scope?.PrioritizeHotspots == true)
        {
            astSummaries = AgentPromptBuilder.ResolveAstSummaries(context);
        }

        if (scope?.IncludeAst == true && astSummaries != null)
        {
            AgentPromptBuilder.AppendSymbolMap(
                sb,
                astSummaries,
                includeComplexity: scope.IncludeAstComplexity,
                heading: !string.IsNullOrWhiteSpace(scope.AstHeading)
                    ? scope.AstHeading
                    : "### Repository Architecture & Symbol Map:");
        }

        var hotspots = astSummaries != null
            ? astSummaries.Where(s => s.IsComplexityHotspot).ToList()
            : [];

        if (scope?.IncludeComplexityHotspots == true && hotspots.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(!string.IsNullOrWhiteSpace(scope.HotspotsHeading)
                ? scope.HotspotsHeading
                : "### Detected Complexity Hotspots:");
            var prefix = !string.IsNullOrWhiteSpace(scope.HotspotItemPrefix) ? scope.HotspotItemPrefix : "- Hotspot:";
            foreach (var spot in hotspots)
            {
                sb.AppendLine($"{prefix} `{spot.RelativePath}` (Complexity score: {spot.TotalComplexityScore}, Symbols: {spot.Symbols.Count})");
            }
        }

        if (scope?.IncludeTests == true)
        {
            if ((context.TestFiles?.Count ?? 0) == 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Existing Automated Test Suite Files:");
                sb.AppendLine("(No automated test files discovered in scope)");
            }
            else
            {
                var testSummaries = AstAnalyzer.AnalyzeAll(context.TestFiles!);
                AgentPromptBuilder.AppendSymbolMap(
                    sb,
                    testSummaries,
                    includeComplexity: false,
                    heading: "### Existing Automated Test Suite Symbol Map (All Discovered Test Classes & Methods):");

                AgentPromptBuilder.AppendFilesSection(
                    sb,
                    context.TestFiles!,
                    "### Existing Automated Test Suite Files:",
                    fileLabelPrefix: "Test File");
            }
        }

        IEnumerable<ScannedFile> sourceFilesToInclude = scopedFiles;
        if (scope?.ExcludeTestSourceFiles == true)
        {
            sourceFilesToInclude = sourceFilesToInclude
                .Where(f => !f.IsTest && (!context.IsPullRequestMode || !string.IsNullOrWhiteSpace(f.DiffPatch)));
        }

        if (scope?.PrioritizeHotspots == true)
        {
            var hotspotPaths = hotspots
                .Select(h => h.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            sourceFilesToInclude = sourceFilesToInclude
                .OrderByDescending(f => hotspotPaths.Contains(f.RelativePath))
                .ThenByDescending(f => f.LineCount);
        }

        if (scope?.MaxSourceFiles is > 0)
        {
            sourceFilesToInclude = sourceFilesToInclude.Take(scope.MaxSourceFiles.Value);
        }

        AgentPromptBuilder.AppendFilesSection(
            sb,
            sourceFilesToInclude,
            !string.IsNullOrWhiteSpace(scope?.SourceHeading) ? scope!.SourceHeading! : "### Scoped Source Files Under Audit:",
            fileLabelPrefix: !string.IsNullOrWhiteSpace(scope?.SourceLabelPrefix) ? scope!.SourceLabelPrefix! : "File",
            omitContentWhenDiffPresent: scope?.OmitDiffContentInPrMode == true && context.IsPullRequestMode);

        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(_definition.JsonSchemaInstructions))
        {
            sb.AppendLine(_definition.JsonSchemaInstructions.Trim());
        }
        else
        {
            sb.AppendLine($$"""
            Return a JSON array of findings with the following schema:
            [
              {
                "title": "Concise finding title",
                "summary": "Clear explanation of the finding",
                "category": "{{DefaultCategory}}",
                "severity": "Critical | High | Medium | Low",
                "filePath": "path/to/file",
                "startLine": 10,
                "endLine": 25,
                "suggestedRemediation": "Concrete remediation snippet or action plan",
                "details": "Detailed context"
              }
            ]
            """);
        }

        return Task.FromResult(sb.ToString());
    }

    private string RenderTemplate(string template, RepoContext context, IReadOnlyList<ScannedFile> scopedFiles)
    {
        var prSb = new StringBuilder();
        AgentPromptBuilder.AppendPullRequestGuidance(
            prSb,
            context,
            _definition.AntiScopeCreepRule ?? "ANTI-SCOPE-CREEP RULE: Only report high-confidence findings directly introduced in this PR diff.");

        var skillsText = context.SkillsContext?.BuildPromptSectionForAgent(_definition.Skills) ?? string.Empty;
        var artifactsText = context.PipelineArtifacts?.BuildPromptSectionForAgent(_definition.Artifacts) ?? string.Empty;

        var upstreamSb = new StringBuilder();
        if (context.UpstreamFindings is { Count: > 0 })
        {
            AgentPromptBuilder.AppendUpstreamFindingsSection(upstreamSb, context.UpstreamFindings);
        }

        var symbolSb = new StringBuilder();
        var summaries = AgentPromptBuilder.ResolveAstSummaries(context);
        AgentPromptBuilder.AppendSymbolMap(symbolSb, summaries, includeComplexity: true);

        var filesSb = new StringBuilder();
        AgentPromptBuilder.AppendFilesSection(filesSb, scopedFiles, "### Scoped Source Files Under Audit:");

        return template
            .Replace("{{PullRequestGuidance}}", prSb.ToString().Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{AgentSkills}}", skillsText.Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{PipelineArtifacts}}", artifactsText.Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{UpstreamStageResults}}", upstreamSb.ToString().Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{RepositorySymbolMap}}", symbolSb.ToString().Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{ScopedFiles}}", filesSb.ToString().Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ScannedFile> FilterFilesByAgentScope(
        IReadOnlyList<ScannedFile> files,
        AgentScopeConfig? scope)
    {
        if (scope == null || (scope.Include.Count == 0 && scope.Exclude.Count == 0))
        {
            return files;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        if (scope.Include.Count > 0)
        {
            foreach (var inc in scope.Include.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                matcher.AddInclude(inc);
            }
        }
        else
        {
            matcher.AddInclude("**/*");
        }

        foreach (var exc in scope.Exclude.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            matcher.AddExclude(exc);
        }

        return files
            .Where(f => matcher.Match(f.RelativePath.Replace('\\', '/')).HasMatches)
            .ToList();
    }

    internal static AgentDefinition CreateTestSentinelDefinition(IReadOnlyList<string>? tiers, AgentDefinition? definition)
    {
        var resolved = definition ?? StandardAgentYamlCatalog.GetStandardDefinition("tests");
        if (tiers is { Count: > 0 })
        {
            resolved.Tiers = [.. tiers];
        }
        else if (resolved.Tiers == null || resolved.Tiers.Count == 0)
        {
            resolved.Tiers = ["unit", "integration", "e2e"];
        }

        return resolved;
    }
}

public sealed class SpecDriftAgent(ILlmClient llmClient, string? model = null, AgentDefinition? definition = null)
    : DeclarativeYamlAgent(
        llmClient,
        definition ?? StandardAgentYamlCatalog.GetStandardDefinition("specDrift"),
        model,
        LlmModelDefaults.DefaultGeminiProModel);

public sealed class SecurityAuditorAgent(ILlmClient llmClient, string? model = null, AgentDefinition? definition = null)
    : DeclarativeYamlAgent(
        llmClient,
        definition ?? StandardAgentYamlCatalog.GetStandardDefinition("security"),
        model,
        LlmModelDefaults.DefaultGeminiProModel);

public sealed class ReliabilityAgent(ILlmClient llmClient, string? model = null, AgentDefinition? definition = null)
    : DeclarativeYamlAgent(
        llmClient,
        definition ?? StandardAgentYamlCatalog.GetStandardDefinition("reliability"),
        model,
        LlmModelDefaults.DefaultGeminiReliabilityModel);

public sealed class RefactoringAgent(ILlmClient llmClient, string? model = null, AgentDefinition? definition = null)
    : DeclarativeYamlAgent(
        llmClient,
        definition ?? StandardAgentYamlCatalog.GetStandardDefinition("refactoring"),
        model,
        LlmModelDefaults.DefaultGeminiFlashModel);

public sealed class TestSentinelAgent(
    ILlmClient llmClient,
    string? model = null,
    IReadOnlyList<string>? tiers = null,
    AgentDefinition? definition = null)
    : DeclarativeYamlAgent(
        llmClient,
        CreateTestSentinelDefinition(tiers, definition),
        model,
        LlmModelDefaults.DefaultGeminiProModel);
