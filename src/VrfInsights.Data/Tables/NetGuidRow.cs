namespace VrfInsights.Data.Tables;

/// <summary>
/// One row of <c>net_guids.parquet</c> — GUID-to-path resolution and containment.
/// Schema per vrfkit docs/USAGE.md ("net_guids.parquet"). <c>actors.parquet</c> only covers
/// GUIDs that opened their own channel, so this table is what fills in subobjects (e.g. a
/// weapon's "FiringState" subobject) — walk <see cref="OuterNetGuid"/> to find the owning actor.
/// </summary>
public sealed record NetGuidRow(
    long NetGuid,
    string Path,
    long? OuterNetGuid)
{
    public static NetGuidRow FromRow(IReadOnlyDictionary<string, object> row) => new(
        RowConvert.ToLong(row, "net_guid") ?? 0,
        RowConvert.ToStringValue(row, "path") ?? "",
        RowConvert.ToLong(row, "outer_net_guid"));

    public static List<NetGuidRow> LoadAll(ParquetTable table) =>
        table.Rows.Select(FromRow).ToList();
}
