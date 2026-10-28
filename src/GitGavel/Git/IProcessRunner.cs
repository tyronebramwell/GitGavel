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

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                onError?.Invoke($"Process '{fileName}' timed out after {timeoutMs}ms.");
                return null;
            }

            if (process.ExitCode != 0)
            {
                var err = process.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(err))
                {
                    onError?.Invoke($"Process '{fileName}' exited with code {process.ExitCode}: {err.Trim()}");
                }
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
