using System.Diagnostics;

namespace VrfInsights.Pipeline;

/// <summary>
/// Shells out to an already-built <c>vrfkit.exe</c> (https://github.com/yakisoba0728/vrfkit) as
/// an external process to turn a <c>.vrf</c> file into a decoded export directory (Parquet
/// tables + <c>manifest.json</c>).
///
/// <para><b>This class never opens a <c>.vrf</c> file itself and contains no decoding/payload-
/// transform logic of any kind.</b> It only launches vrfkit's own binary with
/// <c>export &lt;file&gt; --out &lt;dir&gt;</c> and reports vrfkit's own exit code and console
/// output back to the caller. All of the actual cipher-defeating work happens inside vrfkit,
/// which is an independent, external, MIT-licensed tool — not code that lives in, or is
/// derived from, this repository.</para>
/// </summary>
public static class VrfkitExportRunner
{
    public sealed record VrfkitExportResult(bool Success, int ExitCode, string ExportDirectory);

    /// <param name="vrfkitExePath">Path to a vrfkit executable the user built/downloaded
    /// themselves (see vrfkit's own README) — e.g.
    /// <c>...\vrfkit\target\release\vrfkit.exe</c>.</param>
    /// <param name="vrfFilePath">Path to the <c>.vrf</c> replay file to decode.</param>
    /// <param name="exportDirectory">Directory vrfkit should write its Parquet tables and
    /// <c>manifest.json</c> into. Created if it doesn't already exist.</param>
    /// <param name="onOutputLine">Called for each line vrfkit writes to stdout, as it's
    /// produced — useful for streaming progress into a console or a GUI log box.</param>
    /// <param name="onErrorLine">Called for each line vrfkit writes to stderr, as it's
    /// produced.</param>
    public static async Task<VrfkitExportResult> RunAsync(
        string vrfkitExePath,
        string vrfFilePath,
        string exportDirectory,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(vrfkitExePath))
        {
            throw new FileNotFoundException($"vrfkit executable not found: {vrfkitExePath}", vrfkitExePath);
        }

        if (!File.Exists(vrfFilePath))
        {
            throw new FileNotFoundException($"replay file not found: {vrfFilePath}", vrfFilePath);
        }

        Directory.CreateDirectory(exportDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = vrfkitExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(vrfkitExePath)) ?? Environment.CurrentDirectory,
        };

        // ArgumentList (not a concatenated string) so paths with spaces need no manual
        // quoting/escaping.
        startInfo.ArgumentList.Add("export");
        startInfo.ArgumentList.Add(vrfFilePath);
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(exportDirectory);

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
            throw new InvalidOperationException($"failed to start vrfkit process: {vrfkitExePath}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        return new VrfkitExportResult(process.ExitCode == 0, process.ExitCode, exportDirectory);
    }
}
