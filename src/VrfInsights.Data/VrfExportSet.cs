using System.Text.Json;
using VrfInsights.Data.Manifest;
using VrfInsights.Data.Tables;

namespace VrfInsights.Data;

/// <summary>
/// Loads everything <c>vrfkit export &lt;file.vrf&gt; --out &lt;dir&gt;</c> writes: the five
/// always-written main tables plus <c>manifest.json</c>. <c>partials.parquet</c> (unresolved
/// transport fragments) and the seven <c>--checkpoints</c> tables are intentionally not loaded
/// here — nothing in this project's analysis layer currently needs them.
///
/// This type never opens a <c>.vrf</c> file itself; it only reads vrfkit's already-decoded
/// output directory.
/// </summary>
public sealed class VrfExportSet
{
    public required ReplayManifest Manifest { get; init; }
    public required IReadOnlyList<FieldRow> Fields { get; init; }
    public required IReadOnlyList<MovementRow> Movement { get; init; }
    public required IReadOnlyList<ActorRow> Actors { get; init; }
    public required IReadOnlyList<NetGuidRow> NetGuids { get; init; }
    public required IReadOnlyList<EventRow> Events { get; init; }

    public static async Task<VrfExportSet> LoadAsync(string exportDirectory, CancellationToken ct = default)
    {
        if (!Directory.Exists(exportDirectory))
        {
            throw new DirectoryNotFoundException(
                $"vrfkit export directory not found: {exportDirectory}. " +
                "Run `vrfkit export <file.vrf> --out <dir>` first (https://github.com/yakisoba0728/vrfkit).");
        }

        string manifestPath = Path.Combine(exportDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"manifest.json not found under {exportDirectory} — is this a vrfkit export directory?", manifestPath);
        }

        ReplayManifest manifest = JsonSerializer.Deserialize<ReplayManifest>(
            await File.ReadAllTextAsync(manifestPath, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"manifest.json at {manifestPath} deserialized to null.");

        // All five tables are loaded sequentially, not concurrently (via Task.WhenAll as this used
        // to do) -- each ParquetTable.LoadAsync call briefly holds the WHOLE file as a
        // List<Dictionary<string,object>> (one dictionary per row, every column boxed) before it's
        // converted into the much more compact typed *Row list. Running all five loads
        // concurrently means all five of those bulky intermediate forms can be alive in memory at
        // the same time; sequential loading trades a bit of wall-clock time for a lower peak, since
        // only one table's intermediate form needs to exist at once.
        //
        // fields.parquet gets a further, more important change on top of that: it uses
        // FieldRow.LoadAllStreamingAsync, a column-native reader that never builds a
        // Dictionary<string,object> per row at all, instead of the whole-file LoadAsync + LoadAll
        // path the other four tables below still use. A real full match's fields.parquet is 1.5M+
        // rows -- large enough on its own, loaded as a Dictionary-per-row list, to
        // OutOfMemoryException a 512MB container (a row-group-bounded version of that same
        // Dictionary approach was tried first and didn't help -- vrfkit writes this table as a
        // single row group). See FieldRow.LoadAllStreamingAsync's doc comment for details and
        // caveats.
        IReadOnlyList<FieldRow> fields = await FieldRow.LoadAllStreamingAsync(Path.Combine(exportDirectory, "fields.parquet"), ct);

        ParquetTable movementTable = await ParquetTable.LoadAsync(Path.Combine(exportDirectory, "movement.parquet"), ct);
        IReadOnlyList<MovementRow> movement = MovementRow.LoadAll(movementTable);
        movementTable = null!;

        ParquetTable actorsTable = await ParquetTable.LoadAsync(Path.Combine(exportDirectory, "actors.parquet"), ct);
        IReadOnlyList<ActorRow> actors = ActorRow.LoadAll(actorsTable);
        actorsTable = null!;

        ParquetTable netGuidsTable = await ParquetTable.LoadAsync(Path.Combine(exportDirectory, "net_guids.parquet"), ct);
        IReadOnlyList<NetGuidRow> netGuids = NetGuidRow.LoadAll(netGuidsTable);
        netGuidsTable = null!;

        ParquetTable eventsTable = await ParquetTable.LoadAsync(Path.Combine(exportDirectory, "events.parquet"), ct);
        IReadOnlyList<EventRow> events = EventRow.LoadAll(eventsTable);
        eventsTable = null!;

        return new VrfExportSet
        {
            Manifest = manifest,
            Fields = fields,
            Movement = movement,
            Actors = actors,
            NetGuids = netGuids,
            Events = events,
        };
    }
}
