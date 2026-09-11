using System.Globalization;
using VrfInsights.Analysis.Common;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Abilities;

/// <summary>
/// Extracts ability casts from <c>Comp_AbilityStatisticsReplicator</c>'s
/// <c>AbilityCastsThisRound</c> replicated array (see vrfkit docs/DATA.md, "Abilities"), which
/// is the one ability signal vrfkit documents as directly attributing a cast to a player, slot,
/// round and location — rather than only observing a caster-side actor spawn.
///
/// <para><b>Member naming, confirmed against a real export via <c>vrf-insights dump-fields</c>:</b>
/// every leaf member of this array comes through with vrfkit's own disambiguation suffix appended
/// to the readable name, e.g. <c>Player_11_0963330440D68BDF1A8E34B035420342</c> or
/// <c>CastLocation_21_61F4B6BC47A10FE8CD34D29141FC9B88</c> — the trailing <c>_&lt;number&gt;_&lt;hex&gt;</c>
/// isn't stable across builds, so this looks members up by matching the readable prefix
/// (<see cref="FindMember"/>) rather than the full field name. An earlier version of this file
/// assumed <c>CastLocation</c> itself flattened into <c>.X</c>/<c>.Y</c>/<c>.Z</c> child fields —
/// confirmed wrong the same way (<c>vrf-insights dump-values --field CastLocation</c> against a
/// real export): it's one field, not three.</para>
///
/// <para><b>CastLocation's actual encoding,</b> also confirmed via <c>dump-values</c>: it comes
/// through as a single string-valued field formatted like <c>(1042.06,3786.68,282.13)</c> — a bare
/// parenthesized, comma-separated (X,Y,Z), no axis labels. <see cref="ParseVector3"/> parses that
/// directly; there is no separate X/Y/Z member to look up.</para>
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
/// </summary>
public static class AbilityCastBuilder
{
    private const string GroupNameFragment = "Comp_AbilityStatisticsReplicator";
    private const string ArrayName = "AbilityCastsThisRound";

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
            (double X, double Y, double Z)? location = ParseVector3(FindMember(snap, "CastLocation")?.Value as string);

            results.Add(new AbilityCastEvent(
                Subject: FindMember(snap, "Player")?.Value as string,
                Slot: AsLong(FindMember(snap, "Slot")?.Value),
                Round: AsLong(FindMember(snap, "Round")?.Value),
                RoundPhase: AsLong(FindMember(snap, "RoundPhase")?.Value),
                CastTimeSeconds: AsDouble(FindMember(snap, "CastTime")?.Value),
                FirstObservedAtMs: snap.TimeMs,
                CastX: location?.X,
                CastY: location?.Y,
                CastZ: location?.Z));
        }

        results.Sort((a, b) => a.FirstObservedAtMs.CompareTo(b.FirstObservedAtMs));
        return results;
    }

    /// <summary>Finds a member by its readable name, matching either an exact key (in case a
    /// future/other build emits a clean name with no suffix) or vrfkit's
    /// <c>&lt;name&gt;_&lt;number&gt;_&lt;hash&gt;</c> disambiguation suffix.</summary>
    private static FieldRow? FindMember(FlattenedArrayPivot.ArrayElementSnapshot snapshot, string readableName)
    {
        foreach (KeyValuePair<string, FieldRow> member in snapshot.Members)
        {
            if (member.Key == readableName || member.Key.StartsWith(readableName + "_", StringComparison.Ordinal))
            {
                return member.Value;
            }
        }

        return null;
    }

    /// <summary>Parses vrfkit's bare <c>(X,Y,Z)</c> vector-string format (no axis labels, unlike
    /// Unreal's usual <c>FVector::ToString()</c> which would read <c>X=.. Y=.. Z=..</c>) —
    /// confirmed via <c>vrf-insights dump-values --field CastLocation</c> against a real export.
    /// Returns null for anything that doesn't match, rather than throwing, since a differently
    /// formatted export should degrade to "no location" the same way a missing field does.</summary>
    private static (double X, double Y, double Z)? ParseVector3(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        string trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')')
        {
            trimmed = trimmed[1..^1];
        }

        string[] parts = trimmed.Split(',');
        if (parts.Length != 3) return null;

        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            return (x, y, z);
        }

        return null;
    }

    private static long? AsLong(object? value) => value switch { long l => l, double d => (long)d, _ => (long?)null };

    private static double? AsDouble(object? value) => value switch { double d => d, long l => l, _ => (double?)null };
}
