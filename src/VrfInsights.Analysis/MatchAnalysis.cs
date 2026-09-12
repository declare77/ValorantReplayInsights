using VrfInsights.Analysis.Abilities;
using VrfInsights.Analysis.Combat;
using VrfInsights.Analysis.Identity;
using VrfInsights.Analysis.Loadouts;
using VrfInsights.Analysis.Movement;
using VrfInsights.Analysis.Rounds;
using VrfInsights.Analysis.Utility;
using VrfInsights.Analysis.Vision;
using VrfInsights.Analysis.Weapons;
using VrfInsights.Data;

namespace VrfInsights.Analysis;

/// <summary>Everything this project can currently derive from one vrfkit export, gathered in
/// one place. Nothing here parses a <c>.vrf</c> file or touches the payload cipher — all of it
/// is built from <see cref="VrfExportSet"/>, i.e. from vrfkit's already-decoded tables.</summary>
public sealed class MatchAnalysis
{
    public required string ReplayBuild { get; init; }
    public required long DurationMs { get; init; }
    /// <summary>Best-effort map detection (see <see cref="Identity.MapDetector"/>) — null when
    /// the map's asset path couldn't be found anywhere in this export. A 2D replay viewer should
    /// let the user pick the map manually in that case rather than assume one.</summary>
    public required MapInfo? Map { get; init; }
    public required IReadOnlyList<PlayerIdentity> Players { get; init; }
    public required IReadOnlyList<RoundInfo> Rounds { get; init; }
    public required IReadOnlyList<MatchEvent> Events { get; init; }
    /// <summary>Per-round attacker/defender rosters (see <see cref="Rounds.TeamSideResolver"/>) --
    /// empty when the replay didn't have enough evidence to determine sides, in which case a
    /// viewer should fall back to per-player coloring rather than guessing.</summary>
    public required IReadOnlyList<RoundSides> Sides { get; init; }
    public required IReadOnlyList<PlayerTrack> MovementTracks { get; init; }
    public required IReadOnlyList<PersistentEffectEvent> Utility { get; init; }
    public required IReadOnlyList<AbilityCastEvent> AbilityCasts { get; init; }
    public required IReadOnlyList<UltimateUsageEvent> UltimateUsages { get; init; }
    public required IReadOnlyList<CombatInteraction> CombatInteractions { get; init; }
    public required IReadOnlyList<EconomySnapshot> Economy { get; init; }
    /// <summary>Per-shot events (see <see cref="Weapons.ShotFiredBuilder"/>'s doc comment for how
    /// confident to be in this — unlike most of the rest of this class, NOT yet confirmed against
    /// a real decoded export).</summary>
    public required IReadOnlyList<ShotFiredEvent> Shots { get; init; }

    public static MatchAnalysis Build(VrfExportSet export, AgentCatalog agentCatalog, VisionConeOptions? visionOptions = null) =>
        Build(export, agentCatalog, MapCatalog.LoadEmbedded(), visionOptions);

    public static MatchAnalysis Build(VrfExportSet export, AgentCatalog agentCatalog, MapCatalog mapCatalog, VisionConeOptions? visionOptions = null)
    {
        IReadOnlyList<PlayerIdentity> players = PlayerRegistry.Build(export, agentCatalog);
        IReadOnlyList<RoundInfo> rounds = RoundTimelineBuilder.BuildRounds(export.Events);
        IReadOnlyList<MatchEvent> events = RoundTimelineBuilder.BuildEventTimeline(export.Events, rounds);
        IReadOnlyList<PlayerTrack> tracks = MovementTimelineBuilder.Build(export, players);
        MapInfo? map = MapDetector.Detect(export, mapCatalog);

        return new MatchAnalysis
        {
            ReplayBuild = export.Manifest.ReplayBuild ?? "(unknown)",
            DurationMs = export.Manifest.DurationMs,
            Map = map,
            Players = players,
            Rounds = rounds,
            Events = events,
            Sides = TeamSideResolver.Build(export, players, rounds, events),
            MovementTracks = tracks,
            Utility = UtilityTimelineBuilder.Build(export.Actors),
            AbilityCasts = AbilityCastBuilder.Build(export.Fields),
            UltimateUsages = UltimateUsageBuilder.Build(export.Events, rounds),
            CombatInteractions = CombatReportBuilder.Build(export.Fields),
            Economy = EconomySnapshotBuilder.Build(export.Fields),
            Shots = ShotFiredBuilder.Build(export.Fields, GameplayTagTable.Build(export.Manifest.NetFieldExportGroups)),
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
