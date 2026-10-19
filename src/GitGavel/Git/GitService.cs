using System.Diagnostics;

namespace GitGavel.Git;

public class GitService : IGitService
{
    public (string commitSha, string branch) GetGitMetadata(string workingDirectory)
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

    public (string owner, string repo) ResolveRepoCoordinates(string workingDirectory, string? explicitRepo = null)
    {
        // 1. Explicit override passed via CLI option or config (format: "owner/repo")
        if (!string.IsNullOrWhiteSpace(explicitRepo) && explicitRepo.Contains('/'))
        {
            var parts = explicitRepo.Split('/', 2);
            return (parts[0].Trim(), parts[1].Trim());
        }

        // 2. Check GITHUB_REPOSITORY environment variable (format: "owner/repo")
        var envRepo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        if (!string.IsNullOrWhiteSpace(envRepo) && envRepo.Contains('/'))
        {
            var parts = envRepo.Split('/', 2);
            return (parts[0].Trim(), parts[1].Trim());
        }

        // 3. Query git remote origin url
        var remote = RunGitCommand(workingDirectory, "remote get-url origin");
        if (!string.IsNullOrWhiteSpace(remote))
        {
            // https://github.com/owner/repo.git or git@github.com:owner/repo.git
            var clean = remote.Trim();
            if (clean.EndsWith(".git")) clean = clean[..^4];

            if (clean.StartsWith("git@github.com:"))
            {
                var path = clean["git@github.com:".Length..];
                var parts = path.Split('/', 2);
                if (parts.Length == 2) return (parts[0], parts[1]);
            }
            else if (clean.Contains("github.com/"))
            {
                var idx = clean.IndexOf("github.com/", StringComparison.Ordinal);
                var path = clean[(idx + "github.com/".Length)..];
                var parts = path.Split('/', 2);
                if (parts.Length == 2) return (parts[0], parts[1]);
            }
        }

        return ("local-owner", "GitGavel");
    }

    public string? RunGitCommand(string workingDirectory, string args)
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
