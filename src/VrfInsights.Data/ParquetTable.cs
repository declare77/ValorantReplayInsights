using Parquet;
using Parquet.Serialization;

namespace VrfInsights.Data;

/// <summary>
/// Loads a single Parquet file into a list of column-name-keyed row dictionaries, using
/// Parquet.Net's schema-agnostic "untyped" deserializer. vrfkit's tables are ordinary
/// dictionary-encoded/ZSTD-compressed Parquet with plain primitive columns (see
/// vrfkit's docs/USAGE.md), so there is no need to hand-walk <c>DataField</c>s per table —
/// one call reads every row group and every column.
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

    /// <summary>
    /// Like <see cref="LoadAsync"/> followed by converting every row, except it converts and
    /// discards each ROW GROUP's dictionaries before reading the next one, instead of
    /// materializing every row of the whole file as <c>Dictionary&lt;string,object&gt;</c> at
    /// once (which is what <see cref="LoadAsync"/> does, via a single whole-file call to
    /// <see cref="ParquetSerializer.DeserializeUntypedAsync"/>).
    ///
    /// <para>Added for <c>fields.parquet</c> specifically: a real full match has 1.5M+ rows
    /// there, and loading the whole file's dictionaries at once was enough to
    /// <c>OutOfMemoryException</c> a 512MB container even with that table loaded on its own.
    /// This works by calling the SAME <c>DeserializeUntypedAsync</c> method used above, but once
    /// per row group via its own <c>rowGroupIndex</c> parameter (confirmed against
    /// parquet-dotnet's actual source: passing a specific index makes it deserialize only that
    /// one row group's data, not the rest of the file) — so <paramref name="convert"/> can be an
    /// existing <c>*Row.FromRow</c> method unchanged, with the exact same
    /// <see cref="RowConvert"/>-based coercion/null-handling every table already relies on. The
    /// only thing that changes is HOW MANY rows are held in dictionary form at once (one row
    /// group's worth, not the whole file).</para>
    ///
    /// <para><b>Honest caveat</b>: this bounds peak memory to one row group, not to a fixed small
    /// amount — if vrfkit ever writes a table as a single row group (or very few, very large
    /// ones), this helps little or not at all. Row-group sizing wasn't verified against a real
    /// vrfkit export while writing this (none was available) — if a real upload still runs out of
    /// memory after this change, that's the first thing to check before assuming this fix did
    /// nothing.</para>
    /// </summary>
    public static async Task<List<T>> LoadStreamingAsync<T>(
        string filePath,
        Func<IReadOnlyDictionary<string, object>, T> convert,
        CancellationToken ct = default)
    {
        await using FileStream fs = File.OpenRead(filePath);

        int rowGroupCount;
        await using (ParquetReader countReader = await ParquetReader.CreateAsync(fs, cancellationToken: ct))
        {
            rowGroupCount = countReader.RowGroupCount; // metadata only -- doesn't read any column data.
        }

        var results = new List<T>();
        for (int rg = 0; rg < rowGroupCount; rg++)
        {
            ct.ThrowIfCancellationRequested();
            fs.Position = 0; // ParquetReader/DeserializeUntypedAsync re-locate the footer themselves each call.

            Parquet.Serialization.DeserializationResult<Dictionary<string, object>> rgResult =
                await ParquetSerializer.DeserializeUntypedAsync(fs, rowGroupIndex: rg, cancellationToken: ct);

            foreach (Dictionary<string, object> row in rgResult.Data)
            {
                results.Add(convert(row));
            }

            // rgResult and its Data list fall out of scope here, eligible for garbage collection
            // before the next row group is read, instead of every row group's dictionaries
            // coexisting for the whole file's lifetime.
        }

        return results;
    }
}
