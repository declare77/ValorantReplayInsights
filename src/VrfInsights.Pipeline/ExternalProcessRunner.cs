using System.Diagnostics;

namespace VrfInsights.Pipeline;

/// <summary>
/// Small shared helper for launching an external process (vrfkit, git, cargo) with
/// <see cref="ProcessStartInfo.ArgumentList"/> (no manual quoting), streaming its stdout/stderr
/// line by line, and awaiting its exit. Every process this project ever launches goes through
/// this one place.
/// </summary>
public static class ExternalProcessRunner
{
    public sealed record RunResult(int ExitCode, bool Success);

    public static async Task<RunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) onErrorLine?.Invoke(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"failed to start process: {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        return new RunResult(process.ExitCode, process.ExitCode == 0);
    }

    /// <summary>
    /// True if <paramref name="fileName"/> can be launched at all (i.e. it's on PATH / a valid
    /// executable) and returns a zero exit code for <paramref name="versionArg"/>. Used to check
    /// whether git/cargo are installed before trying to use them. Never throws — a missing
    /// executable (the common case) is reported as <c>false</c>, not an exception.
    /// </summary>
    public static async Task<bool> IsAvailableAsync(string fileName, string versionArg = "--version", CancellationToken ct = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(versionArg);

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The executable isn't on PATH / couldn't be launched at all.
            return false;
        }
    }
}
