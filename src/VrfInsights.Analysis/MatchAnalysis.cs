using VrfInsights.Analysis.Abilities;
using VrfInsights.Analysis.Combat;
using VrfInsights.Analysis.Identity;
using VrfInsights.Analysis.Loadouts;
using VrfInsights.Analysis.Movement;
using VrfInsights.Analysis.Rounds;
using VrfInsights.Analysis.Utility;
using VrfInsights.Analysis.Vision;
using VrfInsights.Data;

namespace VrfInsights.Analysis;

/// <summary>Everything this project can currently derive from one vrfkit export, gathered in
/// one place. Nothing here parses a <c>.vrf</c> file or touches the payload cipher — all of it
/// is built from <see cref="VrfExportSet"/>, i.e. from vrfkit's already-decoded tables.</summary>
public sealed class MatchAnalysis
{
    public required string ReplayBuild { get; init; }
    public required long DurationMs { get; init; }
    public required IReadOnlyList<PlayerIdentity> Players { get; init; }
    public required IReadOnlyList<RoundInfo> Rounds { get; init; }
    public required IReadOnlyList<MatchEvent> Events { get; init; }
    public required IReadOnlyList<PlayerTrack> MovementTracks { get; init; }
    public required IReadOnlyList<PersistentEffectEvent> Utility { get; init; }
    public required IReadOnlyList<AbilityCastEvent> AbilityCasts { get; init; }
    public required IReadOnlyList<UltimateUsageEvent> UltimateUsages { get; init; }
    public required IReadOnlyList<CombatInteraction> CombatInteractions { get; init; }
    public required IReadOnlyList<EconomySnapshot> Economy { get; init; }

    public static MatchAnalysis Build(VrfExportSet export, AgentCatalog agentCatalog, VisionConeOptions? visionOptions = null)
    {
        IReadOnlyList<PlayerIdentity> players = PlayerRegistry.Build(export, agentCatalog);
        IReadOnlyList<RoundInfo> rounds = RoundTimelineBuilder.BuildRounds(export.Events);
        IReadOnlyList<MatchEvent> events = RoundTimelineBuilder.BuildEventTimeline(export.Events, rounds);
        IReadOnlyList<PlayerTrack> tracks = MovementTimelineBuilder.Build(export, players);

        return new MatchAnalysis
        {
            ReplayBuild = export.Manifest.ReplayBuild ?? "(unknown)",
            DurationMs = export.Manifest.DurationMs,
            Players = players,
            Rounds = rounds,
            Events = events,
            MovementTracks = tracks,
            Utility = UtilityTimelineBuilder.Build(export.Actors),
            AbilityCasts = AbilityCastBuilder.Build(export.Fields),
            UltimateUsages = UltimateUsageBuilder.Build(export.Events, rounds),
            CombatInteractions = CombatReportBuilder.Build(export.Fields),
            Economy = EconomySnapshotBuilder.Build(export.Fields),
        };
    }

    /// <summary>Vision cones aren't precomputed onto <see cref="MatchAnalysis"/> itself (they're
    /// one row per movement sample per player, which can be large) — build them on demand per
    /// track with whatever FOV/range you want to visualize.</summary>
    public IReadOnlyList<VisionCone> BuildVisionCones(PlayerTrack track, VisionConeOptions? options = null)
    {
        options ??= new VisionConeOptions();
        return VisionConeCalculator.Build(track, options.FovDegrees, options.RangeCm, options.EyeHeightCm);
    }
}

public sealed record VisionConeOptions(
    double FovDegrees = VisionConeCalculator.DefaultFovDegrees,
    double RangeCm = VisionConeCalculator.DefaultRangeCm,
    double EyeHeightCm = VisionConeCalculator.EyeHeightCm);
