using System.Globalization;
using System.Text.RegularExpressions;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Common;

/// <summary>
/// vrfkit flattens replicated arrays into <c>fields.parquet</c>'s <c>field_name</c> column,
/// e.g. <c>AbilityCastsThisRound[2].CastLocation</c> or
/// <c>Rounds[3].Reports[1].Interactions[0].DamageDealt</c> (see vrfkit README, "Arrays are
/// flattened"). This turns a top-level array back into one grouped snapshot per element per
/// replication event, without needing to know every member name up front.
/// </summary>
public static class FlattenedArrayPivot
{
    /// <param name="Index">The array index this snapshot came from.</param>
    /// <param name="ActorNetGuid">The owning actor, carried through from the source rows.</param>
    /// <param name="ChannelIndex">The owning actor channel, carried through from the source rows.</param>
    /// <param name="TimeMs">When this particular snapshot of the element was replicated. A
    /// replicated array can be re-sent wholesale later in the round (vrfkit's docs note this
    /// explicitly for <c>AbilityCastsThisRound</c>), so the same Index can appear in multiple
    /// snapshots — take the earliest TimeMs per Index if you want "first observed", not every
    /// resend.</param>
    /// <param name="Members">Remaining path (after <c>Name[Index].</c>) to the row carrying that
    /// member's value. For a further-nested member (as in the CombatReport example above) the
    /// key still contains its own <c>[N]</c> segments — pivot again on that key prefix if you
    /// need to descend another level.</param>
    public sealed record ArrayElementSnapshot(
        int Index,
        long ActorNetGuid,
        long ChannelIndex,
        long TimeMs,
        IReadOnlyDictionary<string, FieldRow> Members);

    private static readonly Regex TopLevelIndexPattern = new(@"^\[(\d+)\]\.(.+)$", RegexOptions.Compiled);

    public static IReadOnlyList<ArrayElementSnapshot> Pivot(IEnumerable<FieldRow> rows, string arrayName)
    {
        string prefix = arrayName + "[";
        var groups = new Dictionary<(long Actor, long Channel, int Index, long TimeMs), Dictionary<string, FieldRow>>();

        foreach (FieldRow row in rows)
        {
            if (row.FieldName is null || !row.FieldName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string remainder = row.FieldName[arrayName.Length..];
            Match match = TopLevelIndexPattern.Match(remainder);
            if (!match.Success)
            {
                continue;
            }

            int index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            string member = match.Groups[2].Value;
            var key = (row.ActorNetGuid, row.ChannelIndex, index, row.TimeMs);

            if (!groups.TryGetValue(key, out Dictionary<string, FieldRow>? members))
            {
                members = new Dictionary<string, FieldRow>();
                groups[key] = members;
            }

            members[member] = row;
        }

        var result = new List<ArrayElementSnapshot>(groups.Count);
        foreach (KeyValuePair<(long Actor, long Channel, int Index, long TimeMs), Dictionary<string, FieldRow>> kvp in groups)
        {
            result.Add(new ArrayElementSnapshot(kvp.Key.Index, kvp.Key.Actor, kvp.Key.Channel, kvp.Key.TimeMs, kvp.Value));
        }

        return result;
    }

    /// <summary>Convenience accessor: the decoded overlay value of a member, or null if that
    /// member wasn't present in the snapshot or has no typed overlay (only raw bits).</summary>
    public static object? GetValue(this ArrayElementSnapshot snapshot, string member) =>
        snapshot.Members.TryGetValue(member, out FieldRow? row) ? row.Value : null;

    public static string? GetString(this ArrayElementSnapshot snapshot, string member) =>
        snapshot.GetValue(member) as string;

    public static double? GetDouble(this ArrayElementSnapshot snapshot, string member) =>
        snapshot.GetValue(member) switch
        {
            double d => d,
            long l => l,
            _ => null
        };

    public static long? GetLong(this ArrayElementSnapshot snapshot, string member) =>
        snapshot.GetValue(member) switch
        {
            long l => l,
            double d => (long)d,
            _ => null
        };

    public static bool? GetBool(this ArrayElementSnapshot snapshot, string member) =>
        snapshot.GetValue(member) as bool?;
}
