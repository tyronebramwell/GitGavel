using System.Text;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System1.Models;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Agents;

public class IssueResolutionAgent
{
    private readonly ILlmClient _llmClient;
    private readonly string _model;

    public string Name => "IssueResolutionAuditor";

    public IssueResolutionAgent(ILlmClient llmClient, string model = LlmModelDefaults.DefaultGeminiFlashModel)
    {
        _llmClient = llmClient;
        _model = LlmModelDefaults.ResolveSubagentModel(model, LlmModelDefaults.DefaultGeminiFlashModel);
    }

    public async Task<IssueResolutionAgentResult> EvaluateOpenIssuesAsync(
        IReadOnlyList<ExistingIssue> openIssues,
        RepoContext context,
        CancellationToken ct = default)
    {
        var activeIssues = openIssues
            .Where(i => string.Equals(i.State, "open", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Number)
            .ToList();

        if (activeIssues.Count == 0)
        {
            return new IssueResolutionAgentResult
            {
                AgentName = Name,
                Candidates = []
            };
        }

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Issue Resolution Agent. Audit the following open GitHub issues against the current codebase and recent git commit history.");
        promptBuilder.AppendLine("Identify which open issues have been completed/fixed, explain concisely how each issue was fixed, and list the specific git commit SHA(s) and commit messages that resolved them.");

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Open GitHub Issues:");
        foreach (var issue in activeIssues)
        {
            promptBuilder.AppendLine($"--- Issue #{issue.Number}: {issue.Title} ---");
            promptBuilder.AppendLine(AgentPromptBuilder.Truncate(issue.Body, AgentPromptBuilder.MaxIssueBodyLength));
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Recent Git Commit History:");
        if (context.RecentCommits.Count == 0)
        {
            promptBuilder.AppendLine("(No recent git commits found)");
        }
        else
        {
            foreach (var commit in context.RecentCommits.Take(50))
            {
                var files = commit.ChangedFiles.Count > 0 ? string.Join(", ", commit.ChangedFiles.Take(8)) : "none";
                promptBuilder.AppendLine($"- Commit `{commit.ShortSha}` ({commit.Sha}) by {commit.Author} on {commit.DateIso}: \"{commit.Subject}\" [Files: {files}]");
            }
        }

        var summaries = AgentPromptBuilder.ResolveAstSummaries(context);
        AgentPromptBuilder.AppendSymbolMap(promptBuilder, summaries);

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Current Repository Files Snapshot:");
        promptBuilder.AppendLine("(File contents omitted for context size limits. Rely on the AST Symbol Map and Git Commit History above to determine issue resolution.)");

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Return a JSON array of resolved issues with the following schema:");
        promptBuilder.AppendLine("""
        [
          {
            "issueNumber": 42,
            "issueTitle": "Title of the issue",
            "isResolved": true,
            "howItWasFixed": "Concise explanation of how the issue was fixed in the codebase",
            "resolvingCommitShas": ["cfad09afc6f436fb3af957ff2d94eba2d3e1ea36"],
            "resolvingCommitSummaries": ["`cfad09a` — *fix(tests): isolate CI environment variables in GitService fallback test*"]
          }
        ]
        """);

        try
        {
            var response = await _llmClient.GenerateAsync(new LlmRequest
            {
                AgentName = Name,
                Prompt = promptBuilder.ToString(),
                SystemPrompt = "You are a senior maintainer verifying whether open GitHub issues have been completed in recent commits. Return strictly a JSON array of resolution assessments.",
                Model = _model,
                ExpectJson = true
            }, ct);

            var candidates = FindingParser.ParseIssueResolutions(response.Content);

            var enriched = candidates
                .Where(c => c.IsResolved)
                .Select(c => EnrichCommitSummaries(c, context))
                .ToList();

            return new IssueResolutionAgentResult
            {
                AgentName = Name,
                Candidates = enriched,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                DurationMs = response.DurationMs,
                IsSuccess = true
            };
        }
        catch (Exception ex)
        {
            return new IssueResolutionAgentResult
            {
                AgentName = Name,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static IssueResolutionCandidate EnrichCommitSummaries(IssueResolutionCandidate candidate, RepoContext context)
    {
        var shas = candidate.ResolvingCommitShas;
        if (shas.Count == 0 && candidate.IsResolved && context.RecentCommits.Count > 0)
        {
            shas = [context.RecentCommits[0].Sha];
        }

        if (candidate.ResolvingCommitSummaries.Count > 0 || shas.Count == 0)
        {
            return shas == candidate.ResolvingCommitShas
                ? candidate
                : candidate with { ResolvingCommitShas = shas };
        }

        var summaries = new List<string>();
        foreach (var sha in shas)
        {
            var match = context.RecentCommits.FirstOrDefault(c =>
                c.Sha.StartsWith(sha, StringComparison.OrdinalIgnoreCase) ||
                sha.StartsWith(c.ShortSha, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                summaries.Add($"`{match.ShortSha}` — *{match.Subject}*");
            }
            else
            {
                var shortSha = sha.Length > 7 ? sha[..7] : sha;
                summaries.Add($"`{shortSha}`");
            }
        }

        return candidate with
        {
            ResolvingCommitShas = shas,
            ResolvingCommitSummaries = summaries
        };
    }
}
