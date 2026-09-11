namespace VrfInsights.Analysis.Abilities;

/// <param name="Subject">Account UUID of the caster (the array element's <c>Player</c> field).</param>
/// <param name="Slot">Ability slot index as VALORANT's client reports it (raw — this project
/// doesn't assume a fixed slot-to-C/Q/E/X mapping since that hasn't been independently
/// confirmed here).</param>
/// <param name="Round">Round number the cast belongs to, per the replicated array element
/// itself (not derived from <c>events.roundStarted</c>).</param>
/// <param name="CastTimeSeconds">Raw <c>CastTime</c> as replicated. This is seconds
/// <b>since the buy-phase barrier drop, not since round start</b> — see
/// <see cref="AbilityCastBuilder"/> remarks before treating it as an absolute time.</param>
/// <param name="FirstObservedAtMs">The earliest <c>time_ms</c> this array element was seen at —
/// use this as "when the cast entry first appeared" rather than <see cref="CastTimeSeconds"/>
/// directly, since the array re-sends on every later replication and later sends carry a
/// growing, no-longer-meaningful timestamp for the original cast (per vrfkit's docs/DATA.md
/// "Abilities" section).</param>
/// <param name="CastX">Null if this export's <c>CastLocation</c> member is missing or isn't in
/// the <c>(X,Y,Z)</c> string format this project has confirmed against a real export — see
/// <see cref="AbilityCastBuilder"/>.</param>
public sealed record AbilityCastEvent(
    string? Subject,
    long? Slot,
    long? Round,
    long? RoundPhase,
    double? CastTimeSeconds,
    long FirstObservedAtMs,
    double? CastX,
    double? CastY,
    double? CastZ);
