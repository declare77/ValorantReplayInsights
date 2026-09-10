using VrfInsights.Analysis.Common;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Abilities;

/// <summary>
/// Extracts ability casts from <c>Comp_AbilityStatisticsReplicator</c>'s
/// <c>AbilityCastsThisRound</c> replicated array (see vrfkit docs/DATA.md, "Abilities"), which
/// is the one ability signal vrfkit documents as directly attributing a cast to a player, slot,
/// round and location — rather than only observing a caster-side actor spawn.
///
/// <para><b>Timing:</b> per vrfkit's measurement, <c>CastTime</c> is measured from the buy-phase
/// barrier drop, not from <c>events.roundStarted</c> — joining as
/// <c>roundStarted + CastTime</c> lands ~30s early (45s on the first round of each half/overtime).
/// The correct absolute time is <c>roundStarted + buyPhaseLength(round) + CastTime</c>. This
/// builder does not attempt that adjustment for you (buy-phase length depends on which round of
/// which half you're in); it exposes both <see cref="AbilityCastEvent.CastTimeSeconds"/> (raw)
/// and <see cref="AbilityCastEvent.FirstObservedAtMs"/> (the wall-clock time this array entry was
/// first replicated, which is a reasonable proxy for "roughly when this got sent" if you don't
/// need buy-phase-accurate timing).</para>
///
/// <para><b>Deduplication:</b> the array re-sends its whole contents on every later replication,
/// so the same (Round, Slot, Subject) cast can appear many times. This builder keeps only the
/// earliest snapshot per array index per actor, per vrfkit's own recommendation.</para>
///
/// <para><b>CastLocation:</b> this assumes vrfkit flattens the nested FVector as
/// <c>CastLocation.X</c> / <c>.Y</c> / <c>.Z</c> child fields (matching the child-row convention
/// vrfkit documents elsewhere, e.g. <c>LifeChangeEvents[i].LifeResult</c>). That specific
/// sub-field naming was not independently confirmed against a real export while building this —
/// if your export's <c>field_name</c>s for this group use a different suffix, the X/Y/Z here
/// will simply come back null; run <c>vrf-insights dump-fields --group Comp_AbilityStatisticsReplicator</c>
/// (see the CLI) to see the real names and adjust <see cref="VectorMemberSuffixes"/> below.</para>
/// </summary>
public static class AbilityCastBuilder
{
    private const string GroupNameFragment = "Comp_AbilityStatisticsReplicator";
    private const string ArrayName = "AbilityCastsThisRound";

    private static readonly (string X, string Y, string Z) VectorMemberSuffixes = ("CastLocation.X", "CastLocation.Y", "CastLocation.Z");

    public static IReadOnlyList<AbilityCastEvent> Build(IReadOnlyList<FieldRow> fields)
    {
        List<FieldRow> relevant = fields
            .Where(f => f.GroupPath.Contains(GroupNameFragment, StringComparison.Ordinal))
            .ToList();

        IReadOnlyList<FlattenedArrayPivot.ArrayElementSnapshot> snapshots =
            FlattenedArrayPivot.Pivot(relevant, ArrayName);

        // Keep only the earliest snapshot per (actor, index) — later ones are re-sends.
        var earliest = new Dictionary<(long Actor, int Index), FlattenedArrayPivot.ArrayElementSnapshot>();
        foreach (FlattenedArrayPivot.ArrayElementSnapshot snap in snapshots)
        {
            var key = (snap.ActorNetGuid, snap.Index);
            if (!earliest.TryGetValue(key, out FlattenedArrayPivot.ArrayElementSnapshot? existing) || snap.TimeMs < existing.TimeMs)
            {
                earliest[key] = snap;
            }
        }

        var results = new List<AbilityCastEvent>(earliest.Count);
        foreach (FlattenedArrayPivot.ArrayElementSnapshot snap in earliest.Values)
        {
            results.Add(new AbilityCastEvent(
                Subject: snap.GetString("Player"),
                Slot: snap.GetLong("Slot"),
                Round: snap.GetLong("Round"),
                RoundPhase: snap.GetLong("RoundPhase"),
                CastTimeSeconds: snap.GetDouble("CastTime"),
                FirstObservedAtMs: snap.TimeMs,
                CastX: snap.GetDouble(VectorMemberSuffixes.X),
                CastY: snap.GetDouble(VectorMemberSuffixes.Y),
                CastZ: snap.GetDouble(VectorMemberSuffixes.Z)));
        }

        results.Sort((a, b) => a.FirstObservedAtMs.CompareTo(b.FirstObservedAtMs));
        return results;
    }
}
