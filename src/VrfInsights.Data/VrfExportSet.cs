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

        // Loaded sequentially, not concurrently. Each ParquetTable.LoadAsync call briefly holds
        // the WHOLE file as a List<Dictionary<string,object>> (one dictionary per row, every
        // column boxed) before it's converted into the much more compact typed *Row list below --
        // fields.parquet alone can be 1.5M+ rows on a real full match. Running all five loads
        // concurrently means all five of those bulky intermediate dictionary-lists (plus their
        // typed replacements) can be alive in memory at the same time, which is what pushed a real
        // deploy over Render's free-tier 512MB limit into an OutOfMemoryException. Sequential
        // loading trades a bit of wall-clock time for a much lower peak: only one table's
        // intermediate form needs to exist at once, and it's eligible for garbage collection as
        // soon as its LoadAll() call returns, before the next table starts. fields.parquet (by far
        // the largest table) is loaded first so its intermediate form has the least other live data
        // to coexist with.
        ParquetTable fieldsTable = await ParquetTable.LoadAsync(Path.Combine(exportDirectory, "fields.parquet"), ct);
        IReadOnlyList<FieldRow> fields = FieldRow.LoadAll(fieldsTable);
        fieldsTable = null!; // drop the reference explicitly so the GC can reclaim it before the next load, rather than waiting for this method to return.

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
