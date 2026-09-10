using System.Globalization;
using System.Text.RegularExpressions;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Combat;

/// <summary>
/// Extracts <c>CombatReport</c> interactions from <c>fields.parquet</c>. This is a three-level
/// nested array (<c>Rounds[r].Reports[p].Interactions[i].&lt;member&gt;</c>) rather than the
/// single-level arrays <see cref="Common.FlattenedArrayPivot"/> handles, so it parses the full
/// path directly instead of pivoting twice.
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

    private static double? GetDouble(Dictionary<string, FieldRow> members, string name) =>
        members.TryGetValue(name, out FieldRow? row) ? row.Value switch { double d => d, long l => l, _ => (double?)null } : null;

    private static long? GetLong(Dictionary<string, FieldRow> members, string name) =>
        members.TryGetValue(name, out FieldRow? row) ? row.Value switch { long l => l, double d => (long)d, _ => (long?)null } : null;

    private static bool? GetBool(Dictionary<string, FieldRow> members, string name) =>
        members.TryGetValue(name, out FieldRow? row) ? row.Value as bool? : null;
}
