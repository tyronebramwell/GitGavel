using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.Discovery;

namespace GitGavel.Tests;

public class DiscoveryTests
{

    [Fact]
    public void SarifParser_WithValidSarifJson_ParsesFindings()
    {
        var sarif = """
        {
          "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Semgrep" } },
              "results": [
                {
                  "ruleId": "csharp.lang.security.sql-injection",
                  "level": "error",
                  "message": { "text": "Potential SQL injection in RawSql query" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "Services/UserService.cs" },
                        "region": { "startLine": 45, "endLine": 48 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var findings = SarifParser.Parse(sarif);

        findings.Should().HaveCount(1);
        var f = findings[0];
        f.RuleId.Should().Be("csharp.lang.security.sql-injection");
        f.Level.Should().Be("error");
        f.FilePath.Should().Be("Services/UserService.cs");
        f.StartLine.Should().Be(45);
        f.EndLine.Should().Be(48);
    }

    [Fact]
    public void AstAnalyzer_DetectsSymbolsAndComplexity()
    {
        var code = """
        public class OrderProcessor
        {
            public void Process(Order order)
            {
                if (order == null) return;
                if (order.IsPaid)
                {
                    while (order.HasPendingItems())
                    {
                        // process
                    }
                }
            }
        }
        """;

        var file = new ScannedFile("Services/OrderProcessor.cs", "/mock/Services/OrderProcessor.cs", 15, code.Length, code, false);
        var summary = AstAnalyzer.Analyze(file);

        summary.RelativePath.Should().Be("Services/OrderProcessor.cs");
        summary.Symbols.Should().Contain(s => s.Kind == "class" && s.Name == "OrderProcessor");
        summary.TotalComplexityScore.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FileScanner_ScansDirectoryMatchingInclusionsAndExclusions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var docsDir = Path.Combine(tempDir, "docs");
            var srcDir = Path.Combine(tempDir, "src");
            var binDir = Path.Combine(tempDir, "src", "bin");
            var testsDir = Path.Combine(tempDir, "tests");
            Directory.CreateDirectory(docsDir);
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(testsDir);

            await File.WriteAllTextAsync(Path.Combine(docsDir, "spec.md"), "# API Spec");
            await File.WriteAllTextAsync(Path.Combine(srcDir, "App.cs"), "public class App {}");
            await File.WriteAllTextAsync(Path.Combine(binDir, "Ignored.cs"), "public class Ignored {}");
            await File.WriteAllTextAsync(Path.Combine(testsDir, "AppTests.cs"), "public class AppTests {}");

            var config = new GitGavelConfig();
            config.Scope.Specs.Include = ["docs/**/*.md"];
            config.Scope.Source.Include = ["src/**/*.cs"];
            config.Scope.Source.Exclude = ["**/bin/**"];
            config.Scope.Tests.Include = ["tests/**/*.cs"];

            var scanner = new FileScanner();
            var context = await scanner.ScanAsync(tempDir, config);

            context.SpecFiles.Should().HaveCount(1);
            context.SpecFiles[0].RelativePath.Should().Contain("spec.md");

            context.SourceFiles.Should().HaveCount(1);
            context.SourceFiles[0].RelativePath.Should().Contain("App.cs");
            context.SourceFiles.Should().NotContain(f => f.RelativePath.Contains("bin"));

            context.TestFiles.Should().HaveCount(1);
            context.TestFiles[0].RelativePath.Should().Contain("AppTests.cs");
            context.TestFiles[0].IsTest.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RepoContext_ScopeToPullRequest_FiltersSourceAndTestFilesToPrChangedFiles()
    {
        var repoContext = new RepoContext
        {
            RootDirectory = "/mock/repo",
            SpecFiles = [new ScannedFile("SPECIFICATION.md", "/mock/repo/SPECIFICATION.md", 100, 2000, "# Spec", true)],
            SourceFiles =
            [
                new ScannedFile("src/ChangedService.cs", "/mock/repo/src/ChangedService.cs", 50, 1000, "public class ChangedService {}", false),
                new ScannedFile("src/UnchangedService.cs", "/mock/repo/src/UnchangedService.cs", 80, 1500, "public class UnchangedService {}", false)
            ],
            TestFiles =
            [
                new ScannedFile("tests/ChangedServiceTests.cs", "/mock/repo/tests/ChangedServiceTests.cs", 30, 600, "public class ChangedServiceTests {}", false, true),
                new ScannedFile("tests/UnchangedServiceTests.cs", "/mock/repo/tests/UnchangedServiceTests.cs", 40, 800, "public class UnchangedServiceTests {}", false, true)
            ],
            SarifFindings =
            [
                new SarifFinding("rule-1", "Alert on changed file", "error", "src/ChangedService.cs", 10, 12),
                new SarifFinding("rule-2", "Alert on unchanged file", "error", "src/UnchangedService.cs", 20, 25)
            ]
        };

        var prContext = new GitGavel.System2.Models.PullRequestContext
        {
            PrNumber = 42,
            Title = "Update ChangedService",
            HeadSha = "abc1234",
            ChangedFiles =
            [
                new GitGavel.System2.Models.PullRequestChangedFile("src/ChangedService.cs", "modified", 10, 2, "@@ -1,5 +1,13 @@"),
                new GitGavel.System2.Models.PullRequestChangedFile("tests/ChangedServiceTests.cs", "modified", 5, 1, "@@ -1,3 +1,7 @@")
            ]
        };

        var scoped = repoContext.ScopeToPullRequest(prContext);

        scoped.IsPullRequestMode.Should().BeTrue();
        scoped.ActivePullRequest!.PrNumber.Should().Be(42);
        scoped.SpecFiles.Should().HaveCount(1);
        scoped.SourceFiles.Should().ContainSingle(f => f.RelativePath == "src/ChangedService.cs" && f.DiffPatch == "@@ -1,5 +1,13 @@");
        scoped.TestFiles.Should().ContainSingle(f => f.RelativePath == "tests/ChangedServiceTests.cs" && f.DiffPatch == "@@ -1,3 +1,7 @@");
        scoped.SarifFindings.Should().ContainSingle(s => s.FilePath == "src/ChangedService.cs");
    }
}
