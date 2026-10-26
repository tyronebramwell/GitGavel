using System.Text;
using GitGavel.Discovery;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Agents;

public class ReliabilityAgent : ISubagent
{
    private readonly ILlmClient _llmClient;
    private readonly string _model;

    public string Name => "ReliabilityAgent";

    public ReliabilityAgent(ILlmClient llmClient, string model = "gemini-3.1-pro-preview")
    {
        _llmClient = llmClient;
        _model = model;
    }

    public async Task<SubagentResult> ExecuteAsync(RepoContext context, CancellationToken ct = default)
    {
        var summaries = context.AstSummaries.Count > 0 ? context.AstSummaries : AstAnalyzer.AnalyzeAll(context.SourceFiles);
        var hotspots = summaries.Where(s => s.IsComplexityHotspot).ToList();

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("You are the Reliability & Performance Agent. Audit production code for functional bugs, performance bottlenecks, performance improvements, provable logic defects, runtime crash paths, concurrency/async hazards, and resource leaks.");
        promptBuilder.AppendLine("For each defect found, identify the root cause, exact failure mode or performance bottleneck, and provide a verified, drop-in remediation code snippet.");

        if (context.IsPullRequestMode && context.ActivePullRequest != null)
        {
            promptBuilder.AppendLine($"PULL REQUEST REVIEW MODE (PR #{context.ActivePullRequest.PrNumber}: {context.ActivePullRequest.Title}): Audit ONLY the changes introduced in this Pull Request's modified files and diff patches.");
            promptBuilder.AppendLine("ANTI-SCOPE-CREEP RULE: Only report substantive functional bugs, crash paths, concurrency defects, resource leaks, or performance bottlenecks directly introduced or touched in this PR diff. Do NOT flag test files (*Tests.cs) or low-severity aesthetic nits; return [] if no defects exist.");
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Core Reliability & Performance Categories to Inspect:");
        promptBuilder.AppendLine("1. **Provable Logic Defects & Bugs**: Off-by-one errors, inverted boolean conditions, state overwrite bugs in loops, broken pagination, silent truncation, and incorrect edge-case handling.");
        promptBuilder.AppendLine("2. **Crash Paths & Exception Safety**: Unhandled exceptions, null/empty dereferences, unsafe type casts, division-by-zero, out-of-range indexing, and missing input/boundary validations.");
        promptBuilder.AppendLine("3. **Concurrency & Async Defects**: Race conditions on shared state, deadlocks, synchronous blocking on asynchronous calls (e.g. Task.Result, .Wait()), async void methods, unawaited tasks, and missing CancellationToken propagation.");
        promptBuilder.AppendLine("4. **Resource Leaks**: Undisposed IDisposable/IAsyncDisposable objects (Stream, HttpClient, database connections, file handles, processes, sockets), and unbounded memory/cache growth.");
        promptBuilder.AppendLine("5. **Performance Issues & Improvements**: O(N^2) or N+1 API/database loops, redundant allocations or serializations in hot paths, unbounded network/file reads, and missing indexing or caching opportunities.");

        if (hotspots.Count > 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("### High-Complexity Hotspots (Inspect for concurrency/branching edge cases):");
            foreach (var spot in hotspots)
            {
                promptBuilder.AppendLine($"- File: `{spot.RelativePath}` (Complexity score: {spot.TotalComplexityScore}, Symbols: {spot.Symbols.Count})");
            }
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("### Source Code Under Audit:");
        var hotspotPaths = new HashSet<string>(hotspots.Select(h => h.RelativePath));
        var prioritizedFiles = context.SourceFiles
            .Where(f => !f.IsTest)
            .OrderByDescending(f => hotspotPaths.Contains(f.RelativePath))
            .ThenByDescending(f => f.LineCount);

        foreach (var src in prioritizedFiles)
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
        promptBuilder.AppendLine("Return a JSON array of findings with the following schema for each item. For 'references', provide 2-3 links in markdown format (e.g. [Title](url)) pointing to official docs, best practices, or Stack Overflow topics:");
        promptBuilder.AppendLine("""
        [
          {
            "title": "Concise defect title",
            "summary": "Detailed explanation of bug, crash path, concurrency hazard, or performance bottleneck",
            "category": "Reliability",
            "severity": "Critical | High | Medium | Low",
            "defectType": "LogicDefect | CrashPath | Concurrency | ResourceLeak | Performance",
            "filePath": "path/to/target/file",
            "startLine": 15,
            "endLine": 35,
            "details": "Root cause, failure mechanism, or algorithmic complexity analysis",
            "suggestedRemediation": "Drop-in remediation code snippet fixing the issue",
            "references": [
              "[Microsoft Learn: Reliability and Exception Handling Best Practices](https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions)",
              "[Microsoft Learn: Async/Await Best Practices and Concurrency](https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)",
              "[Stack Overflow: Performance optimization and resource management in .NET](https://stackoverflow.com/questions/tagged/performance)"
            ]
          }
        ]
        """);

        try
        {
            var response = await _llmClient.GenerateAsync(new LlmRequest
            {
                Prompt = promptBuilder.ToString(),
                SystemPrompt = "You are a principal software reliability and performance engineer. Output strictly a JSON array of findings.",
                Model = _model,
                ExpectJson = true
            }, ct);

            var findings = FindingParser.ParseFindings(response.Content, Name, "Reliability");

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
