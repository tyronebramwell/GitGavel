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

    public IReadOnlyList<GitCommitRecord> GetRecentCommits(string workingDirectory, int maxCount = 50)
    {
        if (maxCount <= 0) return [];

        var output = RunGitCommand(workingDirectory, $"log -n {maxCount} --name-only --pretty=format:COMMIT_START|%H|%h|%an|%aI|%s");
        if (string.IsNullOrWhiteSpace(output)) return [];

        var commits = new List<GitCommitRecord>();
        var lines = output.Split('\n', StringSplitOptions.TrimEntries);

        string? sha = null;
        string? shortSha = null;
        string? author = null;
        string? dateIso = null;
        string? subject = null;
        var changedFiles = new List<string>();

        void FlushCurrent()
        {
            if (!string.IsNullOrWhiteSpace(sha))
            {
                commits.Add(new GitCommitRecord(
                    Sha: sha,
                    ShortSha: shortSha ?? sha[..Math.Min(7, sha.Length)],
                    Subject: subject ?? "",
                    Author: author ?? "unknown",
                    DateIso: dateIso ?? "",
                    ChangedFiles: [.. changedFiles]
                ));
            }
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("COMMIT_START|", StringComparison.Ordinal))
            {
                FlushCurrent();
                changedFiles.Clear();

                var parts = line.Split('|', 6);
                sha = parts.Length > 1 ? parts[1] : "";
                shortSha = parts.Length > 2 ? parts[2] : "";
                author = parts.Length > 3 ? parts[3] : "";
                dateIso = parts.Length > 4 ? parts[4] : "";
                subject = parts.Length > 5 ? parts[5] : "";
            }
            else if (sha != null)
            {
                changedFiles.Add(line);
            }
        }

        FlushCurrent();
        return commits;
    }

    public int? ResolvePullRequestNumber(int? explicitPrNumber = null)
    {
        if (explicitPrNumber is > 0) return explicitPrNumber;

        var envPr = Environment.GetEnvironmentVariable("INPUT_PR_NUMBER")
                    ?? Environment.GetEnvironmentVariable("PR_NUMBER")
                    ?? Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER");
        if (int.TryParse(envPr, out var parsedPr) && parsedPr > 0)
        {
            return parsedPr;
        }

        // GitHub Actions sets GITHUB_REF to "refs/pull/<pr_number>/merge" on pull_request events
        var ghRef = Environment.GetEnvironmentVariable("GITHUB_REF");
        if (!string.IsNullOrWhiteSpace(ghRef) && ghRef.StartsWith("refs/pull/", StringComparison.OrdinalIgnoreCase))
        {
            var segments = ghRef.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 && int.TryParse(segments[2], out var refPr) && refPr > 0)
            {
                return refPr;
            }
        }

        // Fallback: inspect GITHUB_EVENT_PATH JSON payload for pull_request.number
        var eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
        if (!string.IsNullOrWhiteSpace(eventPath) && File.Exists(eventPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(eventPath));
                if (doc.RootElement.TryGetProperty("pull_request", out var prElem) &&
                    prElem.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    prElem.TryGetProperty("number", out var numProp) &&
                    numProp.TryGetInt32(out var eventPr) &&
                    eventPr > 0)
                {
                    return eventPr;
                }
            }
            catch
            {
                // Ignore malformed event payload
            }
        }

        return null;
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
