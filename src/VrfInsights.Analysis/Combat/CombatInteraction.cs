namespace VrfInsights.Analysis.Combat;

/// <summary>
/// One <c>CombatReport</c> interaction — VALORANT's own per-round, per-participant combat
/// summary (damage dealt/received, hits, kill/assist), which vrfkit's README documents as
/// "the sole source of K/D/A, ADR, HS%, multi-kills, and wallbangs" and reports as
/// multiset-identical against the existing C# reference parser.
///
/// This is a per-round aggregate (hits/damage against one opponent for the round), not an
/// individual shot event — VALORANT's replay format doesn't appear to expose a discrete
/// "bullet fired" record; <see cref="HitsDealt"/>/<see cref="HitsReceived"/> and the regional
/// breakdown are the closest thing to shot-level detail vrfkit documents extracting.
/// </summary>
/// <param name="RoundIndex">Index into the <c>Rounds[]</c> array (0-based, as replicated —
/// not necessarily the same numbering as <c>events.roundStarted</c>'s round number).</param>
/// <param name="ReportIndex">Index into that round's <c>Reports[]</c> array.</param>
/// <param name="InteractionIndex">Index into that report's <c>Interactions[]</c> array.</param>
/// <param name="RawMembers">Every other decoded member under this interaction, keyed by its
/// path suffix (e.g. <c>DealtInteractions[0].Regions[0].Hits</c>), for anything not promoted to
/// a named property above.</param>
public sealed record CombatInteraction(
    long ActorNetGuid,
    int RoundIndex,
    int ReportIndex,
    int InteractionIndex,
    long TimeMs,
    double? DamageDealt,
    double? DamageReceived,
    long? HitsDealt,
    long? HitsReceived,
    bool? DidKill,
    long? AssistType,
    bool? IsWallPen,
    IReadOnlyDictionary<string, object?> RawMembers);
