using System.Diagnostics;

namespace GitGavel.Reporting;

public static class GitMetadataHelper
{
    public static (string commitSha, string branch) GetGitMetadata(string workingDirectory)
    {
        // 1. Check CI environment variables first
        var envSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        var envBranch = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

        if (!string.IsNullOrWhiteSpace(envSha) && !string.IsNullOrWhiteSpace(envBranch))
        {
            return (envSha[..Math.Min(7, envSha.Length)], envBranch);
        }

        // 2. Query git CLI locally
        var sha = envSha ?? RunGitCommand(workingDirectory, "rev-parse --short HEAD") ?? "local-dev";
        var branch = envBranch ?? RunGitCommand(workingDirectory, "rev-parse --abbrev-ref HEAD") ?? "main";

        return (sha.Trim(), branch.Trim());
    }

    private static string? RunGitCommand(string workingDirectory, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
