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
}
