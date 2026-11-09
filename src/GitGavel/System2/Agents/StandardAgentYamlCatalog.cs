using GitGavel.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitGavel.System2.Agents;

public static class StandardAgentYamlCatalog
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public const string SpecDriftYaml = """
        version: 1
        id: "specDrift"
        name: "SpecDriftAgent"
        category: "SpecDrift"
        enabled: true
        provider: "gemini"
        model: "gemini-2.5-pro"
        scope:
          requireSpecs: true
          includeSpecs: true
          includeTests: false
          includeAst: true
          includeAstComplexity: false
          sourceHeading: "### Source Code Implementation:"
        gate:
          engine: "deterministic-then-jev"
          jev:
            primitive: "noul"
            question: "Do the changes modify specifications, API documentation, OpenAPI schemas, or public interface contracts?"
            minConfidence: 0.50
        systemPrompt: "You are an expert software architect auditing specification compliance. Return strictly a JSON array of findings."
        userPromptIntro: |
          You are the Spec Drift Agent. Compare the repository specifications/documentation against the actual source code implementation.
          Identify any missing endpoints, divergent parameters, unimplemented requirements, or schema drift.
          IMPORTANT: Include documentation drift (e.g., README.md, SPECIFICATION.md, docs/) where documented CLI commands, options, features, architecture, or configurations are missing, outdated, or contradict the actual codebase.
          IMPORTANT: Do NOT report compliant implementations or positive alignments where no remediation is needed; return [] if all changes match the specification.
        antiScopeCreepRule: "ANTI-SCOPE-CREEP RULE: Only report Critical, High, or Medium severity specification contradictions introduced by this PR. Do NOT report Low-severity nits, test file details, or compliant changes; return [] if no substantive spec drift exists."
        jsonSchemaInstructions: |
          Return a JSON array of findings with the following schema for each item. For 'references', provide 2-3 links in markdown format (e.g. [Title](url)) pointing to official framework/API docs, Stack Overflow topics, or research articles:
                  [
                    {
                      "title": "Concise issue title",
                      "summary": "Clear summary of divergence between spec and code",
                      "category": "SpecDrift",
                      "severity": "Critical | High | Medium | Low",
                      "filePath": "path/to/affected/file",
                      "startLine": 10,
                      "endLine": 30,
                      "suggestedRemediation": "Concrete code snippet or action plan to align code with spec",
                      "details": "Specification section referenced vs actual implementation",
                      "references": [
                        "[Microsoft Learn: REST API Design Guidelines](https://learn.microsoft.com/en-us/azure/architecture/best-practices/api-design)",
                        "[Stack Overflow: Managing OpenAPI contract drift](https://stackoverflow.com/questions/tagged/openapi)"
                      ]
                    }
                  ]
        """;

    public const string SecurityYaml = """
        version: 1
        id: "security"
        name: "SecurityAuditor"
        category: "Security"
        enabled: true
        provider: "gemini"
        model: "gemini-2.5-pro"
        scope:
          includeSpecs: false
          includeTests: false
          includeAst: false
          includeSarif: true
          sourceHeading: "### Source Code Files Under Audit:"
        gate:
          engine: "deterministic-then-jev"
          jev:
            primitive: "noul"
            question: "Do the changes touch security-critical code, authentication, authorization, cryptography, data access, network boundaries, or third-party dependencies?"
            minConfidence: 0.50
        systemPrompt: "You are a principal application security engineer. Identify real vulnerabilities and output strictly a JSON array of findings."
        userPromptIntro: |
          You are the Security Auditor Agent. Audit the repository for security vulnerabilities, credential leaks, injection flaws, and authorization defects.
          For each vulnerability found, provide OWASP classification, attack vector, and verified secure remediation snippet.
        antiScopeCreepRule: "ANTI-SCOPE-CREEP RULE: Only report exploitable Critical, High, or Medium security vulnerabilities directly introduced in this PR diff. Do NOT flag test files (*Tests.cs) or theoretical low-severity nits; return [] if no substantive vulnerabilities exist."
        jsonSchemaInstructions: |
          Return a JSON array of findings with the following schema for each item. For 'references', provide 2-3 links in markdown format (e.g. [Title](url)) pointing to OWASP guidelines, official security docs, or Stack Overflow topics:
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
        """;

    public const string ReliabilityYaml = """
        version: 1
        id: "reliability"
        name: "ReliabilityAgent"
        category: "Reliability"
        enabled: true
        provider: "gemini"
        model: "gemini-3.1-pro-preview"
        scope:
          includeSpecs: false
          includeTests: false
          includeAst: false
          includeComplexityHotspots: true
          hotspotsHeading: "### High-Complexity Hotspots (Inspect for concurrency/branching edge cases):"
          hotspotItemPrefix: "- File:"
          excludeTestSourceFiles: true
          prioritizeHotspots: true
          maxSourceFiles: 20
          omitDiffContentInPrMode: true
          sourceHeading: "### Source Code Under Audit:"
        gate:
          engine: "deterministic-then-jev"
          jev:
            primitive: "noul"
            question: "Do the changes modify or introduce asynchronous execution, concurrency, task scheduling, exception handling, resource disposal, or performance-critical loops?"
            minConfidence: 0.50
        systemPrompt: "You are a principal software reliability and performance engineer. Output strictly a JSON array of findings."
        userPromptIntro: |
          You are the Reliability & Performance Agent. Audit production code for functional bugs, performance bottlenecks, performance improvements, provable logic defects, runtime crash paths, concurrency/async hazards, and resource leaks.
          For each defect found, identify the root cause, exact failure mode or performance bottleneck, and provide a verified, drop-in remediation code snippet.
        antiScopeCreepRule: "ANTI-SCOPE-CREEP RULE: Only report substantive functional bugs, crash paths, concurrency defects, resource leaks, or performance bottlenecks directly introduced or touched in this PR diff. Do NOT flag test files (*Tests.cs) or low-severity aesthetic nits; return [] if no defects exist."
        additionalPromptBody: |
          ### Core Reliability & Performance Categories to Inspect:
          1. **Provable Logic Defects & Bugs**: Off-by-one errors, inverted boolean conditions, state overwrite bugs in loops, broken pagination, silent truncation, and incorrect edge-case handling.
          2. **Crash Paths & Exception Safety**: Unhandled exceptions, null/empty dereferences, unsafe type casts, division-by-zero, out-of-range indexing, and missing input/boundary validations.
          3. **Concurrency & Async Defects**: Race conditions on shared state, deadlocks, synchronous blocking on asynchronous calls (e.g. Task.Result, .Wait()), async void methods, unawaited tasks, and missing CancellationToken propagation.
          4. **Resource Leaks**: Undisposed IDisposable/IAsyncDisposable objects (Stream, HttpClient, database connections, file handles, processes, sockets), and unbounded memory/cache growth.
          5. **Performance Issues & Improvements**: O(N^2) or N+1 API/database loops, redundant allocations or serializations in hot paths, unbounded network/file reads, and missing indexing or caching opportunities.
        jsonSchemaInstructions: |
          Return a JSON array of findings with the following schema for each item. For 'references', provide 2-3 links in markdown format (e.g. [Title](url)) pointing to official docs, best practices, or Stack Overflow topics:
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
        """;

    public const string RefactoringYaml = """
        version: 1
        id: "refactoring"
        name: "RefactoringAgent"
        category: "Refactoring"
        enabled: true
        provider: "gemini"
        model: "gemini-2.5-flash"
        scope:
          includeSpecs: false
          includeTests: false
          includeAst: true
          includeAstComplexity: true
          includeComplexityHotspots: true
          hotspotsHeading: "### Detected Complexity Hotspots:"
          hotspotItemPrefix: "- Hotspot:"
          prioritizeHotspots: true
          sourceHeading: "### Source Code Implementation:"
        gate:
          engine: "deterministic-then-jev"
          jev:
            primitive: "noul"
            question: "Do the modified files exhibit significant structural complexity churn, high cyclomatic complexity, or large method modifications?"
            minConfidence: 0.50
        systemPrompt: "You are a software architect specializing in clean code and refactoring. Output strictly a JSON array of findings."
        userPromptIntro: |
          You are the Architecture & Refactoring Agent. Flag real architectural debt, tight coupling across architectural boundaries, large code duplication, and severe maintainability issues.
          NOTE: Focus only on high-confidence, actionable architectural debt and anti-patterns. The project uses lightweight multi-language heuristics, standard DI defaults, and modular command patterns by architectural design; do not flag these intentional design decisions as architectural debt.
        antiScopeCreepRule: "ANTI-SCOPE-CREEP RULE: Only report High or Medium severity architectural defects introduced in this PR. Do NOT flag Low-severity nits, do NOT nitpick test files (*Tests.cs), and do NOT suggest extracting extra interfaces/services for newly added helper methods; return [] if the PR changes are clean."
        jsonSchemaInstructions: |
          Return a JSON array of findings with the following schema for each item. For 'references', provide 2-3 links in markdown format (e.g. [Title](url)) pointing to refactoring guides, architecture docs, or Stack Overflow topics:
                  [
                    {
                      "title": "Concise refactoring title",
                      "summary": "Clear explanation of architectural debt or coupling issue",
                      "category": "Refactoring",
                      "severity": "High | Medium | Low",
                      "filePath": "path/to/target/file",
                      "startLine": 20,
                      "endLine": 60,
                      "suggestedRemediation": "Concrete refactoring proposal or design pattern recommendation",
                      "details": "Impact on maintainability and testability",
                      "references": [
                        "[Refactoring Guru: Code Smells and Refactoring Techniques](https://refactoring.guru/refactoring)",
                        "[Microsoft Learn: Architectural Principles and Clean Architecture](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/architectural-principles)",
                        "[Stack Overflow: Decoupling and dependency injection patterns](https://stackoverflow.com/questions/tagged/dependency-injection)"
                      ]
                    }
                  ]
        """;

    public const string TestSentinelYaml = """
        version: 1
        id: "tests"
        name: "TestSentinelAgent"
        category: "Testing"
        enabled: true
        provider: "gemini"
        model: "gemini-2.5-pro"
        tiers:
          - "unit"
          - "integration"
          - "e2e"
        scope:
          requireSourceOrTests: true
          includeSpecs: false
          includeTests: true
          includeAst: true
          includeAstComplexity: true
          astHeading: "### Production Symbol & Complexity Map:"
          sourceHeading: "### Production Source Files Under Audit:"
          sourceLabelPrefix: "Source File"
        gate:
          engine: "deterministic-then-jev"
          jev:
            primitive: "noul"
            question: "Were new production features or branching logic introduced that require automated unit, integration, or E2E test verification?"
            minConfidence: 0.50
        systemPrompt: "You are a principal SDET and test architecture specialist auditing automated test coverage and quality. Output strictly a JSON array of findings."
        userPromptIntro: "You are the Test Sentinel Agent. Audit the repository's automated testing suite across the test pyramid."
        antiScopeCreepRule: "ANTI-SCOPE-CREEP RULE: Only report High or Medium severity missing tests for completely untested production logic introduced in this PR. Check the 'Existing Automated Test Suite Symbol Map' below before reporting any MissingTest finding—if unit or integration test methods already exist in the symbol map or test files for the production class/method (even when large test file bodies are truncated), return []. Do NOT request tests for trivial constructors, getters, or tests for existing tests."
        additionalPromptBody: |
          Identify:
          1. Missing Test Detection (MissingTest): critical production classes, public methods, business rules, or error paths lacking Unit, Integration, or E2E coverage.
          2. Extra Scenarios & Boundary Coverage (ExtraScenario): omitted edge cases, boundary conditions, invalid inputs, concurrency/race hazards, and negative assertions.
          3. Test Quality & Maintainability (TestQuality): brittle assertions, tautological mocks, flaky patterns (e.g., arbitrary Thread.Sleep), or poor fixture isolation.
        jsonSchemaInstructions: |
          Return a JSON array of findings with the following schema for each item:
                  [
                    {
                      "title": "Concise test gap or test quality finding title",
                      "summary": "Clear explanation of the untested logic, missing boundary scenario, or test quality defect",
                      "category": "Testing",
                      "severity": "High | Medium | Low",
                      "testTier": "Unit | Integration | E2E",
                      "testDefectType": "MissingTest | ExtraScenario | TestQuality",
                      "filePath": "path/to/production/or/test/file",
                      "startLine": 10,
                      "endLine": 40,
                      "suggestedRemediation": "Executable test implementation blueprint (e.g. xUnit/NUnit/Playwright snippet)",
                      "details": "Why this scenario is critical for test pyramid reliability",
                      "references": [
                        "[Microsoft Learn: Unit testing best practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)",
                        "[Martin Fowler: Practical Test Pyramid](https://martinfowler.com/articles/practical-test-pyramid.html)"
                      ]
                    }
                  ]
        """;

    public const string IssueResolutionYaml = """
        version: 1
        id: "issueResolution"
        name: "IssueResolutionAuditor"
        category: "IssueResolution"
        enabled: true
        provider: "gemini"
        model: "gemini-2.5-flash"
        systemPrompt: "You are a senior maintainer verifying whether open GitHub issues have been completed in recent commits. Return strictly a JSON array of resolution assessments."
        userPromptIntro: |
          You are the Issue Resolution Agent. Audit the following open GitHub issues against the current codebase and recent git commit history.
          Identify which open issues have been completed/fixed, explain concisely how each issue was fixed, and list the specific git commit SHA(s) and commit messages that resolved them.
        jsonSchemaInstructions: |
          Return a JSON array of resolved issues with the following schema:
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
        """;

    private static readonly IReadOnlyDictionary<string, (string FileName, string Yaml)> RawCatalog =
        new Dictionary<string, (string FileName, string Yaml)>(StringComparer.OrdinalIgnoreCase)
        {
            ["specDrift"] = ("spec-drift.agent.yml", SpecDriftYaml),
            ["SpecDriftAgent"] = ("spec-drift.agent.yml", SpecDriftYaml),
            ["security"] = ("security.agent.yml", SecurityYaml),
            ["SecurityAuditor"] = ("security.agent.yml", SecurityYaml),
            ["reliability"] = ("reliability.agent.yml", ReliabilityYaml),
            ["ReliabilityAgent"] = ("reliability.agent.yml", ReliabilityYaml),
            ["refactoring"] = ("refactoring.agent.yml", RefactoringYaml),
            ["RefactoringAgent"] = ("refactoring.agent.yml", RefactoringYaml),
            ["tests"] = ("test-sentinel.agent.yml", TestSentinelYaml),
            ["TestSentinelAgent"] = ("test-sentinel.agent.yml", TestSentinelYaml),
            ["issueResolution"] = ("issue-resolution.agent.yml", IssueResolutionYaml),
            ["IssueResolutionAuditor"] = ("issue-resolution.agent.yml", IssueResolutionYaml)
        };

    public static AgentDefinition GetStandardDefinition(string idOrName)
    {
        if (!RawCatalog.TryGetValue(idOrName, out var entry))
        {
            throw new ArgumentException($"Unknown standard agent '{idOrName}'.", nameof(idOrName));
        }

        return ParseFromYaml(entry.Yaml);
    }

    public static IReadOnlyList<(string FileName, string Yaml, AgentDefinition Definition)> GetAllStandardDefinitions()
    {
        var keys = new[] { "specDrift", "security", "reliability", "refactoring", "tests", "issueResolution" };
        return keys
            .Select(k => RawCatalog[k])
            .Select(e => (e.FileName, e.Yaml, ParseFromYaml(e.Yaml)))
            .ToList();
    }

    public static AgentDefinition ParseFromYaml(string yamlContent)
    {
        return Deserializer.Deserialize<AgentDefinition>(yamlContent) ?? new AgentDefinition();
    }

    public static string SerializeToYaml(AgentDefinition definition)
    {
        return Serializer.Serialize(definition);
    }
}
