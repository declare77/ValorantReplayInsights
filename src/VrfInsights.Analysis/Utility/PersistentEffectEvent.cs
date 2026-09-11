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
///
/// <para><b>One ability cast can spawn several separately-classified actors</b> (confirmed via
/// <c>dump-actors</c> against a real export, for both Omen's and Jett's smoke): an
/// <c>Ability_*</c> per-player container that opens once near match start at that player's spawn
/// point, a <c>Projectile_*</c> actor that opens per-cast and closes quickly (the in-flight
/// grenade), and — where the ability has one — a <c>Zone_*</c>/<c>*Zone</c> actor that opens
/// slightly after the projectile and stays open for the effect's real duration (the deployed
/// smoke cloud). Naively classifying every one of these as its own placement produces exactly
/// the reported bug ("some of them just end up in spawn"): the <c>Ability_*</c> container's
/// position is always near spawn, since that's where that player was when it opened at t≈0.
/// This builder (1) excludes <c>Ability_*</c> containers entirely — see
/// <see cref="UtilityEffectClassifier.IsAbilityContainerActor"/> — and (2), per ability (agent +
/// ability slot, via <see cref="UtilityEffectClassifier.ExtractAbilityGroupKey"/>), prefers a
/// <c>Zone</c>-named actor's position over a sibling <c>Projectile</c>-named one when both exist,
/// since the Zone actor's open/close window is the one that actually matches real ability
/// durations. Abilities with no separate Zone actor are unaffected.</para>
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

                if (UtilityEffectClassifier.IsAbilityContainerActor(row.ClassPath))
                {
                    // Per-player ability-slot container, not a per-cast placement -- see the
                    // class doc comment above. Its position is never meaningful here.
                    continue;
                }

                if (UtilityEffectClassifier.IsWeaponModelActor(row.ClassPath))
                {
                    // A gun/weapon model, never a utility placement -- see
                    // IsWeaponModelActor's doc comment for the real false-positive this guards.
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

        List<PersistentEffectEvent> deduped = PreferZoneOverNonZoneSiblings(results);
        deduped.Sort((a, b) => a.SpawnTimeMs.CompareTo(b.SpawnTimeMs));
        return deduped;
    }

    /// <summary>
    /// Per ability (agent + slot — see <see cref="UtilityEffectClassifier.ExtractAbilityGroupKey"/>),
    /// if a <c>Zone</c>-named actor was seen at all for that ability, drops every event for that
    /// same ability whose class name does *not* itself contain "Zone" (e.g. the
    /// <c>Projectile_*</c> sibling). Confirmed via <c>dump-actors</c> for smoke: the Zone actor's
    /// open/close window matches the ability's real on-field duration, while the Projectile
    /// actor's is much shorter (its in-flight time) and its recorded position is a different
    /// point than where the effect actually settled. Abilities with no Zone-named actor at all
    /// are left untouched, since there's nothing to prefer it over.
    /// </summary>
    private static List<PersistentEffectEvent> PreferZoneOverNonZoneSiblings(List<PersistentEffectEvent> events)
    {
        var groupsWithZone = new HashSet<string>();
        foreach (PersistentEffectEvent evt in events)
        {
            string? group = UtilityEffectClassifier.ExtractAbilityGroupKey(evt.ClassPath);
            string? name = UtilityEffectClassifier.ClassNameSegment(evt.ClassPath);
            if (group is not null && name is not null && name.Contains("Zone", StringComparison.OrdinalIgnoreCase))
            {
                groupsWithZone.Add(group);
            }
        }

        if (groupsWithZone.Count == 0)
        {
            return events;
        }

        var filtered = new List<PersistentEffectEvent>(events.Count);
        foreach (PersistentEffectEvent evt in events)
        {
            string? group = UtilityEffectClassifier.ExtractAbilityGroupKey(evt.ClassPath);
            string? name = UtilityEffectClassifier.ClassNameSegment(evt.ClassPath);
            bool isZoneActor = name is not null && name.Contains("Zone", StringComparison.OrdinalIgnoreCase);
            if (group is not null && groupsWithZone.Contains(group) && !isZoneActor)
            {
                continue; // a sibling Zone actor exists for this exact ability -- prefer it.
            }
            filtered.Add(evt);
        }
        return filtered;
    }
}
