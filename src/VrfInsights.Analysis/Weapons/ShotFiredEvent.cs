using VrfInsights.Analysis.Common;

namespace VrfInsights.Analysis.Weapons;

/// <summary>
/// One decoded <c>ReplayPlayContinuousEffectAtLocation</c> RPC call — vrfkit's own signal for an
/// individual weapon shot, as opposed to <see cref="Combat.CombatInteraction"/>'s per-round,
/// per-opponent damage/hit aggregate (which has no per-shot timing or direction at all).
///
/// <para><b>NOT yet confirmed against a real decoded export</b> — everything here comes from
/// reading vrfkit's own Rust/Python source directly (see <see cref="ShotFiredBuilder"/>'s doc
/// comment for exactly which files), the same way <c>AbilityCastsThisRound</c>'s member names
/// were before a real <c>dump-fields</c> run confirmed them. Treat every field here as a sourced,
/// plausible assumption — worth checking your own export's <c>shots.json</c> against
/// <c>dump-fields --field ReplayPlayContinuousEffectAtLocation</c> before trusting blindly.</para>
/// </summary>
/// <param name="ActorNetGuid">The actor these <c>fields.parquet</c> rows were tagged with — not
/// independently confirmed whether this identifies the firing player, their weapon, or some other
/// actor in the ownership chain; matching this against <see cref="Identity.PlayerIdentity"/> and
/// checking it lines up with who was actually shooting at that timestamp (e.g. against movement
/// and aim direction) is the first thing to verify.</param>
/// <param name="TimeMs">When this RPC was replicated.</param>
/// <param name="AmmoRemaining">Magazine ammo remaining after this shot, from the blob's
/// <c>FiringState.AmmoRemaining</c> tag, if present.</param>
/// <param name="NumProjectiles">Pellets/projectiles fired in this one shot (1 for most weapons,
/// more for a shotgun), from <c>FiringState.NumProjectiles</c>.</param>
/// <param name="RandomSeed">The shot's spread RNG seed, from <c>FiringState.RandomSeed</c> —
/// exposed verbatim; this project doesn't attempt to reproduce VALORANT's spread-cone math from it.</param>
/// <param name="TracerOption">Raw <c>FiringState.TracerOption</c> value — meaning not
/// independently confirmed.</param>
/// <param name="BurstShotNumber">Raw <c>FiringState.BurstShotNumber</c> — vrfkit's own reference
/// tooling reportedly uses this to distinguish primary/alternate fire, but this project doesn't
/// classify fire mode from it (yet).</param>
/// <param name="AttackVectors">One direction vector per projectile fired (so more than one for a
/// shotgun), from <c>FiringState.AttackVector.1</c>..<c>.15</c>. This is a firing *direction*
/// (a unit-ish vector from wherever the shooter's camera/muzzle was), not a hit point or a target
/// actor — VALORANT's guns are hitscan, so there's no real "travel time" to animate accurately,
/// only a direction and moment to draw a stylized tracer along. A named record, not a
/// <c>ValueTuple</c> — <c>System.Text.Json</c> only serializes public properties by default, and a
/// <c>ValueTuple</c>'s <c>Item1</c>/<c>Item2</c>/<c>Item3</c> are fields, so a list of tuples here
/// would silently serialize as a list of empty objects.</param>
/// <param name="FiringPlayerStateNetGuid">Raw object reference from the blob's
/// <c>FiringState.FiringPlayerState</c> tag, if present — not yet resolved to a player identity by
/// <see cref="ShotFiredBuilder"/>.</param>
/// <param name="FiringStateNetGuid">Raw object reference from <c>FiringState.FiringState</c>, if
/// present — the likely path to the actual weapon/equippable actor via
/// <c>net_guids.parquet</c>'s <see cref="Data.Tables.NetGuidRow.OuterNetGuid"/> chain, not yet
/// resolved here (turning this into a real weapon name needs a weapon-class-path codename table
/// this project doesn't have confirmed evidence for, the same kind of gap the ticker's "current
/// weapon loadout" already documents as out of scope for now).</param>
/// <param name="RawTags">Every other resolved <c>{tagName: value}</c> pair from all three decoded
/// blobs (float/object/vector), for anything not promoted to a field above.</param>
///
/// <remarks>As of this writing, unverified against a real export and not producing visible output
/// for at least one user — see <see cref="Combat.DamageHitEvent"/> for a second, much
/// better-evidenced signal (vrfkit's own docs mark it ✅) built afterward specifically because this
/// one didn't pan out; the viewer now draws its shot-tracer animation from that instead, and only
/// falls back to this one if a replay's <c>shots.json</c> happens to have entries.</remarks>
public sealed record ShotFiredEvent(
    long ActorNetGuid,
    long TimeMs,
    double? AmmoRemaining,
    double? NumProjectiles,
    double? RandomSeed,
    double? TracerOption,
    double? BurstShotNumber,
    IReadOnlyList<Vec3> AttackVectors,
    long? FiringPlayerStateNetGuid,
    long? FiringStateNetGuid,
    IReadOnlyDictionary<string, object?> RawTags);
