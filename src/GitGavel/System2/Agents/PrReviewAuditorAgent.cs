using System.Text;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Agents;

public class PrReviewAuditorAgent
{
    private readonly ILlmClient _llmClient;
    private readonly string _model;

    public string Name => "PrReviewAuditor";

    public PrReviewAuditorAgent(ILlmClient llmClient, string model = "gemini-2.5-pro")
    {
        _llmClient = llmClient;
        _model = model;
    }

    public async Task<PrReviewAuditResult> AuditPullRequestAsync(
        PullRequestContext prContext,
        RepoContext repoContext,
        GitGavelConfig config,
        CancellationToken ct = default)
    {
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the PR Review Auditor Agent. Audit this Pull Request diff alongside existing PR reviews and comments.");
        promptBuilder.AppendLine($"Pull Request #{prContext.PrNumber}: {prContext.Title}");
        promptBuilder.AppendLine($"Branches: {prContext.HeadRef} -> {prContext.BaseRef} (Head SHA: {prContext.HeadSha})");
        if (!string.IsNullOrWhiteSpace(prContext.Body))
        {
            promptBuilder.AppendLine("PR Description:");
            promptBuilder.AppendLine(prContext.Body.Length > 2000 ? prContext.Body[..2000] + "\n...(truncated)" : prContext.Body);
        }

        if (config.Subagents.PrReview.AuditExistingReviews && (prContext.ExistingReviews.Count > 0 || prContext.ExistingReviewComments.Count > 0))
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("### Existing PR Reviews & Review Comments:");
            foreach (var rev in prContext.ExistingReviews.Take(10))
            {
                promptBuilder.AppendLine($"- Review by @{rev.Author} [{rev.State}]: {rev.Body}");
            }
            foreach (var c in prContext.ExistingReviewComments.Take(25))
            {
                promptBuilder.AppendLine($"- Inline Comment #{c.Id} by @{c.Author} on `{c.FilePath}:{c.Line}`: {c.Body}");
            }
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Changed Files & Diff Patches:");
        foreach (var file in prContext.ChangedFiles.Take(20))
        {
            promptBuilder.AppendLine($"--- Diff: {file.FilePath} ({file.Status}, +{file.Additions}/-{file.Deletions}) ---");
            var patch = file.Patch ?? "";
            promptBuilder.AppendLine(patch.Length > 4000 ? patch[..4000] + "\n...(truncated)" : patch);
        }

        if (repoContext.SpecFiles.Count > 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("### Specification Documents:");
            foreach (var spec in repoContext.SpecFiles.Take(3))
            {
                promptBuilder.AppendLine($"--- Spec: {spec.RelativePath} ---");
                promptBuilder.AppendLine(spec.Content.Length > 2500 ? spec.Content[..2500] + "\n...(truncated)" : spec.Content);
            }
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Return a JSON object with the following schema:");
        promptBuilder.AppendLine("""
        {
          "summaryObservations": "Overall assessment of the Pull Request and existing review feedback",
          "recommendedEvent": "COMMENT | REQUEST_CHANGES | APPROVE",
          "resolvedThreadsNotes": [
            "Confirmed that prior review feedback on X was addressed in the latest diff"
          ],
          "inlineComments": [
            {
              "filePath": "src/Path/File.cs",
              "line": 42,
              "category": "Security | SpecDrift | Refactoring | Testing",
              "severity": "Critical | High | Medium | Low",
              "title": "Concise issue title",
              "body": "Clear actionable review comment explaining the issue on this line",
              "suggestedCode": "Optional replacement code snippet"
            }
          ]
        }
        """);

        try
        {
            var response = await _llmClient.GenerateAsync(new LlmRequest
            {
                Prompt = promptBuilder.ToString(),
                SystemPrompt = "You are a staff engineer auditing a Pull Request and its existing reviews. Output strictly valid JSON matching the requested schema.",
                Model = _model,
                ExpectJson = true
            }, ct);

            var parsed = FindingParser.ParsePrReviewAudit(response.Content, prContext.PrNumber, prContext.HeadSha);
            return parsed with
            {
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                DurationMs = response.DurationMs,
                IsSuccess = true
            };
        }
        catch (Exception ex)
        {
            return new PrReviewAuditResult
            {
                PrNumber = prContext.PrNumber,
                HeadSha = prContext.HeadSha,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
