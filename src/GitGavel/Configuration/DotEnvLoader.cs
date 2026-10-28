using System.Security;

namespace GitGavel.Configuration;

public static class DotEnvLoader
{
    public static void Load(
        string? directory = null,
        bool allowExternalPaths = false,
        IEnvironmentProvider? environment = null,
        Action<string>? onWarning = null)
    {
        var env = environment ?? SystemEnvironmentProvider.Instance;
        var dir = directory ?? Directory.GetCurrentDirectory();
        var envPath = Path.Combine(dir, ".env");
        if (!File.Exists(envPath)) return;

        try
        {
            var verifiedPath = PathSecurity.ResolveAndVerifyWithinRoot(envPath, dir, allowExternalPaths);
            foreach (var line in File.ReadAllLines(verifiedPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;
                if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed["export ".Length..].TrimStart();
                }

                var parts = trimmed.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    var key = parts[0];
                    var val = parts[1].Trim('"', '\'');
                    if (string.IsNullOrWhiteSpace(env.GetEnvironmentVariable(key)))
                    {
                        env.SetEnvironmentVariable(key, val);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            onWarning?.Invoke($"Unable to load .env file from '{envPath}': {ex.Message}");
        }
    }
}
