using System.Text;
using GitGavel.Configuration;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Agents;

public class SecurityAuditorAgent(ILlmClient llmClient, string model = LlmModelDefaults.DefaultGeminiProModel)
    : BaseLlmAgent(llmClient, LlmModelDefaults.ResolveSubagentModel(model, LlmModelDefaults.DefaultGeminiProModel))
{
    public override string Name => "SecurityAuditor";
    protected override string DefaultCategory => "Security";
    protected override string SystemPrompt => "You are a principal application security engineer. Identify real vulnerabilities and output strictly a JSON array of findings.";

    protected override Task<string> BuildPromptAsync(RepoContext context, CancellationToken ct)
    {
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Security Auditor Agent. Audit the repository for security vulnerabilities, credential leaks, injection flaws, and authorization defects.");
        promptBuilder.AppendLine("For each vulnerability found, provide OWASP classification, attack vector, and verified secure remediation snippet.");

        AgentPromptBuilder.AppendPullRequestGuidance(
            promptBuilder,
            context,
            "ANTI-SCOPE-CREEP RULE: Only report exploitable Critical, High, or Medium security vulnerabilities directly introduced in this PR diff. Do NOT flag test files (*Tests.cs) or theoretical low-severity nits; return [] if no substantive vulnerabilities exist.");

        if ((context.SarifFindings?.Count ?? 0) > 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("### Static Security Scan Findings (SARIF):");
            foreach (var sarif in context.SarifFindings!.Take(15))
            {
                promptBuilder.AppendLine($"- Rule: {sarif.RuleId} | Level: {sarif.Level} | File: {sarif.FilePath}:{sarif.StartLine} | {sarif.Message}");
            }
        }

        AgentPromptBuilder.AppendFilesSection(promptBuilder, context.SourceFiles ?? [], "### Source Code Files Under Audit:");

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

        return Task.FromResult(promptBuilder.ToString());
    }
}
