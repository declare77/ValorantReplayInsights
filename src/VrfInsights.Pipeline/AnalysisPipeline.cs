using System.Text.Json;
using System.Text.Json.Serialization;
using VrfInsights.Analysis;
using VrfInsights.Analysis.Identity;
using VrfInsights.Analysis.Movement;
using VrfInsights.Analysis.Vision;
using VrfInsights.Data;

namespace VrfInsights.Pipeline;

/// <summary>Options for <see cref="AnalysisPipeline.RunAsync"/>. Mirrors the CLI's
/// <c>analyze</c> command flags so both the CLI and the GUI drive the exact same code path.</summary>
/// <param name="MovementSamplesPerSecond">Caps how many movement samples per second per player
/// make it into <c>movement.json</c> (and, downstream, <c>vision_cones.json</c>) — see
/// <see cref="VrfInsights.Analysis.Movement.MovementDownsampler"/> for why this exists. 0 means
/// full fidelity (can produce a movement.json hundreds of MB for a full match).</param>
public sealed record AnalysisPipelineOptions(
    double FovDegrees = VisionConeCalculator.DefaultFovDegrees,
    double RangeCm = VisionConeCalculator.DefaultRangeCm,
    double EyeHeightCm = VisionConeCalculator.EyeHeightCm,
    bool WithVision = false,
    double MovementSamplesPerSecond = 10);

/// <summary>
/// The single place that turns a vrfkit export directory into this project's JSON analysis
/// output. Both <c>VrfInsights.Cli</c>'s <c>analyze</c> command and <c>VrfInsights.Gui</c> call
/// this instead of duplicating the "load export, build MatchAnalysis, write JSON files" steps
/// in two places.
/// </summary>
public static class AnalysisPipeline
{
    // JsonStringEnumConverter so e.g. UtilityCategory comes out as "Smoke" rather than a bare
    // integer — self-describing for anything else that consumes this JSON (the 2D replay
    // viewer included) without needing to know this project's enum declaration order.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // movement.json and vision_cones.json scale with sample count and can get very large (see
    // MovementDownsampler's remarks) — skip indentation for these two specifically so they stay
    // as small as the (already-thinned) data allows. Everything else stays indented/readable,
    // since inspecting them by hand is part of how this project's own assumptions get checked.
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

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
            Map = analysis.Map,
            Players = analysis.Players,
            Rounds = analysis.Rounds,
        });

        await WriteJsonAsync(Path.Combine(outputDirectory, "events.json"), analysis.Events);

        IReadOnlyList<PlayerTrack> tracksForOutput = MovementDownsampler.Downsample(analysis.MovementTracks, options.MovementSamplesPerSecond);
        if (options.MovementSamplesPerSecond > 0)
        {
            int rawTotal = analysis.MovementTracks.Sum(t => t.Samples.Count);
            int keptTotal = tracksForOutput.Sum(t => t.Samples.Count);
            onStatus?.Invoke($"Downsampling movement for output to ~{options.MovementSamplesPerSecond:0.#}/sec per player ({rawTotal:N0} -> {keptTotal:N0} samples; use --movement-hz 0 for full fidelity).");
        }
        await WriteJsonAsync(Path.Combine(outputDirectory, "movement.json"), tracksForOutput, CompactJsonOptions);

        await WriteJsonAsync(Path.Combine(outputDirectory, "utility.json"), analysis.Utility);
        await WriteJsonAsync(Path.Combine(outputDirectory, "ability_casts.json"), analysis.AbilityCasts);
        await WriteJsonAsync(Path.Combine(outputDirectory, "ultimate_usages.json"), analysis.UltimateUsages);
        await WriteJsonAsync(Path.Combine(outputDirectory, "combat_interactions.json"), analysis.CombatInteractions);
        await WriteJsonAsync(Path.Combine(outputDirectory, "economy.json"), analysis.Economy);

        if (options.WithVision)
        {
            onStatus?.Invoke("Computing vision cones (this can take a while on long replays)...");
            var visionByPlayer = new Dictionary<string, object>();
            foreach (var track in tracksForOutput)
            {
                string label = track.Player.Subject ?? $"actor_{track.Player.ActorNetGuid}";
                visionByPlayer[label] = analysis.BuildVisionCones(track, visionOptions);
            }

            await WriteJsonAsync(Path.Combine(outputDirectory, "vision_cones.json"), visionByPlayer, CompactJsonOptions);
        }

        onStatus?.Invoke($"Wrote analysis output to: {Path.GetFullPath(outputDirectory)}");
        onStatus?.Invoke($"Players: {analysis.Players.Count}   Rounds: {analysis.Rounds.Count}   Utility events: {analysis.Utility.Count}   Ability casts: {analysis.AbilityCasts.Count}   Combat interactions: {analysis.CombatInteractions.Count}");

        return analysis;
    }

    private static async Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        await using FileStream fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, value, options ?? JsonOptions);
    }
}
