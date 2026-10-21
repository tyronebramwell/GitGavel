using System.Text;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Agents;

public class SecurityAuditorAgent : ISubagent
{
    private readonly ILlmClient _llmClient;
    private readonly string _model;

    public string Name => "SecurityAuditor";

    public SecurityAuditorAgent(ILlmClient llmClient, string model = "gemini-2.5-pro")
    {
        _llmClient = llmClient;
        _model = model;
    }

    public async Task<SubagentResult> ExecuteAsync(RepoContext context, CancellationToken ct = default)
    {
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Security Auditor Agent. Audit the repository for security vulnerabilities, credential leaks, injection flaws, and authorization defects.");
        promptBuilder.AppendLine("For each vulnerability found, provide OWASP classification, attack vector, and verified secure remediation snippet.");
        if (context.IsPullRequestMode && context.ActivePullRequest != null)
        {
            promptBuilder.AppendLine($"PULL REQUEST REVIEW MODE (PR #{context.ActivePullRequest.PrNumber}: {context.ActivePullRequest.Title}): Audit ONLY the changes introduced in this Pull Request's modified files and diff patches.");
            promptBuilder.AppendLine("ANTI-SCOPE-CREEP RULE: Only report exploitable Critical, High, or Medium security vulnerabilities directly introduced in this PR diff. Do NOT flag test files (*Tests.cs) or theoretical low-severity nits; return [] if no substantive vulnerabilities exist.");
        }

        if (context.SarifFindings.Count > 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("### Static Security Scan Findings (SARIF):");
            foreach (var sarif in context.SarifFindings.Take(15))
            {
                promptBuilder.AppendLine($"- Rule: {sarif.RuleId} | Level: {sarif.Level} | File: {sarif.FilePath}:{sarif.StartLine} | {sarif.Message}");
            }
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Source Code Files Under Audit:");
        foreach (var src in context.SourceFiles)
        {
            promptBuilder.AppendLine($"--- File: {src.RelativePath} ---");
            if (!string.IsNullOrWhiteSpace(src.DiffPatch))
            {
                promptBuilder.AppendLine("PR Diff Patch:");
                promptBuilder.AppendLine(src.DiffPatch.Length > 4000 ? src.DiffPatch[..4000] + "\n...(truncated)" : src.DiffPatch);
            }
            promptBuilder.AppendLine(src.Content.Length > 8000 ? src.Content[..8000] + "\n...(truncated)" : src.Content);
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Return a JSON array of findings with the following schema for each item. For 'references', provide 2-3 links in markdown format (e.g. [Title](url)) pointing to OWASP guidelines, official security docs, or Stack Overflow topics:");
        promptBuilder.AppendLine("""
        [
          {
            "title": "Concise vulnerability title",
            "summary": "Detailed explanation of security risk and exploit vector",
            "category": "Security",
            "severity": "Critical | High | Medium | Low",
            "filePath": "path/to/vulnerable/file",
            "startLine": 15,
            "endLine": 35,
            "owaspCategory": "A01: Broken Access Control | A03: Injection | etc.",
            "attackVector": "How an adversary could exploit this vulnerability",
            "suggestedRemediation": "Safe code snippet and defense-in-depth guidance",
            "references": [
              "[OWASP Top 10 Security Guidance](https://owasp.org/Top10/)",
              "[Microsoft Learn: Secure Coding Guidelines](https://learn.microsoft.com/en-us/dotnet/standard/security/secure-coding-guidelines)",
              "[Stack Overflow: Secure development best practices](https://stackoverflow.com/questions/tagged/security)"
            ]
          }
        ]
        """);

        try
        {
            var response = await _llmClient.GenerateAsync(new LlmRequest
            {
                Prompt = promptBuilder.ToString(),
                SystemPrompt = "You are a principal application security engineer. Identify real vulnerabilities and output strictly a JSON array of findings.",
                Model = _model,
                ExpectJson = true
            }, ct);

            var findings = FindingParser.ParseFindings(response.Content, Name, "Security");

            return new SubagentResult
            {
                AgentName = Name,
                Findings = findings,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                DurationMs = response.DurationMs
            };
        }
        catch (Exception ex)
        {
            return new SubagentResult
            {
                AgentName = Name,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
