namespace VrfInsights.Analysis.Movement;

/// <summary>
/// Reduces how many samples a <see cref="PlayerTrack"/> carries, for output formats that scale
/// linearly with sample count — chiefly <c>movement.json</c> and, downstream of it,
/// <c>vision_cones.json</c>. <c>movement.parquet</c> can replicate at close to the replay's own
/// tick rate (observed up to roughly 128/sec per player), and a full match's worth of that,
/// serialized as JSON, easily reaches hundreds of megabytes for a single file — far more than a
/// browser's <c>FileReader</c>/<c>JSON.parse</c> can reliably load for the 2D replay viewer.
///
/// This is purely a presentation-layer thinning for JSON output: it's applied by
/// <see cref="VrfInsights.Pipeline.AnalysisPipeline"/> when writing files, never inside
/// <see cref="MatchAnalysis"/> itself, so anything working with <c>MatchAnalysis</c> directly
/// (tests, another consumer) still sees full-fidelity movement data.
/// </summary>
public static class MovementDownsampler
{
    /// <param name="maxSamplesPerSecond">Keep at most this many samples per second per player, by
    /// minimum time spacing (not a fixed-grid resample) — plus always the first and last sample
    /// of each track, so a player's spawn point and final position are never dropped. A value
    /// &lt;= 0 returns the input unchanged (full fidelity, for anyone who wants the raw data).</param>
    public static IReadOnlyList<PlayerTrack> Downsample(IReadOnlyList<PlayerTrack> tracks, double maxSamplesPerSecond)
    {
        if (maxSamplesPerSecond <= 0)
        {
            return tracks;
        }

        double minIntervalMs = 1000.0 / maxSamplesPerSecond;
        var result = new List<PlayerTrack>(tracks.Count);

        foreach (PlayerTrack track in tracks)
        {
            IReadOnlyList<MovementSample> samples = track.Samples;
            if (samples.Count <= 2)
            {
                result.Add(track);
                continue;
            }

            var kept = new List<MovementSample>(samples.Count) { samples[0] };
            long lastKeptTimeMs = samples[0].TimeMs;

            for (int i = 1; i < samples.Count - 1; i++)
            {
                if (samples[i].TimeMs - lastKeptTimeMs >= minIntervalMs)
                {
                    kept.Add(samples[i]);
                    lastKeptTimeMs = samples[i].TimeMs;
                }
            }

            MovementSample last = samples[^1];
            if (kept[^1].TimeMs != last.TimeMs)
            {
                kept.Add(last);
            }

            result.Add(track with { Samples = kept });
        }

        return result;
    }
}
