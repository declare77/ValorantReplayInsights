namespace VrfInsights.Data.Tables;

/// <summary>
/// One row of <c>fields.parquet</c> — a single replicated property or RPC parameter.
/// Schema per vrfkit docs/USAGE.md ("fields.parquet").
///
/// <para>Unreal's property stream is self-describing: every field carries a handle and a
/// bit-length before its value, so vrfkit can walk field boundaries without knowing the type.
/// The base path always emits {group_path, handle, field_name, bit_count, raw_bits}; when a
/// type is registered for that (group, handle), one of the <c>Value*</c> columns is filled in
/// as an <i>additive overlay</i> — at most one of them is non-null per row.</para>
///
/// <para>Arrays are flattened into <see cref="FieldName"/>, e.g.
/// <c>AbilityCastsThisRound[2].CastLocation</c> or <c>Rounds[3].Reports[1].Interactions[0].DamageDealt</c>.
/// <see cref="VrfInsights.Analysis.Common.FlattenedArrayPivot"/> turns groups of these rows back
/// into structured records.</para>
/// </summary>
public sealed record FieldRow(
    long TimeMs,
    long PacketId,
    long ChannelIndex,
    long ActorNetGuid,
    long? ObjectNetGuid,
    string GroupPath,
    long Handle,
    string? FieldName,
    long BitCount,
    byte[]? RawBits,
    long? ValueI64,
    double? ValueF64,
    bool? ValueBool,
    string? ValueStr,
    long? CompatibleChecksum)
{
    /// <summary>The single non-null overlay value for this row, or null if the row is untyped
    /// (only <see cref="RawBits"/> is available).</summary>
    public object? Value => (object?)ValueI64 ?? (object?)ValueF64 ?? (object?)ValueBool ?? ValueStr;

    public static FieldRow FromRow(IReadOnlyDictionary<string, object> row) => new(
        RowConvert.ToLong(row, "time_ms") ?? 0,
        RowConvert.ToLong(row, "packet_id") ?? 0,
        RowConvert.ToLong(row, "channel_index") ?? 0,
        RowConvert.ToLong(row, "actor_net_guid") ?? 0,
        RowConvert.ToLong(row, "object_net_guid"),
        RowConvert.ToStringValue(row, "group_path") ?? "",
        RowConvert.ToLong(row, "handle") ?? 0,
        RowConvert.ToStringValue(row, "field_name"),
        RowConvert.ToLong(row, "bit_count") ?? 0,
        RowConvert.ToBytes(row, "raw_bits"),
        RowConvert.ToLong(row, "value_i64"),
        RowConvert.ToDouble(row, "value_f64"),
        RowConvert.ToBool(row, "value_bool"),
        RowConvert.ToStringValue(row, "value_str"),
        RowConvert.ToLong(row, "compatible_checksum"));

    public static List<FieldRow> LoadAll(ParquetTable table) =>
        table.Rows.Select(FromRow).ToList();

    /// <summary>Memory-bounded alternative to <c>ParquetTable.LoadAsync(path)</c> +
    /// <see cref="LoadAll"/> — see <see cref="ParquetTable.LoadStreamingAsync{T}"/> for why
    /// fields.parquet specifically needs this (a real full match's fields table is 1.5M+ rows,
    /// large enough on its own to exhaust a 512MB container's memory when loaded all at once).</summary>
    public static Task<List<FieldRow>> LoadAllStreamingAsync(string filePath, CancellationToken ct = default) =>
        ParquetTable.LoadStreamingAsync(filePath, FromRow, ct);
}
