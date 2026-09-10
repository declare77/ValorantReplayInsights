using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Loadouts;

/// <param name="ActorNetGuid">The actor <c>MoneyManagementComponent</c> replicated on. Joined
/// against <see cref="Identity.PlayerIdentity.ActorNetGuid"/> — this assumes the component
/// replicates on the player's own PlayerState actor rather than a separate subobject; if your
/// export shows otherwise (check the row's <c>object_net_guid</c> against
/// <c>net_guids.parquet</c>), join through that instead.</param>
public sealed record EconomySnapshot(
    long ActorNetGuid,
    long TimeMs,
    long? Money,
    long? StartOfRoundMoney,
    long? TotalMoneyGranted);

/// <summary>
/// Reads <c>MoneyManagementComponent.{Money,StartOfRoundMoney,TotalMoneyGranted}</c> — plain
/// Int32 scalars per vrfkit's docs/DATA.md "Economy" table, so unlike CombatReport or ability
/// casts there's no array flattening involved here, just a straight field filter.
/// </summary>
public static class EconomySnapshotBuilder
{
    public static IReadOnlyList<EconomySnapshot> Build(IReadOnlyList<FieldRow> fields)
    {
        var byActorAndTime = new Dictionary<(long Actor, long TimeMs), EconomySnapshot>();

        foreach (FieldRow row in fields)
        {
            if (row.FieldName is not ("Money" or "StartOfRoundMoney" or "TotalMoneyGranted") ||
                !row.GroupPath.Contains("MoneyManagementComponent", StringComparison.Ordinal))
            {
                continue;
            }

            var key = (row.ActorNetGuid, row.TimeMs);
            if (!byActorAndTime.TryGetValue(key, out EconomySnapshot? snap))
            {
                snap = new EconomySnapshot(row.ActorNetGuid, row.TimeMs, null, null, null);
            }

            long? value = row.Value switch { long l => l, double d => (long)d, _ => (long?)null };
            snap = row.FieldName switch
            {
                "Money" => snap with { Money = value },
                "StartOfRoundMoney" => snap with { StartOfRoundMoney = value },
                "TotalMoneyGranted" => snap with { TotalMoneyGranted = value },
                _ => snap
            };

            byActorAndTime[key] = snap;
        }

        var results = new List<EconomySnapshot>(byActorAndTime.Values);
        results.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return results;
    }
}
