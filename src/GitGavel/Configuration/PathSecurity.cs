using System.Security;

namespace GitGavel.Configuration;

public static class PathSecurity
{
    private static readonly bool IsWindows = OperatingSystem.IsWindows();
    private static readonly StringComparison PathComparison = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Checks whether candidatePath is strictly inside or equal to rootDirectory.
    /// </summary>
    public static bool IsWithinDirectory(string candidatePath, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }

        var candidateFull = Path.GetFullPath(candidatePath);
        var rootFull = Path.GetFullPath(rootDirectory);

        if (string.Equals(candidateFull, rootFull, PathComparison))
        {
            return true;
        }

        var rootWithSep = EnsureTrailingSeparator(rootFull);
        return candidateFull.StartsWith(rootWithSep, PathComparison);
    }

    /// <summary>
    /// Verifies that a relative path does not attempt path traversal (e.g. '..') and is not rooted.
    /// </summary>
    public static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == ".." || segment.Contains(':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the canonical path of a file, following and validating symbolic links.
    /// Throws SecurityException if the file or its symlink target escapes rootDirectory without explicit opt-in.
    /// </summary>
    public static string ResolveAndVerifyWithinRoot(string path, string rootDirectory, bool allowExternal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullPath = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(fullRoot, path));

        // 1. Check direct path confinement
        if (!allowExternal && !IsWithinDirectory(fullPath, fullRoot))
        {
            throw new SecurityException($"Path traversal detected: Path '{path}' resolves to '{fullPath}' which is outside repository root '{fullRoot}'.");
        }

        // 2. If the file exists, inspect for symbolic links
        if (File.Exists(fullPath))
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.LinkTarget != null)
            {
                var targetInfo = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (targetInfo != null)
                {
                    var targetFull = Path.GetFullPath(targetInfo.FullName);
                    if (!allowExternal && !IsWithinDirectory(targetFull, fullRoot))
                    {
                        throw new SecurityException($"Path traversal detected: Symbolic link '{path}' points to target '{targetFull}' outside repository root '{fullRoot}'.");
                    }
                    return targetFull;
                }
            }
        }
        else if (Directory.Exists(fullPath))
        {
            var dirInfo = new DirectoryInfo(fullPath);
            if (dirInfo.LinkTarget != null)
            {
                var targetInfo = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (targetInfo != null)
                {
                    var targetFull = Path.GetFullPath(targetInfo.FullName);
                    if (!allowExternal && !IsWithinDirectory(targetFull, fullRoot))
                    {
                        throw new SecurityException($"Path traversal detected: Symbolic directory link '{path}' points to target '{targetFull}' outside repository root '{fullRoot}'.");
                    }
                    return targetFull;
                }
            }
        }

        return fullPath;
    }

    /// <summary>
    /// Validates that rootDirectory exists and is not a filesystem root.
    /// </summary>
    public static void ValidateRootDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var full = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Repository root directory not found: '{rootDirectory}'");
        }

        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root))
        {
            var cleanFull = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var cleanRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(cleanFull, cleanRoot, PathComparison) || cleanFull.Length == 0)
            {
                throw new SecurityException($"Auditing filesystem root '{rootDirectory}' is strictly prohibited.");
            }
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar) && !path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path + Path.DirectorySeparatorChar;
        }
        return path;
    }
}
