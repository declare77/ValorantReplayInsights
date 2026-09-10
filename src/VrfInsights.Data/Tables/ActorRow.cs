namespace VrfInsights.Data.Tables;

/// <summary>
/// One row of <c>actors.parquet</c> — one row per actor-channel lifecycle event.
/// Schema per vrfkit docs/USAGE.md ("actors.parquet").
///
/// <para><b><see cref="Event"/> is "open" / "close" / "dormant" — not "spawn" / "despawn".</b>
/// A "dormant" close means the server stopped replicating an actor that is still alive (it may
/// "wake up" later as another "open" of the same instance); only a non-dormant "close" is the
/// actor actually going away. Code pairing spawns with despawns must treat "dormant" as neither.</para>
///
/// <para>This is where weapon and ability/utility actor classes are found — including actors
/// that never produce a single row in fields.parquet (DefuserItem, HeavyArmorItem, etc.) still
/// show up here purely because they opened a channel.</para>
/// </summary>
public sealed record ActorRow(
    long TimeMs,
    long PacketId,
    long ChannelIndex,
    long ActorNetGuid,
    string Event,
    string? ClassPath,
    string? ArchetypePath,
    double? SpawnX,
    double? SpawnY,
    double? SpawnZ,
    double? SpawnPitch,
    double? SpawnYaw,
    double? SpawnRoll)
{
    public bool IsOpen => Event == "open";
    public bool IsClose => Event == "close";
    public bool IsDormant => Event == "dormant";

    public static ActorRow FromRow(IReadOnlyDictionary<string, object> row) => new(
        RowConvert.ToLong(row, "time_ms") ?? 0,
        RowConvert.ToLong(row, "packet_id") ?? 0,
        RowConvert.ToLong(row, "channel_index") ?? 0,
        RowConvert.ToLong(row, "actor_net_guid") ?? 0,
        RowConvert.ToStringValue(row, "event") ?? "",
        RowConvert.ToStringValue(row, "class_path"),
        RowConvert.ToStringValue(row, "archetype_path"),
        RowConvert.ToDouble(row, "spawn_x"),
        RowConvert.ToDouble(row, "spawn_y"),
        RowConvert.ToDouble(row, "spawn_z"),
        RowConvert.ToDouble(row, "spawn_pitch"),
        RowConvert.ToDouble(row, "spawn_yaw"),
        RowConvert.ToDouble(row, "spawn_roll"));

    public static List<ActorRow> LoadAll(ParquetTable table) =>
        table.Rows.Select(FromRow).ToList();
}
