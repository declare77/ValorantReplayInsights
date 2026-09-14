using Parquet.Serialization;

namespace VrfInsights.Data;

/// <summary>
/// Loads a single Parquet file into a list of column-name-keyed row dictionaries, using
/// Parquet.Net's schema-agnostic "untyped" deserializer. vrfkit's tables are ordinary
/// dictionary-encoded/ZSTD-compressed Parquet with plain primitive columns (see
/// vrfkit's docs/USAGE.md), so there is no need to hand-walk <c>DataField</c>s per table —
/// one call reads every row group and every column.
///
/// <para>Used for the four smaller tables (movement/actors/net_guids/events). fields.parquet is
/// the one exception — a real full match's fields table is 1.5M+ rows, large enough on its own
/// to exceed a 512MB container's memory when loaded this way (one
/// <c>Dictionary&lt;string,object&gt;</c> per row is expensive: far more memory than the handful
/// of primitive values it actually holds). Reading it one row group at a time didn't help either
/// — vrfkit apparently writes it as a single row group, so that bounded nothing. See
/// <see cref="Tables.FieldRow.LoadAllStreamingAsync"/> for the column-native reader that
/// replaces this for that one table specifically.</para>
/// </summary>
public sealed class ParquetTable
{
    public IReadOnlyList<IReadOnlyDictionary<string, object>> Rows { get; }

    private ParquetTable(IReadOnlyList<IReadOnlyDictionary<string, object>> rows)
    {
        Rows = rows;
    }

    public static async Task<ParquetTable> LoadAsync(string filePath, CancellationToken ct = default)
    {
        await using FileStream fs = File.OpenRead(filePath);
        Parquet.Serialization.DeserializationResult<Dictionary<string, object>> result =
            await ParquetSerializer.DeserializeUntypedAsync(fs, cancellationToken: ct);

        var rows = new List<IReadOnlyDictionary<string, object>>(result.Data.Count);
        foreach (Dictionary<string, object> row in result.Data)
        {
            rows.Add(row);
        }

        return new ParquetTable(rows);
    }

    /// <summary>Returns null (rather than throwing) when the file doesn't exist — some vrfkit
    /// tables are only written with <c>--checkpoints</c>, and callers should treat an absent
    /// optional table as "not available" rather than a hard failure.</summary>
    public static async Task<ParquetTable?> TryLoadAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        return await LoadAsync(filePath, ct);
    }
}
