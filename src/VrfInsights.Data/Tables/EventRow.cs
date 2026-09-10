namespace VrfInsights.Data.Tables;

/// <summary>
/// One row of <c>events.parquet</c> — the timeline the VALORANT server wrote itself (one row
/// per Event chunk), independent of the actor-replication stream. Schema per vrfkit
/// docs/USAGE.md ("events.parquet").
///
/// <para><see cref="Group"/> is one of: <c>characterDeath</c>, <c>characterUltimateUsed</c>,
/// <c>roundStarted</c>, <c>spikePlanted</c>, <c>spikeDefused</c>, <c>spikeExploded</c>,
/// <c>switchTeams</c>, and possibly others vrfkit hasn't catalogued yet.</para>
///
/// <para>The nullable <see cref="Word0"/>/<see cref="Word1"/>/<see cref="PayloadTag"/>/
/// <see cref="PayloadName"/>/<see cref="PayloadSeconds"/> overlay is populated only when
/// vrfkit's arity/tag/name/time cross-check all agree for that row's group; otherwise every
/// overlay column is null and only <see cref="RawPayload"/> is authoritative. For
/// <c>characterDeath</c>, (Word0, Word1) is (killer, killed) NetGUID; for <c>roundStarted</c>,
/// Word0 is the round number.</para>
/// </summary>
public sealed record EventRow(
    string? Id,
    string Group,
    string? Metadata,
    long Time1,
    long Time2,
    long PayloadSize,
    byte[]? RawPayload,
    long? Word0,
    long? Word1,
    long? PayloadTag,
    string? PayloadName,
    double? PayloadSeconds)
{
    public static EventRow FromRow(IReadOnlyDictionary<string, object> row) => new(
        RowConvert.ToStringValue(row, "id"),
        RowConvert.ToStringValue(row, "group") ?? "",
        RowConvert.ToStringValue(row, "metadata"),
        RowConvert.ToLong(row, "time1") ?? 0,
        RowConvert.ToLong(row, "time2") ?? 0,
        RowConvert.ToLong(row, "payload_size") ?? 0,
        RowConvert.ToBytes(row, "raw_payload"),
        RowConvert.ToLong(row, "word0"),
        RowConvert.ToLong(row, "word1"),
        RowConvert.ToLong(row, "payload_tag"),
        RowConvert.ToStringValue(row, "payload_name"),
        RowConvert.ToDouble(row, "payload_seconds"));

    public static List<EventRow> LoadAll(ParquetTable table) =>
        table.Rows.Select(FromRow).ToList();
}
