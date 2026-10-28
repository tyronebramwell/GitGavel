using System.ComponentModel;
using System.Diagnostics;

namespace GitGavel.Git;

public interface IProcessRunner
{
    string? Run(string fileName, string workingDirectory, IEnumerable<string> arguments, int timeoutMs = 5000, Action<string>? onError = null);
}

public class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Instance = new();

    public string? Run(string fileName, string workingDirectory, IEnumerable<string> arguments, int timeoutMs = 5000, Action<string>? onError = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in arguments)
            {
                if (!string.IsNullOrEmpty(arg))
                {
                    psi.ArgumentList.Add(arg);
                }
            }

            using var process = Process.Start(psi);
            if (process == null) return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                onError?.Invoke($"Process '{fileName}' timed out after {timeoutMs}ms.");
                return null;
            }

            var output = outputTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                var err = errorTask.GetAwaiter().GetResult();
                var suffix = string.IsNullOrWhiteSpace(err) ? "" : $": {err.Trim()}";
                onError?.Invoke($"Process '{fileName}' exited with code {process.ExitCode}{suffix}");
                return null;
            }

            return output.Trim();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            onError?.Invoke($"Failed to execute '{fileName}': {ex.Message}");
            return null;
        }
    }
}
