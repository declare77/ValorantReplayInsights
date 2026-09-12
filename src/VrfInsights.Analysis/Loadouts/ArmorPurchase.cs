namespace VrfInsights.Analysis.Loadouts;

/// <summary>Which armor tier a purchase resolved to. <see cref="Other"/> covers
/// <c>PlasmaArmorItem_C</c> — vrfkit's docs confirm its 25-point max (matching light armor's) but
/// don't say what in-game concept it corresponds to, so this project doesn't relabel it as
/// "Light" on an unconfirmed guess.</summary>
public enum ArmorTier
{
    Light,
    Heavy,
    Other,
}

/// <summary>
/// One armor item actor opening a channel for a player — read as "this player bought/equipped
/// this armor tier at this time," per <see cref="ArmorPurchaseBuilder"/>'s doc comment for exactly
/// how confident to be in that reading. This is a purchase EVENT, not a live remaining-armor
/// value — this project doesn't attempt to track armor depletion from combat (that would need
/// decoding <c>MulticastNotifyDamage</c>'s raw <c>LifeChangeEvents[]</c> array, which
/// <see cref="Combat.DamageHitBuilder"/> deliberately doesn't do yet).
/// </summary>
/// <param name="ActorNetGuid">The owning player, resolved from the armor item actor's
/// <c>net_guids.parquet</c> outer-actor chain.</param>
/// <param name="TimeMs">When the armor item actor opened its channel (its purchase/equip
/// moment).</param>
/// <param name="RoundNumber">Which round this happened in, if it falls inside one of
/// <see cref="Rounds.RoundInfo"/>'s known windows.</param>
/// <param name="Tier">Light/Heavy/Other — see <see cref="ArmorTier"/>.</param>
/// <param name="MaxArmor">The tier's full point value (50 for Heavy, 25 for Light/Other), per
/// vrfkit's own confirmed measurement, not something this project computed.</param>
/// <param name="ClassPath">The armor item actor's raw <c>class_path</c>, kept for anyone who wants
/// to check <see cref="Tier"/>'s classification themselves.</param>
public sealed record ArmorPurchase(
    long ActorNetGuid,
    long TimeMs,
    int? RoundNumber,
    ArmorTier Tier,
    double MaxArmor,
    string ClassPath);
