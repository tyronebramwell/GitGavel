using System.Text;
using System.Text.Json;
using GitGavel.Configuration;

namespace GitGavel.Git;

public class GitService : IGitService
{
    private readonly IProcessRunner _processRunner;
    private readonly IEnvironmentProvider _env;
    private readonly Action<string>? _onWarning;

    public GitService(
        IProcessRunner? processRunner = null,
        IEnvironmentProvider? environment = null,
        Action<string>? onWarning = null)
    {
        _processRunner = processRunner ?? ProcessRunner.Instance;
        _env = environment ?? SystemEnvironmentProvider.Instance;
        _onWarning = onWarning;
    }

    public (string commitSha, string branch) GetGitMetadata(string workingDirectory)
    {
        // 1. Check CI environment variables first
        var envSha = _env.GetEnvironmentVariable("GITHUB_SHA");
        var envBranch = _env.GetEnvironmentVariable("GITHUB_REF_NAME");

        if (!string.IsNullOrWhiteSpace(envSha) && !string.IsNullOrWhiteSpace(envBranch))
        {
            return (envSha[..Math.Min(7, envSha.Length)], envBranch);
        }

        // 2. Query git CLI locally
        var sha = envSha ?? RunGitCommand(workingDirectory, "rev-parse", "--short", "HEAD") ?? "local-dev";
        var branch = envBranch ?? RunGitCommand(workingDirectory, "rev-parse", "--abbrev-ref", "HEAD") ?? "main";

        return (sha.Trim(), branch.Trim());
    }

    public (string owner, string repo) ResolveRepoCoordinates(string workingDirectory, string? explicitRepo = null)
    {
        // 1. Explicit override passed via CLI option or config (format: "owner/repo")
        if (TryParseOwnerRepoPair(explicitRepo, out var explicitCoords))
        {
            return explicitCoords;
        }

        // 2. Check GITHUB_REPOSITORY environment variable (format: "owner/repo")
        var envRepo = _env.GetEnvironmentVariable("GITHUB_REPOSITORY");
        if (TryParseOwnerRepoPair(envRepo, out var envCoords))
        {
            return envCoords;
        }

        // 3. Query git remote origin url
        var remote = RunGitCommand(workingDirectory, "remote", "get-url", "origin");
        if (TryParseGitRemoteUrl(remote, out var remoteCoords))
        {
            return remoteCoords;
        }

        return ("local-owner", "GitGavel");
    }

    public static bool TryParseGitRemoteUrl(string? remoteUrl, out (string owner, string repo) coordinates)
    {
        coordinates = default;
        if (string.IsNullOrWhiteSpace(remoteUrl)) return false;

        var clean = remoteUrl.Trim().TrimEnd('/');
        if (clean.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^4].TrimEnd('/');
        }

        string? path = null;
        if (clean.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = clean["git@github.com:".Length..];
        }
        else if (clean.Contains("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = clean.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            path = clean[(idx + "github.com/".Length)..];
        }

        return TryParseOwnerRepoPair(path, out coordinates);
    }

    private static bool TryParseOwnerRepoPair(string? input, out (string owner, string repo) coordinates)
    {
        coordinates = default;
        if (string.IsNullOrWhiteSpace(input) || !input.Contains('/')) return false;

        var parts = input.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        coordinates = (parts[0], parts[1]);
        return true;
    }

    public IReadOnlyList<GitCommitRecord> GetRecentCommits(string workingDirectory, int maxCount = 50)
    {
        if (maxCount <= 0) return [];

        var output = RunGitCommand(workingDirectory, "log", "-n", maxCount.ToString(), "--name-only", "--pretty=format:COMMIT_START|%H|%h|%an|%aI|%s");
        return ParseCommitLogOutput(output);
    }

    public static IReadOnlyList<GitCommitRecord> ParseCommitLogOutput(string? output)
    {
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

        return TryResolvePrFromEnvironmentVariables()
               ?? TryResolvePrFromGitHubRef()
               ?? TryResolvePrFromEventPayload();
    }

    private int? TryResolvePrFromEnvironmentVariables()
    {
        var envPr = _env.GetEnvironmentVariable("INPUT_PR_NUMBER")
                    ?? _env.GetEnvironmentVariable("PR_NUMBER")
                    ?? _env.GetEnvironmentVariable("GITHUB_PR_NUMBER");
        return int.TryParse(envPr, out var parsedPr) && parsedPr > 0 ? parsedPr : null;
    }

    private int? TryResolvePrFromGitHubRef()
    {
        // GitHub Actions sets GITHUB_REF to "refs/pull/<pr_number>/merge" on pull_request events
        var ghRef = _env.GetEnvironmentVariable("GITHUB_REF");
        if (!string.IsNullOrWhiteSpace(ghRef) && ghRef.StartsWith("refs/pull/", StringComparison.OrdinalIgnoreCase))
        {
            var segments = ghRef.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 && int.TryParse(segments[2], out var refPr) && refPr > 0)
            {
                return refPr;
            }
        }

        return null;
    }

    private int? TryResolvePrFromEventPayload()
    {
        var eventPath = _env.GetEnvironmentVariable("GITHUB_EVENT_PATH");
        if (string.IsNullOrWhiteSpace(eventPath) || !File.Exists(eventPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(eventPath));
            if (doc.RootElement.TryGetProperty("pull_request", out var prElem) &&
                prElem.ValueKind == JsonValueKind.Object &&
                prElem.TryGetProperty("number", out var numProp) &&
                numProp.TryGetInt32(out var eventPr) &&
                eventPr > 0)
            {
                return eventPr;
            }

            if (doc.RootElement.TryGetProperty("issue", out var issueElem) &&
                issueElem.ValueKind == JsonValueKind.Object &&
                issueElem.TryGetProperty("pull_request", out _) &&
                issueElem.TryGetProperty("number", out var issueNumProp) &&
                issueNumProp.TryGetInt32(out var issuePr) &&
                issuePr > 0)
            {
                return issuePr;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _onWarning?.Invoke($"Failed to parse GITHUB_EVENT_PATH payload at '{eventPath}': {ex.Message}");
        }

        return null;
    }

    public string? RunGitCommand(string workingDirectory, string args)
    {
        var tokenized = TokenizeArguments(args);
        return RunGitCommand(workingDirectory, tokenized);
    }

    public string? RunGitCommand(string workingDirectory, params string[] args)
    {
        return RunGitCommand(workingDirectory, (IEnumerable<string>)args);
    }

    public string? RunGitCommand(string workingDirectory, IEnumerable<string> args)
    {
        var fullArgs = new List<string> { "-c", "safe.directory=*" };
        foreach (var arg in args)
        {
            if (!string.IsNullOrEmpty(arg))
            {
                fullArgs.Add(arg);
            }
        }

        return _processRunner.Run("git", workingDirectory, fullArgs, timeoutMs: 5000, onError: _onWarning);
    }

    public static List<string> TokenizeArguments(string args)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return list;

        var sb = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';

        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            var isEscaped = i > 0 && args[i - 1] == '\\';

            if ((c == '"' || c == '\'') && (!inQuotes || c == quoteChar) && !isEscaped)
            {
                inQuotes = !inQuotes;
                quoteChar = inQuotes ? c : '\0';
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                if (isEscaped && (c == '"' || c == '\'') && sb.Length > 0 && sb[^1] == '\\')
                {
                    sb.Length--; // Remove escape backslash to preserve literal quote in argument
                }
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            list.Add(sb.ToString());
        }

        return list;
    }
}
