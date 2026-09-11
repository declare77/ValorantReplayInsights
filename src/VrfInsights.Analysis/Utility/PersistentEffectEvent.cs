using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Utility;

/// <param name="DespawnTimeMs">Null if the actor never closed before the replay ended (or if
/// the export ends while it's still dormant).</param>
/// <param name="YawDegrees">The actor's spawn yaw (<c>actors.parquet</c>'s <c>spawn_yaw</c>),
/// carried through mainly so a directional effect (a wall) can be drawn oriented rather than as
/// a bare point — a 2D viewer still has to assume a length for that line since this table has no
/// size/extent field, so treat the drawn line as an approximation, not measured geometry.</param>
public sealed record PersistentEffectEvent(
    long ActorNetGuid,
    string? ClassPath,
    UtilityCategory Category,
    long SpawnTimeMs,
    long? DespawnTimeMs,
    double? X,
    double? Y,
    double? Z,
    double? YawDegrees);

/// <summary>
/// Builds smoke/wall/molly/trap/etc. placement events from <c>actors.parquet</c>'s open/close
/// lifecycle, honoring vrfkit's documented distinction: a "dormant" close means the server
/// stopped replicating an actor that is still alive (it can "wake up" as another "open" later),
/// and is <b>not</b> a despawn — only a genuine non-dormant "close" ends an effect's lifetime.
/// </summary>
public static class UtilityTimelineBuilder
{
    private enum State { Open, Dormant }

    public static IReadOnlyList<PersistentEffectEvent> Build(IReadOnlyList<ActorRow> actors)
    {
        var sorted = new List<ActorRow>(actors);
        sorted.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

        var pending = new Dictionary<long, (ActorRow OpenRow, State State)>();
        var results = new List<PersistentEffectEvent>();

        foreach (ActorRow row in sorted)
        {
            if (row.IsOpen)
            {
                if (pending.ContainsKey(row.ActorNetGuid))
                {
                    // A wake-up from dormancy re-opens the same instance under vrfkit's model —
                    // keep the original spawn row rather than treating this as a new instance.
                    pending[row.ActorNetGuid] = (pending[row.ActorNetGuid].OpenRow, State.Open);
                    continue;
                }

                UtilityCategory category = UtilityEffectClassifier.Classify(row.ClassPath);
                if (category == UtilityCategory.Unclassified)
                {
                    continue;
                }

                pending[row.ActorNetGuid] = (row, State.Open);
            }
            else if (row.IsDormant)
            {
                if (pending.TryGetValue(row.ActorNetGuid, out (ActorRow OpenRow, State State) entry))
                {
                    pending[row.ActorNetGuid] = (entry.OpenRow, State.Dormant);
                }
            }
            else if (row.IsClose)
            {
                if (pending.TryGetValue(row.ActorNetGuid, out (ActorRow OpenRow, State State) entry))
                {
                    pending.Remove(row.ActorNetGuid);
                    results.Add(new PersistentEffectEvent(
                        ActorNetGuid: row.ActorNetGuid,
                        ClassPath: entry.OpenRow.ClassPath,
                        Category: UtilityEffectClassifier.Classify(entry.OpenRow.ClassPath),
                        SpawnTimeMs: entry.OpenRow.TimeMs,
                        DespawnTimeMs: row.TimeMs,
                        X: entry.OpenRow.SpawnX,
                        Y: entry.OpenRow.SpawnY,
                        Z: entry.OpenRow.SpawnZ,
                        YawDegrees: entry.OpenRow.SpawnYaw));
                }
            }
        }

        // Anything still open/dormant at end-of-replay: emit with a null despawn time rather
        // than dropping it.
        foreach ((ActorRow OpenRow, State State) entry in pending.Values)
        {
            results.Add(new PersistentEffectEvent(
                ActorNetGuid: entry.OpenRow.ActorNetGuid,
                ClassPath: entry.OpenRow.ClassPath,
                Category: UtilityEffectClassifier.Classify(entry.OpenRow.ClassPath),
                SpawnTimeMs: entry.OpenRow.TimeMs,
                DespawnTimeMs: null,
                X: entry.OpenRow.SpawnX,
                Y: entry.OpenRow.SpawnY,
                Z: entry.OpenRow.SpawnZ,
                YawDegrees: entry.OpenRow.SpawnYaw));
        }

        results.Sort((a, b) => a.SpawnTimeMs.CompareTo(b.SpawnTimeMs));
        return results;
    }
}
