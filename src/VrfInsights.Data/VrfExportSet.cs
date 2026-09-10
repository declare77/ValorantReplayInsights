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

        // Load the four large tables concurrently — they're independent files.
        Task<ParquetTable> fieldsTask = ParquetTable.LoadAsync(Path.Combine(exportDirectory, "fields.parquet"), ct);
        Task<ParquetTable> movementTask = ParquetTable.LoadAsync(Path.Combine(exportDirectory, "movement.parquet"), ct);
        Task<ParquetTable> actorsTask = ParquetTable.LoadAsync(Path.Combine(exportDirectory, "actors.parquet"), ct);
        Task<ParquetTable> netGuidsTask = ParquetTable.LoadAsync(Path.Combine(exportDirectory, "net_guids.parquet"), ct);
        Task<ParquetTable> eventsTask = ParquetTable.LoadAsync(Path.Combine(exportDirectory, "events.parquet"), ct);

        await Task.WhenAll(fieldsTask, movementTask, actorsTask, netGuidsTask, eventsTask);

        return new VrfExportSet
        {
            Manifest = manifest,
            Fields = FieldRow.LoadAll(fieldsTask.Result),
            Movement = MovementRow.LoadAll(movementTask.Result),
            Actors = ActorRow.LoadAll(actorsTask.Result),
            NetGuids = NetGuidRow.LoadAll(netGuidsTask.Result),
            Events = EventRow.LoadAll(eventsTask.Result),
        };
    }
}
