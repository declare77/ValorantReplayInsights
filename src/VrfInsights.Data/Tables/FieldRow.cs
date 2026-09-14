using Parquet;
using Parquet.Schema;

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

    /// <summary>
    /// Memory-bounded alternative to <c>ParquetTable.LoadAsync</c> + <see cref="LoadAll"/>, for
    /// fields.parquet specifically: a real full match has 1.5M+ rows there, and building a
    /// <c>Dictionary&lt;string,object&gt;</c> per row (what the untyped path does) was enough to
    /// <c>OutOfMemoryException</c> a 512MB container even loaded one row group at a time —
    /// vrfkit apparently writes this table as a single row group, so that didn't bound anything.
    ///
    /// <para>This instead reads each column straight into a typed array via Parquet.Net's
    /// lower-level <c>ParquetRowGroupReader.ReadAsync&lt;T&gt;</c> API, then builds
    /// <see cref="FieldRow"/> records directly from those arrays — no
    /// <c>Dictionary&lt;string,object&gt;</c> is ever created. A Dictionary per row is the
    /// expensive part: its own bookkeeping (buckets, entries, boxed keys/values) costs far more
    /// than the ~15 primitive values it holds, multiplied by 1.5M rows.</para>
    ///
    /// <para>Each column's actual CLR type isn't assumed — <see cref="DataField.ClrType"/> is
    /// checked at runtime and the matching typed overload is called, keeping the same "don't
    /// assume the exact width" defensiveness <see cref="RowConvert"/> already used for the
    /// dictionary-based path, just applied one column-array at a time instead of one cell at a
    /// time. If a column's actual type doesn't match any candidate checked here, this throws a
    /// clear <see cref="NotSupportedException"/> naming the column and its real type, rather than
    /// silently producing wrong data.</para>
    ///
    /// <para><b>Honest caveat</b>: this was written and reasoned through against parquet-dotnet's
    /// real source (not guessed), but never compiled or run against a real vrfkit export — no
    /// .NET SDK or real .vrf file was available while writing it. It's the most memory-efficient
    /// approach available through this library's public API (no per-row Dictionary, no
    /// intermediate boxing beyond what reading a value out of a typed array requires), but
    /// whether it comfortably fits a specific match's data in 512MB specifically hasn't been
    /// measured.</para>
    /// </summary>
    public static async Task<List<FieldRow>> LoadAllStreamingAsync(string filePath, CancellationToken ct = default)
    {
        await using FileStream fs = File.OpenRead(filePath);
        await using ParquetReader reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct);
        DataField[] allFields = reader.Schema.GetDataFields();

        DataField? Find(string name) => Array.Find(allFields, f => f.Name == name);
        DataField Require(string name) => Find(name)
            ?? throw new InvalidDataException($"fields.parquet is missing expected column '{name}'.");

        DataField timeMsF = Require("time_ms");
        DataField packetIdF = Require("packet_id");
        DataField channelIndexF = Require("channel_index");
        DataField actorNetGuidF = Require("actor_net_guid");
        DataField? objectNetGuidF = Find("object_net_guid");
        DataField groupPathF = Require("group_path");
        DataField handleF = Require("handle");
        DataField? fieldNameF = Find("field_name");
        DataField bitCountF = Require("bit_count");
        DataField? rawBitsF = Find("raw_bits");
        DataField? valueI64F = Find("value_i64");
        DataField? valueF64F = Find("value_f64");
        DataField? valueBoolF = Find("value_bool");
        DataField? valueStrF = Find("value_str");
        DataField? compatibleChecksumF = Find("compatible_checksum");

        var results = new List<FieldRow>();
        for (int rgIdx = 0; rgIdx < reader.RowGroupCount; rgIdx++)
        {
            ct.ThrowIfCancellationRequested();
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(rgIdx);
            int rowCount = checked((int)rg.RowCount);

            long?[] timeMs = await ReadIntegerColumnAsync(rg, timeMsF, rowCount, ct);
            long?[] packetId = await ReadIntegerColumnAsync(rg, packetIdF, rowCount, ct);
            long?[] channelIndex = await ReadIntegerColumnAsync(rg, channelIndexF, rowCount, ct);
            long?[] actorNetGuid = await ReadIntegerColumnAsync(rg, actorNetGuidF, rowCount, ct);
            long?[]? objectNetGuid = objectNetGuidF is null ? null : await ReadIntegerColumnAsync(rg, objectNetGuidF, rowCount, ct);
            string?[] groupPath = await ReadStringColumnAsync(rg, groupPathF, rowCount, ct);
            long?[] handle = await ReadIntegerColumnAsync(rg, handleF, rowCount, ct);
            string?[]? fieldName = fieldNameF is null ? null : await ReadStringColumnAsync(rg, fieldNameF, rowCount, ct);
            long?[] bitCount = await ReadIntegerColumnAsync(rg, bitCountF, rowCount, ct);
            byte[]?[]? rawBits = rawBitsF is null ? null : await ReadByteArrayColumnAsync(rg, rawBitsF, rowCount, ct);
            long?[]? valueI64 = valueI64F is null ? null : await ReadIntegerColumnAsync(rg, valueI64F, rowCount, ct);
            double?[]? valueF64 = valueF64F is null ? null : await ReadDoubleColumnAsync(rg, valueF64F, rowCount, ct);
            bool?[]? valueBool = valueBoolF is null ? null : await ReadBoolColumnAsync(rg, valueBoolF, rowCount, ct);
            string?[]? valueStr = valueStrF is null ? null : await ReadStringColumnAsync(rg, valueStrF, rowCount, ct);
            long?[]? compatibleChecksum = compatibleChecksumF is null ? null : await ReadIntegerColumnAsync(rg, compatibleChecksumF, rowCount, ct);

            results.Capacity = results.Count + rowCount;
            for (int i = 0; i < rowCount; i++)
            {
                results.Add(new FieldRow(
                    timeMs[i] ?? 0,
                    packetId[i] ?? 0,
                    channelIndex[i] ?? 0,
                    actorNetGuid[i] ?? 0,
                    objectNetGuid?[i],
                    groupPath[i] ?? "",
                    handle[i] ?? 0,
                    fieldName?[i],
                    bitCount[i] ?? 0,
                    rawBits?[i],
                    valueI64?[i],
                    valueF64?[i],
                    valueBool?[i],
                    valueStr?[i],
                    compatibleChecksum?[i]));
            }
        }

        return results;
    }

    // Every integer-ish column in fields.parquet is exposed as `long`/`long?` in this record, but
    // the file's actual physical width per column isn't assumed (mirroring RowConvert's own
    // defensiveness for the Dictionary-based path) -- DataField.ClrType is checked and the
    // matching typed read is used, then widened to long.
    private static async Task<long?[]> ReadIntegerColumnAsync(ParquetRowGroupReader rg, DataField field, int rowCount, CancellationToken ct)
    {
        Type t = field.ClrType;
        if (field.IsNullable)
        {
            if (t == typeof(long)) { var b = new long?[rowCount]; await rg.ReadAsync<long>(field, b, cancellationToken: ct); return b; }
            if (t == typeof(int)) { var b = new int?[rowCount]; await rg.ReadAsync<int>(field, b, cancellationToken: ct); return WidenNullable<int, long>(b, x => x); }
            if (t == typeof(uint)) { var b = new uint?[rowCount]; await rg.ReadAsync<uint>(field, b, cancellationToken: ct); return WidenNullable<uint, long>(b, x => x); }
            if (t == typeof(short)) { var b = new short?[rowCount]; await rg.ReadAsync<short>(field, b, cancellationToken: ct); return WidenNullable<short, long>(b, x => x); }
            if (t == typeof(ushort)) { var b = new ushort?[rowCount]; await rg.ReadAsync<ushort>(field, b, cancellationToken: ct); return WidenNullable<ushort, long>(b, x => x); }
            if (t == typeof(byte)) { var b = new byte?[rowCount]; await rg.ReadAsync<byte>(field, b, cancellationToken: ct); return WidenNullable<byte, long>(b, x => x); }
            if (t == typeof(sbyte)) { var b = new sbyte?[rowCount]; await rg.ReadAsync<sbyte>(field, b, cancellationToken: ct); return WidenNullable<sbyte, long>(b, x => x); }
            if (t == typeof(ulong)) { var b = new ulong?[rowCount]; await rg.ReadAsync<ulong>(field, b, cancellationToken: ct); return WidenNullable<ulong, long>(b, x => unchecked((long)x)); }
            throw new NotSupportedException($"fields.parquet column '{field.Name}': unexpected nullable integer CLR type {t}.");
        }
        else
        {
            if (t == typeof(long)) { var b = new long[rowCount]; await rg.ReadAsync<long>(field, b, cancellationToken: ct); return WidenNonNullable<long, long>(b, x => x); }
            if (t == typeof(int)) { var b = new int[rowCount]; await rg.ReadAsync<int>(field, b, cancellationToken: ct); return WidenNonNullable<int, long>(b, x => x); }
            if (t == typeof(uint)) { var b = new uint[rowCount]; await rg.ReadAsync<uint>(field, b, cancellationToken: ct); return WidenNonNullable<uint, long>(b, x => x); }
            if (t == typeof(short)) { var b = new short[rowCount]; await rg.ReadAsync<short>(field, b, cancellationToken: ct); return WidenNonNullable<short, long>(b, x => x); }
            if (t == typeof(ushort)) { var b = new ushort[rowCount]; await rg.ReadAsync<ushort>(field, b, cancellationToken: ct); return WidenNonNullable<ushort, long>(b, x => x); }
            if (t == typeof(byte)) { var b = new byte[rowCount]; await rg.ReadAsync<byte>(field, b, cancellationToken: ct); return WidenNonNullable<byte, long>(b, x => x); }
            if (t == typeof(sbyte)) { var b = new sbyte[rowCount]; await rg.ReadAsync<sbyte>(field, b, cancellationToken: ct); return WidenNonNullable<sbyte, long>(b, x => x); }
            if (t == typeof(ulong)) { var b = new ulong[rowCount]; await rg.ReadAsync<ulong>(field, b, cancellationToken: ct); return WidenNonNullable<ulong, long>(b, x => unchecked((long)x)); }
            throw new NotSupportedException($"fields.parquet column '{field.Name}': unexpected integer CLR type {t}.");
        }
    }

    private static async Task<double?[]> ReadDoubleColumnAsync(ParquetRowGroupReader rg, DataField field, int rowCount, CancellationToken ct)
    {
        Type t = field.ClrType;
        if (field.IsNullable)
        {
            if (t == typeof(double)) { var b = new double?[rowCount]; await rg.ReadAsync<double>(field, b, cancellationToken: ct); return b; }
            if (t == typeof(float)) { var b = new float?[rowCount]; await rg.ReadAsync<float>(field, b, cancellationToken: ct); return WidenNullable<float, double>(b, x => x); }
            throw new NotSupportedException($"fields.parquet column '{field.Name}': unexpected nullable float CLR type {t}.");
        }
        else
        {
            if (t == typeof(double)) { var b = new double[rowCount]; await rg.ReadAsync<double>(field, b, cancellationToken: ct); return WidenNonNullable<double, double>(b, x => x); }
            if (t == typeof(float)) { var b = new float[rowCount]; await rg.ReadAsync<float>(field, b, cancellationToken: ct); return WidenNonNullable<float, double>(b, x => x); }
            throw new NotSupportedException($"fields.parquet column '{field.Name}': unexpected float CLR type {t}.");
        }
    }

    private static async Task<bool?[]> ReadBoolColumnAsync(ParquetRowGroupReader rg, DataField field, int rowCount, CancellationToken ct)
    {
        if (field.ClrType != typeof(bool))
        {
            throw new NotSupportedException($"fields.parquet column '{field.Name}': unexpected bool CLR type {field.ClrType}.");
        }

        if (field.IsNullable)
        {
            var b = new bool?[rowCount];
            await rg.ReadAsync<bool>(field, b, cancellationToken: ct);
            return b;
        }
        else
        {
            var b = new bool[rowCount];
            await rg.ReadAsync<bool>(field, b, cancellationToken: ct);
            return WidenNonNullable<bool, bool>(b, x => x);
        }
    }

    private static async Task<string?[]> ReadStringColumnAsync(ParquetRowGroupReader rg, DataField field, int rowCount, CancellationToken ct)
    {
        var b = new string?[rowCount];
        await rg.ReadAsync(field, b, cancellationToken: ct);
        return b;
    }

    private static async Task<byte[]?[]> ReadByteArrayColumnAsync(ParquetRowGroupReader rg, DataField field, int rowCount, CancellationToken ct)
    {
        byte[]?[] b = new byte[rowCount][];
        await rg.ReadAsync(field, b, cancellationToken: ct);
        return b;
    }

    private static TOut?[] WidenNullable<TIn, TOut>(TIn?[] source, Func<TIn, TOut> widen)
        where TIn : struct
        where TOut : struct
    {
        var result = new TOut?[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = source[i].HasValue ? widen(source[i]!.Value) : (TOut?)null;
        }

        return result;
    }

    private static TOut?[] WidenNonNullable<TIn, TOut>(TIn[] source, Func<TIn, TOut> widen)
        where TIn : struct
        where TOut : struct
    {
        var result = new TOut?[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = widen(source[i]);
        }

        return result;
    }
}
