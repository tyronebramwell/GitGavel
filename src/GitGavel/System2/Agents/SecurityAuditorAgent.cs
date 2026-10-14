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

    public SecurityAuditorAgent(ILlmClient llmClient, string model = "gemini-1.5-pro")
    {
        _llmClient = llmClient;
        _model = model;
    }

    public async Task<SubagentResult> ExecuteAsync(RepoContext context, CancellationToken ct = default)
    {
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Security Auditor Agent. Audit the repository for security vulnerabilities, credential leaks, injection flaws, and authorization defects.");
        promptBuilder.AppendLine("For each vulnerability found, provide OWASP classification, attack vector, and verified secure remediation snippet.");

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
        foreach (var src in context.SourceFiles.Take(12))
        {
            promptBuilder.AppendLine($"--- File: {src.RelativePath} ---");
            promptBuilder.AppendLine(src.Content.Length > 3000 ? src.Content[..3000] + "\n...(truncated)" : src.Content);
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Return a JSON array of findings with the following schema for each item:");
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
            "suggestedRemediation": "Safe code snippet and defense-in-depth guidance"
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
