using System.Text;
using System.Text.RegularExpressions;
using GitGavel.Configuration;
using GitGavel.Git;

namespace GitGavel.Skills;

public record ResolvedSkill(
    string Id,
    string Name,
    string Description,
    string Instructions,
    string RepoAlias,
    string? CommitSha = null);

public record FetchedSkillRepositoryInfo(
    string Alias,
    string SourceUrlOrPath,
    string ResolvedLocalDirectory,
    string? CommitSha,
    bool WasClonedOnDemand);

public record SkillsContext
{
    public IReadOnlyList<ResolvedSkill> Skills { get; init; } = [];
    public IReadOnlyList<FetchedSkillRepositoryInfo> Repositories { get; init; } = [];

    public IReadOnlyList<ResolvedSkill> ResolveForAgent(IEnumerable<string>? requestedRefs)
    {
        if (Skills.Count == 0 || requestedRefs == null)
        {
            return [];
        }

        var matched = new List<ResolvedSkill>();
        foreach (var rawRef in requestedRefs.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var trimmed = rawRef.Trim();
            if (trimmed.EndsWith("/*", StringComparison.Ordinal))
            {
                var aliasPrefix = trimmed[..^2];
                matched.AddRange(Skills.Where(s => string.Equals(s.RepoAlias, aliasPrefix, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                var exact = Skills.FirstOrDefault(s =>
                    string.Equals(s.Id, trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    matched.Add(exact);
                }
            }
        }

        return matched.DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string BuildPromptSectionForAgent(IEnumerable<string>? requestedRefs, int maxChars = 16000)
    {
        var agentSkills = ResolveForAgent(requestedRefs);
        if (agentSkills.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("### Active Agent Skills & Domain Playbooks:");
        foreach (var skill in agentSkills)
        {
            var desc = !string.IsNullOrWhiteSpace(skill.Description) ? $" — {skill.Description}" : "";
            sb.AppendLine($"--- Skill: {skill.Name} ({skill.Id}){desc} ---");
            sb.AppendLine(skill.Instructions);
        }

        var text = sb.ToString();
        if (maxChars > 0 && text.Length > maxChars)
        {
            return text[..maxChars] + "\n...(skills truncated)";
        }

        return text;
    }
}

public partial class SkillResolver
{
    private readonly IProcessRunner _processRunner;

    [GeneratedRegex(@"^---\s*\r?\n(?<frontmatter>[\s\S]*?)\r?\n---\s*\r?\n(?<body>[\s\S]*)$", RegexOptions.Compiled)]
    private static partial Regex YamlFrontmatterRegex();

    public SkillResolver(IProcessRunner? processRunner = null)
    {
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public async Task<SkillsContext> ResolveSkillsOnDemandAsync(
        string rootDir,
        SkillsConfig? skillsConfig,
        IEnumerable<string>? requiredSkillRefs,
        bool allowExternalPaths = false,
        CancellationToken ct = default)
    {
        if (skillsConfig == null || skillsConfig.Repositories.Count == 0)
        {
            return new SkillsContext();
        }

        var fullRoot = Path.GetFullPath(rootDir);
        PathSecurity.ValidateRootDirectory(fullRoot);

        var requestedList = (requiredSkillRefs ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requestedList.Count == 0)
        {
            return new SkillsContext();
        }

        var neededAliases = requestedList
            .Where(r => r.Contains('/'))
            .Select(r => r.Split('/')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resolvedSkills = new List<ResolvedSkill>();
        var repoInfos = new List<FetchedSkillRepositoryInfo>();

        var cacheRoot = PathSecurity.ResolveAndVerifyWithinRoot(
            string.IsNullOrWhiteSpace(skillsConfig.CacheDir) ? ".gitgavel/cache/skills" : skillsConfig.CacheDir,
            fullRoot,
            allowExternalPaths);

        foreach (var repo in skillsConfig.Repositories)
        {
            if (string.IsNullOrWhiteSpace(repo.Alias))
            {
                continue;
            }

            // Only fetch repositories that are referenced by at least one active agent (or if unprefixed skill refs exist)
            var hasUnprefixedRefs = requestedList.Any(r => !r.Contains('/'));
            if (!neededAliases.Contains(repo.Alias) && !hasUnprefixedRefs)
            {
                continue;
            }

            string? repoBaseDir = null;
            string? commitSha = null;
            var clonedOnDemand = false;

            if (!string.IsNullOrWhiteSpace(repo.LocalPath))
            {
                try
                {
                    repoBaseDir = PathSecurity.ResolveAndVerifyWithinRoot(repo.LocalPath, fullRoot, allowExternalPaths);
                }
                catch
                {
                    continue;
                }
            }
            else if (!string.IsNullOrWhiteSpace(repo.Git))
            {
                var safeAlias = Regex.Replace(repo.Alias, @"[^a-zA-Z0-9_\-]", "_");
                var targetCloneDir = Path.Combine(cacheRoot, safeAlias);

                if (!Directory.Exists(targetCloneDir) && skillsConfig.FetchOnDemand)
                {
                    Directory.CreateDirectory(cacheRoot);
                    var cloneUrl = ResolveAuthenticatedGitUrl(repo.Git, repo.AuthEnv);
                    var branchRef = string.IsNullOrWhiteSpace(repo.Ref) ? "main" : repo.Ref;
                    var args = new[] { "-c", "safe.directory=*", "clone", "--depth", "1", "--branch", branchRef, "--single-branch", cloneUrl, targetCloneDir };
                    var cloneOutput = await Task.Run(() => _processRunner.Run("git", fullRoot, args, timeoutMs: 30000), ct);
                    if (cloneOutput != null)
                    {
                        clonedOnDemand = true;
                    }
                }

                if (Directory.Exists(targetCloneDir))
                {
                    repoBaseDir = targetCloneDir;
                    var shaOutput = _processRunner.Run("git", targetCloneDir, ["-c", "safe.directory=*", "rev-parse", "--short", "HEAD"]);
                    if (!string.IsNullOrWhiteSpace(shaOutput))
                    {
                        commitSha = shaOutput.Trim();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(repoBaseDir) || !Directory.Exists(repoBaseDir))
            {
                continue;
            }

            var skillsRoot = repoBaseDir;
            if (!string.IsNullOrWhiteSpace(repo.Path))
            {
                try
                {
                    skillsRoot = PathSecurity.ResolveAndVerifyWithinRoot(repo.Path, repoBaseDir, allowExternal: false);
                }
                catch
                {
                    continue;
                }
            }

            if (!Directory.Exists(skillsRoot))
            {
                continue;
            }

            repoInfos.Add(new FetchedSkillRepositoryInfo(
                repo.Alias,
                repo.Git ?? repo.LocalPath ?? repo.Alias,
                skillsRoot,
                commitSha,
                clonedOnDemand));

            var discovered = await DiscoverSkillsInDirectoryAsync(repo.Alias, skillsRoot, commitSha, ct);
            resolvedSkills.AddRange(discovered);
        }

        return new SkillsContext
        {
            Skills = resolvedSkills,
            Repositories = repoInfos
        };
    }

    public static async Task<List<ResolvedSkill>> DiscoverSkillsInDirectoryAsync(
        string repoAlias,
        string skillsRoot,
        string? commitSha = null,
        CancellationToken ct = default)
    {
        var result = new List<ResolvedSkill>();
        if (!Directory.Exists(skillsRoot))
        {
            return result;
        }

        // 1. Discover <skill-folder>/SKILL.md
        foreach (var subDir in Directory.GetDirectories(skillsRoot))
        {
            var skillFile = Path.Combine(subDir, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                continue;
            }

            var folderName = Path.GetFileName(subDir);
            var raw = await File.ReadAllTextAsync(skillFile, ct);
            var (name, desc, body) = ParseSkillMarkdown(raw, folderName);
            result.Add(new ResolvedSkill(
                Id: $"{repoAlias}/{folderName}",
                Name: name,
                Description: desc,
                Instructions: body,
                RepoAlias: repoAlias,
                CommitSha: commitSha));
        }

        // 2. Discover standalone <skill-name>.md files directly in skillsRoot
        foreach (var mdFile in Directory.GetFiles(skillsRoot, "*.md"))
        {
            var fileNameNoExt = Path.GetFileNameWithoutExtension(mdFile);
            if (string.Equals(fileNameNoExt, "README", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileNameNoExt, "SKILL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = await File.ReadAllTextAsync(mdFile, ct);
            var (name, desc, body) = ParseSkillMarkdown(raw, fileNameNoExt);
            result.Add(new ResolvedSkill(
                Id: $"{repoAlias}/{fileNameNoExt}",
                Name: name,
                Description: desc,
                Instructions: body,
                RepoAlias: repoAlias,
                CommitSha: commitSha));
        }

        return result;
    }

    public static (string Name, string Description, string Body) ParseSkillMarkdown(string content, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return (fallbackName, string.Empty, string.Empty);
        }

        var match = YamlFrontmatterRegex().Match(content);
        if (!match.Success)
        {
            return (fallbackName, string.Empty, content.Trim());
        }

        var frontmatter = match.Groups["frontmatter"].Value;
        var body = match.Groups["body"].Value.Trim();
        var name = fallbackName;
        var description = string.Empty;

        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                var v = line[5..].Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(v)) name = v;
            }
            else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                var v = line[12..].Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(v)) description = v;
            }
        }

        return (name, description, body);
    }

    private static string ResolveAuthenticatedGitUrl(string gitUrl, string? authEnv)
    {
        if (string.IsNullOrWhiteSpace(authEnv))
        {
            return gitUrl;
        }

        var token = Environment.GetEnvironmentVariable(authEnv);
        if (string.IsNullOrWhiteSpace(token) || !gitUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return gitUrl;
        }

        return $"https://x-access-token:{token}@{gitUrl["https://".Length..]}";
    }
}
