namespace GitGavel.Configuration;

public static class KeyResolver
{
    public static string? Resolve(string? directKey, string? envVarName, params string[] fallbackEnvVarNames)
    {
        if (!string.IsNullOrWhiteSpace(directKey)) return directKey;

        if (!string.IsNullOrWhiteSpace(envVarName))
        {
            var val = Environment.GetEnvironmentVariable(envVarName);
            if (!string.IsNullOrWhiteSpace(val)) return val;

            // If the user pasted their raw API key directly into the apiKeyEnv field
            if (IsRawKey(envVarName)) return envVarName;
        }

        foreach (var fb in fallbackEnvVarNames)
        {
            var fbVal = Environment.GetEnvironmentVariable(fb);
            if (!string.IsNullOrWhiteSpace(fbVal)) return fbVal;
        }

        return null;
    }

    public static bool IsRawKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.StartsWith("apikey_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("AIzaSy", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase) ||
               value.Length > 40;
    }
}
