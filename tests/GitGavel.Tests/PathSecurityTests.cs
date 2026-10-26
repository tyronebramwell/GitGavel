using System.Security;
using FluentAssertions;
using GitGavel.Configuration;

namespace GitGavel.Tests;

public class PathSecurityTests
{
    [Fact]
    public void IsWithinDirectory_WhenInsideDirectory_ReturnsTrue()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "gitgavel_root_" + Guid.NewGuid().ToString("N"));
        var subFile = Path.Combine(tempRoot, "src", "Sub", "File.cs");

        PathSecurity.IsWithinDirectory(subFile, tempRoot).Should().BeTrue();
    }

    [Fact]
    public void IsWithinDirectory_WhenExactSameDirectory_ReturnsTrue()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "gitgavel_root_" + Guid.NewGuid().ToString("N"));

        PathSecurity.IsWithinDirectory(tempRoot, tempRoot).Should().BeTrue();
    }

    [Fact]
    public void IsWithinDirectory_WhenTraversalEscapesRoot_ReturnsFalse()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "gitgavel_root_" + Guid.NewGuid().ToString("N"));
        var escapedPath = Path.Combine(tempRoot, "..", "outside.txt");

        PathSecurity.IsWithinDirectory(escapedPath, tempRoot).Should().BeFalse();
    }

    [Fact]
    public void IsWithinDirectory_WhenPrefixCollidesButDifferentDirectory_ReturnsFalse()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "gitgavel_root");
        var siblingDir = Path.Combine(Path.GetTempPath(), "gitgavel_root_evil", "file.txt");

        PathSecurity.IsWithinDirectory(siblingDir, tempRoot).Should().BeFalse();
    }

    [Fact]
    public void IsWithinDirectory_WhenCandidateOrRootNullOrEmpty_ReturnsFalse()
    {
        PathSecurity.IsWithinDirectory("", "/valid/path").Should().BeFalse();
        PathSecurity.IsWithinDirectory("/valid/path", "").Should().BeFalse();
        PathSecurity.IsWithinDirectory(" ", " ").Should().BeFalse();
    }

    [Theory]
    [InlineData("specs/api.md", true)]
    [InlineData("src/**/*.cs", true)]
    [InlineData("reports/semgrep.sarif", true)]
    [InlineData("./specs/api.md", true)]
    [InlineData("/etc/passwd", false)]
    [InlineData("../etc/passwd", false)]
    [InlineData("specs/../../etc/passwd", false)]
    [InlineData("C:\\Windows\\System32", false)]
    [InlineData("C:file.txt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeRelativePath_ValidatesCorrectly(string? path, bool expected)
    {
        PathSecurity.IsSafeRelativePath(path).Should().Be(expected);
    }

    [Fact]
    public void ResolveAndVerifyWithinRoot_WhenWithinRoot_ReturnsCanonicalPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_resolve_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file.txt");
            File.WriteAllText(filePath, "test");

            var resolved = PathSecurity.ResolveAndVerifyWithinRoot("file.txt", tempDir);
            resolved.Should().Be(Path.GetFullPath(filePath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveAndVerifyWithinRoot_WhenOutsideRootWithoutAllowExternal_ThrowsSecurityException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_resolve_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var act = () => PathSecurity.ResolveAndVerifyWithinRoot("../outside.txt", tempDir, allowExternal: false);
            act.Should().Throw<SecurityException>()
                .WithMessage("*outside repository root*");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveAndVerifyWithinRoot_WhenOutsideRootWithAllowExternal_ReturnsPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_resolve_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var resolved = PathSecurity.ResolveAndVerifyWithinRoot("../outside.txt", tempDir, allowExternal: true);
            resolved.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveAndVerifyWithinRoot_WhenSymlinkPointsOutside_ThrowsSecurityException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_symlink_test_" + Guid.NewGuid().ToString("N"));
        var externalDir = Path.Combine(Path.GetTempPath(), "gitgavel_symlink_ext_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(externalDir);

        try
        {
            var externalFile = Path.Combine(externalDir, "secret.txt");
            File.WriteAllText(externalFile, "super-secret");

            var linkFile = Path.Combine(tempDir, "link_to_secret.txt");
            File.CreateSymbolicLink(linkFile, externalFile);

            var act = () => PathSecurity.ResolveAndVerifyWithinRoot(linkFile, tempDir, allowExternal: false);
            act.Should().Throw<SecurityException>()
                .WithMessage("*Symbolic link*points to target*outside repository root*");
        }
        finally
        {
            Directory.Delete(tempDir, true);
            Directory.Delete(externalDir, true);
        }
    }

    [Fact]
    public void ResolveAndVerifyWithinRoot_WhenSymlinkPointsInside_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_symlink_internal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var internalFile = Path.Combine(tempDir, "internal.txt");
            File.WriteAllText(internalFile, "hello");

            var linkFile = Path.Combine(tempDir, "link_to_internal.txt");
            File.CreateSymbolicLink(linkFile, internalFile);

            var resolved = PathSecurity.ResolveAndVerifyWithinRoot(linkFile, tempDir, allowExternal: false);
            resolved.Should().Be(Path.GetFullPath(internalFile));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ValidateRootDirectory_WhenValidDirectory_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_valid_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var act = () => PathSecurity.ValidateRootDirectory(tempDir);
            act.Should().NotThrow();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ValidateRootDirectory_WhenDirectoryNotFound_ThrowsDirectoryNotFoundException()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "gitgavel_missing_" + Guid.NewGuid().ToString("N"));
        var act = () => PathSecurity.ValidateRootDirectory(nonExistent);
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void ValidateRootDirectory_WhenFilesystemRoot_ThrowsSecurityException()
    {
        var rootDir = Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";
        var act = () => PathSecurity.ValidateRootDirectory(rootDir);
        act.Should().Throw<SecurityException>()
            .WithMessage("*strictly prohibited*");
    }
}
