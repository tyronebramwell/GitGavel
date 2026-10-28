namespace GitGavel.Configuration;

public static class KeyResolver
{
    public static string? Resolve(string? envVarName, params string[] fallbackEnvVarNames) =>
        Resolve(SystemEnvironmentProvider.Instance, envVarName, fallbackEnvVarNames);

    public static string? Resolve(IEnvironmentProvider environment, string? envVarName, params string[] fallbackEnvVarNames)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!string.IsNullOrWhiteSpace(envVarName))
        {
            var val = environment.GetEnvironmentVariable(envVarName);
            if (!string.IsNullOrWhiteSpace(val)) return val;
        }

        foreach (var fb in fallbackEnvVarNames)
        {
            if (string.IsNullOrWhiteSpace(fb)) continue;
            var fbVal = environment.GetEnvironmentVariable(fb);
            if (!string.IsNullOrWhiteSpace(fbVal)) return fbVal;
        }

        return null;
    }
}
