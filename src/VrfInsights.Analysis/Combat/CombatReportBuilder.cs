using System.Globalization;
using System.Text.RegularExpressions;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Combat;

/// <summary>
/// Extracts <c>CombatReport</c> interactions from <c>fields.parquet</c>. This is a three-level
/// nested array (<c>Rounds[r].Reports[p].Interactions[i].&lt;member&gt;</c>) rather than the
/// single-level arrays <see cref="Common.FlattenedArrayPivot"/> handles, so it parses the full
/// path directly instead of pivoting twice.
///
/// <para><b>Fixed: member names need suffix-stripped prefix matching, same as
/// <see cref="Abilities.AbilityCastBuilder"/>.</b> This previously looked up members by an exact
/// key ("DamageDealt", "DidKill", ...), but <see cref="Abilities.AbilityCastBuilder"/> found
/// (confirmed via <c>dump-fields</c> against a real export) that vrfkit appends its own
/// <c>_&lt;number&gt;_&lt;hash&gt;</c> disambiguation suffix to every leaf member of a flattened
/// array — so an exact-name lookup here would silently and permanently miss every member,
/// leaving <see cref="CombatInteraction.DidKill"/>/<see cref="CombatInteraction.AssistType"/>
/// (and, most likely, <see cref="CombatInteraction.DamageDealt"/>/<see cref="CombatInteraction.HitsDealt"/>
/// etc. too) always null — which is indistinguishable from "nobody ever gets credited with a
/// kill" downstream. That fix was applied to <c>AbilityCastsThisRound</c> when it was found there,
/// but never carried over here, since nothing had surfaced this specific report as broken yet.
/// This applies the identical fix (suffix-tolerant prefix matching, see <see cref="FindMember"/>).
/// </para>
///
/// <para><b>Still not independently confirmed against a real export</b> (unlike
/// <c>AbilityCastsThisRound</c>'s member names, which were): whether <c>DamageDealt</c>/
/// <c>HitsDealt</c>/etc. truly are flat scalar members directly under
/// <c>Interactions[i]</c>, or whether some of them are nested a level deeper — e.g. under
/// per-opponent <c>DealtInteractions[j]</c>/<c>ReceivedInteractions[j]</c> sub-arrays, the way
/// <see cref="Common.FlattenedArrayPivot"/>'s own doc comment flags as possible ("pivot again on
/// that key prefix if you need to descend another level"). If <c>DidKill</c>/<c>AssistType</c>
/// still come through null after this fix, that's the next thing to check — run
/// <c>dump-fields --group CombatReport</c> against a real export and look at the actual
/// <c>field_name</c> values under one <c>Interactions[i]</c> to see whether they nest further.
/// This builder also has no notion of *which opponent* an interaction was against — nothing here
/// promotes a target/opponent actor identifier, because none has been confirmed to exist as a
/// distinct field yet; <see cref="CombatInteraction.RawMembers"/> carries every other decoded
/// member verbatim specifically so that, once dump-fields output shows what's actually there
/// (an opponent NetGUID, most likely), extracting it is a matter of promoting one more member
/// here rather than re-deriving the whole structure.</para>
/// </summary>
public static class CombatReportBuilder
{
    private static readonly Regex InteractionPattern = new(
        @"^Rounds\[(\d+)\]\.Reports\[(\d+)\]\.Interactions\[(\d+)\]\.(.+)$",
        RegexOptions.Compiled);

    public static IReadOnlyList<CombatInteraction> Build(IReadOnlyList<FieldRow> fields)
    {
        var groups = new Dictionary<(long Actor, int Round, int Report, int Interaction), (long TimeMs, Dictionary<string, FieldRow> Members)>();

        foreach (FieldRow row in fields)
        {
            if (row.FieldName is null || !row.GroupPath.Contains("CombatReport", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = InteractionPattern.Match(row.FieldName);
            if (!match.Success)
            {
                continue;
            }

            int roundIndex = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int reportIndex = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int interactionIndex = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            string member = match.Groups[4].Value;

            var key = (row.ActorNetGuid, roundIndex, reportIndex, interactionIndex);
            if (!groups.TryGetValue(key, out (long TimeMs, Dictionary<string, FieldRow> Members) entry))
            {
                entry = (row.TimeMs, new Dictionary<string, FieldRow>());
                groups[key] = entry;
            }

            entry.Members[member] = row;
            if (row.TimeMs < entry.TimeMs)
            {
                groups[key] = (row.TimeMs, entry.Members);
            }
        }

        var results = new List<CombatInteraction>(groups.Count);
        foreach (KeyValuePair<(long Actor, int Round, int Report, int Interaction), (long TimeMs, Dictionary<string, FieldRow> Members)> kvp in groups)
        {
            Dictionary<string, FieldRow> members = kvp.Value.Members;
            var raw = new Dictionary<string, object?>();
            foreach (KeyValuePair<string, FieldRow> m in members)
            {
                raw[m.Key] = m.Value.Value;
            }

            results.Add(new CombatInteraction(
                ActorNetGuid: kvp.Key.Actor,
                RoundIndex: kvp.Key.Round,
                ReportIndex: kvp.Key.Report,
                InteractionIndex: kvp.Key.Interaction,
                TimeMs: kvp.Value.TimeMs,
                DamageDealt: GetDouble(members, "DamageDealt"),
                DamageReceived: GetDouble(members, "DamageReceived"),
                HitsDealt: GetLong(members, "HitsDealt"),
                HitsReceived: GetLong(members, "HitsReceived"),
                DidKill: GetBool(members, "DidKill"),
                AssistType: GetLong(members, "AssistType"),
                IsWallPen: GetBool(members, "bIsWallPen"),
                RawMembers: raw));
        }

        results.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return results;
    }

    /// <summary>Finds a member by its readable name, matching either an exact key (in case a
    /// future/other build emits a clean name with no suffix) or vrfkit's
    /// <c>&lt;name&gt;_&lt;number&gt;_&lt;hash&gt;</c> disambiguation suffix — same approach as
    /// <see cref="Abilities.AbilityCastBuilder.FindMember"/>, see this class's doc comment.</summary>
    private static FieldRow? FindMember(Dictionary<string, FieldRow> members, string readableName)
    {
        foreach (KeyValuePair<string, FieldRow> member in members)
        {
            if (member.Key == readableName || member.Key.StartsWith(readableName + "_", StringComparison.Ordinal))
            {
                return member.Value;
            }
        }

        return null;
    }

    private static double? GetDouble(Dictionary<string, FieldRow> members, string name) =>
        FindMember(members, name)?.Value switch { double d => d, long l => l, _ => (double?)null };

    private static long? GetLong(Dictionary<string, FieldRow> members, string name) =>
        FindMember(members, name)?.Value switch { long l => l, double d => (long)d, _ => (long?)null };

    private static bool? GetBool(Dictionary<string, FieldRow> members, string name) =>
        FindMember(members, name)?.Value as bool?;
}
