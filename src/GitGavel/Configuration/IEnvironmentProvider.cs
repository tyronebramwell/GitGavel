namespace GitGavel.Configuration;

public interface IEnvironmentProvider
{
    string? GetEnvironmentVariable(string variable);
    void SetEnvironmentVariable(string variable, string? value);
}

public class SystemEnvironmentProvider : IEnvironmentProvider
{
    public static readonly SystemEnvironmentProvider Instance = new();

    public string? GetEnvironmentVariable(string variable) =>
        Environment.GetEnvironmentVariable(variable);

    public void SetEnvironmentVariable(string variable, string? value) =>
        Environment.SetEnvironmentVariable(variable, value);
}
