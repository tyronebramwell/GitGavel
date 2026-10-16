namespace GitGavel.Configuration;

public static class DotEnvLoader
{
    public static void Load(string? directory = null)
    {
        var dir = directory ?? Directory.GetCurrentDirectory();
        var envPath = Path.Combine(dir, ".env");
        if (!File.Exists(envPath)) return;

        try
        {
            foreach (var line in File.ReadAllLines(envPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

                var parts = trimmed.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    var key = parts[0];
                    var val = parts[1].Trim('"', '\'');
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                    {
                        Environment.SetEnvironmentVariable(key, val);
                    }
                }
            }
        }
        catch
        {
            // Ignore errors reading .env gracefully
        }
    }
}
