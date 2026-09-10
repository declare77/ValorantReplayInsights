using System.Text.Json;
using VrfInsights.Analysis;
using VrfInsights.Analysis.Identity;
using VrfInsights.Analysis.Vision;
using VrfInsights.Data;

namespace VrfInsights.Pipeline;

/// <summary>Options for <see cref="AnalysisPipeline.RunAsync"/>. Mirrors the CLI's
/// <c>analyze</c> command flags so both the CLI and the GUI drive the exact same code path.</summary>
public sealed record AnalysisPipelineOptions(
    double FovDegrees = VisionConeCalculator.DefaultFovDegrees,
    double RangeCm = VisionConeCalculator.DefaultRangeCm,
    double EyeHeightCm = VisionConeCalculator.EyeHeightCm,
    bool WithVision = false);

/// <summary>
/// The single place that turns a vrfkit export directory into this project's JSON analysis
/// output. Both <c>VrfInsights.Cli</c>'s <c>analyze</c> command and <c>VrfInsights.Gui</c> call
/// this instead of duplicating the "load export, build MatchAnalysis, write JSON files" steps
/// in two places.
/// </summary>
public static class AnalysisPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<MatchAnalysis> RunAsync(
        string exportDirectory,
        string outputDirectory,
        AnalysisPipelineOptions? options = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        options ??= new AnalysisPipelineOptions();

        onStatus?.Invoke($"Loading vrfkit export from: {exportDirectory}");
        VrfExportSet export = await VrfExportSet.LoadAsync(exportDirectory);
        ct.ThrowIfCancellationRequested();

        onStatus?.Invoke($"Build: {export.Manifest.ReplayBuild}   Duration: {export.Manifest.DurationMs} ms");
        onStatus?.Invoke($"Tables: fields={export.Fields.Count:N0} movement={export.Movement.Count:N0} actors={export.Actors.Count:N0} net_guids={export.NetGuids.Count:N0} events={export.Events.Count:N0}");

        AgentCatalog agentCatalog = AgentCatalog.LoadEmbedded();
        var visionOptions = new VisionConeOptions(options.FovDegrees, options.RangeCm, options.EyeHeightCm);

        onStatus?.Invoke("Building match analysis...");
        MatchAnalysis analysis = MatchAnalysis.Build(export, agentCatalog, visionOptions);
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(outputDirectory);

        onStatus?.Invoke("Writing JSON output...");

        await WriteJsonAsync(Path.Combine(outputDirectory, "match.json"), new
        {
            analysis.ReplayBuild,
            analysis.DurationMs,
            Players = analysis.Players,
            Rounds = analysis.Rounds,
        });

        await WriteJsonAsync(Path.Combine(outputDirectory, "events.json"), analysis.Events);
        await WriteJsonAsync(Path.Combine(outputDirectory, "movement.json"), analysis.MovementTracks);
        await WriteJsonAsync(Path.Combine(outputDirectory, "utility.json"), analysis.Utility);
        await WriteJsonAsync(Path.Combine(outputDirectory, "ability_casts.json"), analysis.AbilityCasts);
        await WriteJsonAsync(Path.Combine(outputDirectory, "ultimate_usages.json"), analysis.UltimateUsages);
        await WriteJsonAsync(Path.Combine(outputDirectory, "combat_interactions.json"), analysis.CombatInteractions);
        await WriteJsonAsync(Path.Combine(outputDirectory, "economy.json"), analysis.Economy);

        if (options.WithVision)
        {
            onStatus?.Invoke("Computing vision cones (this can take a while on long replays)...");
            var visionByPlayer = new Dictionary<string, object>();
            foreach (var track in analysis.MovementTracks)
            {
                string label = track.Player.Subject ?? $"actor_{track.Player.ActorNetGuid}";
                visionByPlayer[label] = analysis.BuildVisionCones(track, visionOptions);
            }

            await WriteJsonAsync(Path.Combine(outputDirectory, "vision_cones.json"), visionByPlayer);
        }

        onStatus?.Invoke($"Wrote analysis output to: {Path.GetFullPath(outputDirectory)}");
        onStatus?.Invoke($"Players: {analysis.Players.Count}   Rounds: {analysis.Rounds.Count}   Utility events: {analysis.Utility.Count}   Ability casts: {analysis.AbilityCasts.Count}   Combat interactions: {analysis.CombatInteractions.Count}");

        return analysis;
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        await using FileStream fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, value, JsonOptions);
    }
}
