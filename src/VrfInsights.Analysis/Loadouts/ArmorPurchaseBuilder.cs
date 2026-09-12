using VrfInsights.Analysis.Identity;
using VrfInsights.Analysis.Rounds;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Loadouts;

/// <summary>
/// Extracts armor purchases from <c>actors.parquet</c>'s own actor-open events — no RPC/field
/// decoding needed, since <c>HeavyArmorItem_C</c>/<c>LightArmorItem_C</c>/<c>PlasmaArmorItem_C</c>
/// open their own channel and show up in <c>actors.parquet</c> "purely because they opened a
/// channel," per this project's own pre-existing <see cref="Data.Tables.ActorRow"/> doc comment
/// (which already named <c>HeavyArmorItem</c> as an example of this, before this builder existed).
///
/// <para><b>Confirmed by vrfkit's own docs</b> (<c>docs/DATA.md</c>, "Armour is
/// <c>AttachedDamageSection</c>, not <c>ShieldDamageSection</c>"): the three armor item classes and
/// their max values — Heavy 50, Light 25, "Plasma" 25 (vrfkit's docs don't say what "Plasma"
/// corresponds to in-game beyond the matching 25-point cap, so this project labels it
/// <see cref="ArmorTier.Other"/> rather than guessing "Regen Shield" or similar).</para>
///
/// <para><b>This project's own inference, not a vrfkit statement:</b> that the armor item actor's
/// channel-open time is a reasonable proxy for "when this player bought/equipped this armor" —
/// plausible (armor is typically bought once at the start of a round and equips immediately), but
/// not independently verified against a real replay's buy-phase timing the way, say,
/// <c>AbilityCastEvent</c>'s buy-phase offset was. Ownership itself is resolved by walking
/// <c>net_guids.parquet</c>'s <c>OuterNetGuid</c> chain from the armor item's own
/// <c>ActorNetGuid</c> up to a known player's <c>ActorNetGuid</c>/<c>CharacterNetGuid</c> — the
/// exact technique <see cref="Data.Tables.NetGuidRow"/>'s own doc comment already describes
/// generically ("walk <c>OuterNetGuid</c> to find the owning actor"), applied here for the first
/// time in this codebase.</para>
///
/// <para><b>What this does NOT attempt:</b> live remaining-armor tracking as it absorbs damage
/// (that needs decoding <c>MulticastNotifyDamage</c>'s raw <c>LifeChangeEvents[]</c> array, which
/// <see cref="Combat.DamageHitBuilder"/> deliberately skips) — only "which tier did they have
/// equipped, and since when."</para>
/// </summary>
public static class ArmorPurchaseBuilder
{
    private const int MaxOuterChainDepth = 8;

    public static IReadOnlyList<ArmorPurchase> Build(
        IReadOnlyList<ActorRow> actors,
        IReadOnlyList<NetGuidRow> netGuids,
        IReadOnlyList<PlayerIdentity> players,
        IReadOnlyList<RoundInfo> rounds)
    {
        var outerByGuid = new Dictionary<long, long?>();
        foreach (NetGuidRow row in netGuids)
        {
            if (!outerByGuid.ContainsKey(row.NetGuid))
            {
                outerByGuid[row.NetGuid] = row.OuterNetGuid;
            }
        }

        var knownActorGuids = new HashSet<long>();
        var knownCharacterToActor = new Dictionary<long, long>();
        foreach (PlayerIdentity p in players)
        {
            knownActorGuids.Add(p.ActorNetGuid);
            if (p.CharacterNetGuid.HasValue)
            {
                knownCharacterToActor[p.CharacterNetGuid.Value] = p.ActorNetGuid;
            }
        }

        var results = new List<ArmorPurchase>();
        foreach (ActorRow actor in actors)
        {
            if (!actor.IsOpen || actor.ClassPath is null)
            {
                continue;
            }

            (ArmorTier Tier, double Max)? info = ClassifyArmor(actor.ClassPath);
            if (info is null)
            {
                continue;
            }

            long? owner = ResolveOwner(actor.ActorNetGuid, outerByGuid, knownActorGuids, knownCharacterToActor);
            if (owner is null)
            {
                continue;
            }

            int? roundNumber = null;
            foreach (RoundInfo round in rounds)
            {
                if (round.Contains(actor.TimeMs))
                {
                    roundNumber = round.RoundNumber;
                    break;
                }
            }

            results.Add(new ArmorPurchase(owner.Value, actor.TimeMs, roundNumber, info.Value.Tier, info.Value.Max, actor.ClassPath));
        }

        results.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return results;
    }

    private static (ArmorTier Tier, double Max)? ClassifyArmor(string classPath)
    {
        if (classPath.Contains("HeavyArmorItem_C", StringComparison.Ordinal)) return (ArmorTier.Heavy, 50.0);
        if (classPath.Contains("LightArmorItem_C", StringComparison.Ordinal)) return (ArmorTier.Light, 25.0);
        if (classPath.Contains("PlasmaArmorItem_C", StringComparison.Ordinal)) return (ArmorTier.Other, 25.0);
        return null;
    }

    /// <summary>Walks <see cref="NetGuidRow.OuterNetGuid"/> from <paramref name="startGuid"/>
    /// until it lands on a known player's <c>ActorNetGuid</c>/<c>CharacterNetGuid</c>, the chain
    /// runs out, or <see cref="MaxOuterChainDepth"/> is hit (a safety bound against an
    /// unexpected cycle — not expected in practice, since Unreal's outer chain is a tree).</summary>
    private static long? ResolveOwner(
        long startGuid,
        Dictionary<long, long?> outerByGuid,
        HashSet<long> knownActorGuids,
        Dictionary<long, long> knownCharacterToActor)
    {
        long current = startGuid;
        for (int depth = 0; depth < MaxOuterChainDepth; depth++)
        {
            if (knownActorGuids.Contains(current))
            {
                return current;
            }

            if (knownCharacterToActor.TryGetValue(current, out long viaCharacter))
            {
                return viaCharacter;
            }

            if (!outerByGuid.TryGetValue(current, out long? outer) || outer is not long next)
            {
                return null;
            }

            current = next;
        }

        return null;
    }
}
