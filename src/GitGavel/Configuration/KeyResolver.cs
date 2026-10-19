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
        }

        foreach (var fb in fallbackEnvVarNames)
        {
            var fbVal = Environment.GetEnvironmentVariable(fb);
            if (!string.IsNullOrWhiteSpace(fbVal)) return fbVal;
        }

        return null;
    }
}
