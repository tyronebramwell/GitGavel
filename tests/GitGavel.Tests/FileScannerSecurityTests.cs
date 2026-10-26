using System.Security;
using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.Discovery;

namespace GitGavel.Tests;

public class FileScannerSecurityTests
{
    [Fact]
    public async Task ScanAsync_WhenRootIsFileSystemRoot_ThrowsSecurityException()
    {
        var scanner = new FileScanner();
        var config = new GitGavelConfig();
        var rootDir = Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";

        var act = async () => await scanner.ScanAsync(rootDir, config);
        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*strictly prohibited*");
    }

    [Fact]
    public async Task ScanAsync_WhenSymlinkEscapesRootDirectory_ThrowsSecurityExceptionAndFailsAudit()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "gitgavel_scanner_repo_" + Guid.NewGuid().ToString("N"));
        var externalDir = Path.Combine(Path.GetTempPath(), "gitgavel_scanner_ext_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRepo);
        Directory.CreateDirectory(externalDir);

        try
        {
            var externalSecret = Path.Combine(externalDir, "secret.md");
            await File.WriteAllTextAsync(externalSecret, "# Super Secret Specs");

            var symlinkFile = Path.Combine(tempRepo, "spec_link.md");
            File.CreateSymbolicLink(symlinkFile, externalSecret);

            var config = new GitGavelConfig();
            config.Scope.Specs.Include = ["*.md"];

            var scanner = new FileScanner();

            // When allowExternalPaths is false (default), audit must fail with an explicit SecurityException
            var act = async () => await scanner.ScanAsync(tempRepo, config, allowExternalPaths: false);
            await act.Should().ThrowAsync<SecurityException>()
                .WithMessage("*Symbolic link*outside repository root*");
        }
        finally
        {
            Directory.Delete(tempRepo, true);
            Directory.Delete(externalDir, true);
        }
    }

    [Fact]
    public async Task ScanAsync_WhenSymlinkEscapesRootDirectory_WithExplicitOptIn_AllowsScan()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "gitgavel_scanner_optin_" + Guid.NewGuid().ToString("N"));
        var externalDir = Path.Combine(Path.GetTempPath(), "gitgavel_scanner_optin_ext_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRepo);
        Directory.CreateDirectory(externalDir);

        try
        {
            var externalSecret = Path.Combine(externalDir, "spec.md");
            await File.WriteAllTextAsync(externalSecret, "# Allowed Spec");

            var symlinkFile = Path.Combine(tempRepo, "spec_link.md");
            File.CreateSymbolicLink(symlinkFile, externalSecret);

            var config = new GitGavelConfig();
            config.Scope.Specs.Include = ["*.md"];

            var scanner = new FileScanner();

            // When allowExternalPaths is true, scan should succeed
            var context = await scanner.ScanAsync(tempRepo, config, allowExternalPaths: true);
            context.SpecFiles.Should().HaveCount(1);
            context.SpecFiles[0].Content.Should().Contain("# Allowed Spec");
        }
        finally
        {
            Directory.Delete(tempRepo, true);
            Directory.Delete(externalDir, true);
        }
    }

    [Fact]
    public async Task ScanAsync_WhenSarifPathOutsideRoot_WithoutAllowExternal_ThrowsSecurityException()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "gitgavel_sarif_repo_" + Guid.NewGuid().ToString("N"));
        var externalDir = Path.Combine(Path.GetTempPath(), "gitgavel_sarif_ext_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRepo);
        Directory.CreateDirectory(externalDir);

        try
        {
            var externalSarif = Path.Combine(externalDir, "report.sarif");
            await File.WriteAllTextAsync(externalSarif, "{}");

            var config = new GitGavelConfig();
            config.Subagents.Security.Enabled = true;
            config.Subagents.Security.SarifReports = [externalSarif];

            var scanner = new FileScanner();

            var act = async () => await scanner.ScanAsync(tempRepo, config, allowExternalPaths: false);
            await act.Should().ThrowAsync<SecurityException>()
                .WithMessage("*outside repository root*");
        }
        finally
        {
            Directory.Delete(tempRepo, true);
            Directory.Delete(externalDir, true);
        }
    }
}
