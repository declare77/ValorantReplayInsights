using VrfInsights.Analysis;

namespace VrfInsights.Pipeline;

/// <summary>
/// Composes <see cref="VrfkitExportRunner"/> (decode with vrfkit) and
/// <see cref="AnalysisPipeline"/> (analyze vrfkit's output) into the "one smooth command" the
/// CLI's <c>run</c> command and the GUI's Run button both call: point it at a <c>.vrf</c> file
/// and a vrfkit executable, and it produces the finished JSON analysis, without the caller
/// needing to juggle two separate tools.
/// </summary>
public static class FullPipeline
{
    public sealed record FullPipelineOptions(
        string VrfFilePath,
        string VrfkitExePath,
        string ExportDirectory,
        string OutputDirectory,
        AnalysisPipelineOptions AnalysisOptions);

    public sealed record FullPipelineResult(
        bool ExportSucceeded,
        int ExportExitCode,
        MatchAnalysis? Analysis);

    /// <param name="onStatus">High-level progress messages ("Decoding with vrfkit...",
    /// "Building match analysis...", etc.) — safe to show as a single status line.</param>
    /// <param name="onLogLine">Raw output lines (vrfkit's own stdout/stderr) — safe to append to
    /// a scrolling log box.</param>
    public static async Task<FullPipelineResult> RunAsync(
        FullPipelineOptions options,
        Action<string>? onStatus = null,
        Action<string>? onLogLine = null,
        CancellationToken ct = default)
    {
        onStatus?.Invoke($"Decoding {Path.GetFileName(options.VrfFilePath)} with vrfkit...");

        VrfkitExportRunner.VrfkitExportResult exportResult = await VrfkitExportRunner.RunAsync(
            options.VrfkitExePath,
            options.VrfFilePath,
            options.ExportDirectory,
            onOutputLine: line => onLogLine?.Invoke(line),
            onErrorLine: line => onLogLine?.Invoke(line),
            ct: ct);

        if (!exportResult.Success)
        {
            onStatus?.Invoke($"vrfkit export failed (exit code {exportResult.ExitCode}) — see log above.");
            return new FullPipelineResult(false, exportResult.ExitCode, null);
        }

        onStatus?.Invoke("vrfkit export finished. Starting analysis...");

        MatchAnalysis analysis = await AnalysisPipeline.RunAsync(
            options.ExportDirectory,
            options.OutputDirectory,
            options.AnalysisOptions,
            onStatus: line => onLogLine?.Invoke(line),
            ct: ct);

        onStatus?.Invoke("Done.");
        return new FullPipelineResult(true, exportResult.ExitCode, analysis);
    }
}
