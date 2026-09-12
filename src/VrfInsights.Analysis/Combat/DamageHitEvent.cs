using VrfInsights.Analysis.Common;

namespace VrfInsights.Analysis.Combat;

/// <summary>
/// One decoded <c>MulticastNotifyDamage_Point</c> RPC call — a single landed hit, with a real
/// impact location, direction, and (often) which weapon dealt it. Built as a second, much
/// better-evidenced alternative to <see cref="Weapons.ShotFiredEvent"/> after that one turned out
/// not to produce visible output for at least one real replay — see <see cref="DamageHitBuilder"/>'s
/// doc comment for exactly what's confirmed by vrfkit's own docs/source vs. still an assumption
/// here.
/// </summary>
/// <param name="VictimActorNetGuid">The actor these <c>fields.parquet</c> rows were tagged with —
/// per vrfkit's <c>DamageableComponent</c> design (this RPC lives on the component that tracks a
/// character's own health), this should be the player who TOOK the hit. Strongly evidenced by
/// analogy with vrfkit's own healing-observation tooling (which explicitly labels the equivalent
/// field "recipient" for the sibling heal RPC), not a sentence vrfkit's docs state verbatim for
/// this specific RPC.</param>
/// <param name="TimeMs">When this hit was replicated.</param>
/// <param name="DamageDealt">Raw damage amount before armor absorption, if present.</param>
/// <param name="DamageTaken">Raw damage amount actually taken (after armor absorption), if
/// present.</param>
/// <param name="DeltaLife">Net health change from this hit (mirrors <c>LifeChangeEvents[]</c>'s
/// per-element field of the same name, promoted here since it's the simplest single number for
/// "how much this hit hurt").</param>
/// <param name="BDamageKilledTarget">Whether this specific hit was the killing blow.</param>
/// <param name="AttackerActorNetGuid">Best-effort attacker identity — see
/// <see cref="DamageHitBuilder"/>'s doc comment for the resolution order tried (several raw
/// reference fields on this RPC could plausibly identify the attacker; this project has evidence
/// but not proof for which one is authoritative) and <see cref="AttackerCandidates"/> for every
/// candidate this was resolved from, in case a future fix needs to pick a different one.</param>
/// <param name="AttackerCandidates">Every raw attacker-side reference this RPC carried
/// (<c>DamageCauser</c>, <c>EventInstigator</c>, <c>EventInstigatorPawn</c>,
/// <c>DamagerPlayerState</c>, <c>KillCreditPlayerState</c>), each as a raw NetGUID, before any
/// attempt to resolve one to a known player — kept around for diagnosing a wrong attacker
/// attribution without re-running the analyzer.</param>
/// <param name="WeaponClassPath">The firing weapon's <c>actors.parquet</c> <c>class_path</c>,
/// resolved from this RPC's <c>EquippableUsed</c> reference, if it resolved to an actor at all.</param>
/// <param name="WeaponDisplayName">The real weapon name for <see cref="WeaponClassPath"/>, via
/// <see cref="Weapons.WeaponCatalog"/> — null when the class path didn't match any known weapon
/// (could be a genuinely new/unrecognized weapon, ability damage with no traditional gun, or a
/// class-path format this project's substring match doesn't expect).</param>
/// <param name="ImpactLocation">World-space point the hit landed, if this RPC's
/// <c>DamageImpactLocation</c> parsed successfully.</param>
/// <param name="ImpactDirection">The direction damage came from, if this RPC's
/// <c>DamageDirection</c> parsed successfully — used to draw the tracer's origin side of the line
/// when the attacker's own position isn't resolved.</param>
/// <param name="DamagedBone">Raw bone name the hit landed on, if present (e.g. head/body/leg
/// region, VALORANT-internal naming).</param>
/// <param name="IsWallPenetration">Whether this hit penetrated a wall, if the flag was present.</param>
public sealed record DamageHitEvent(
    long VictimActorNetGuid,
    long TimeMs,
    double? DamageDealt,
    double? DamageTaken,
    double? DeltaLife,
    bool? BDamageKilledTarget,
    long? AttackerActorNetGuid,
    AttackerCandidateSet AttackerCandidates,
    string? WeaponClassPath,
    string? WeaponDisplayName,
    Vec3? ImpactLocation,
    Vec3? ImpactDirection,
    string? DamagedBone,
    bool? IsWallPenetration);

/// <summary>Every raw attacker-side reference a <see cref="DamageHitEvent"/> carried, before any
/// attempt to resolve one to a known player — see that record's doc comment.</summary>
public sealed record AttackerCandidateSet(
    long? DamageCauser,
    long? EventInstigator,
    long? EventInstigatorPawn,
    long? DamagerPlayerState,
    long? KillCreditPlayerState);
