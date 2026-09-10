namespace VrfInsights.Data.Tables;

/// <summary>
/// One row of <c>movement.parquet</c> — a character position/rotation/velocity sample.
/// Schema per vrfkit docs/USAGE.md ("movement.parquet"), 14 columns, all NOT NULL:
/// Unreal's left-handed, Z-up coordinate system; position in centimetres, yaw/pitch in
/// degrees in [0, 360), velocity in cm/s.
/// </summary>
/// <param name="TimeMs">Milliseconds since replay start — the global timeline.</param>
/// <param name="PacketId">Packet sequence number.</param>
/// <param name="CharacterNetGuid">The character actor's NetGUID — join key against
/// <see cref="Manifest.ReplayManifest.Players"/>' <c>CharacterNetGuid</c>.</param>
/// <param name="Timestamp">A 128.0 Hz server tick. Resets at every round boundary — use this
/// only for in-round alignment, never as a global timeline (use <see cref="TimeMs"/> for that).</param>
/// <param name="MovementState">Posture byte. vrfkit's corpus measurements found this constant
/// (0) across every replay sampled so far — crouch is <c>bCrouchHeld</c> in fields.parquet, or a
/// ~19 cm drop in PosZ, not this column.</param>
/// <param name="MoveType">0 = variant without velocity, 1 = variant with velocity.</param>
public sealed record MovementRow(
    long TimeMs,
    long PacketId,
    long CharacterNetGuid,
    double PosX,
    double PosY,
    double PosZ,
    double Yaw,
    double Pitch,
    double VelX,
    double VelY,
    double VelZ,
    long Timestamp,
    long MovementState,
    long MoveType)
{
    public static MovementRow FromRow(IReadOnlyDictionary<string, object> row) => new(
        RowConvert.ToLong(row, "time_ms") ?? 0,
        RowConvert.ToLong(row, "packet_id") ?? 0,
        RowConvert.ToLong(row, "character_net_guid") ?? 0,
        RowConvert.ToDouble(row, "pos_x") ?? 0,
        RowConvert.ToDouble(row, "pos_y") ?? 0,
        RowConvert.ToDouble(row, "pos_z") ?? 0,
        RowConvert.ToDouble(row, "yaw") ?? 0,
        RowConvert.ToDouble(row, "pitch") ?? 0,
        RowConvert.ToDouble(row, "vel_x") ?? 0,
        RowConvert.ToDouble(row, "vel_y") ?? 0,
        RowConvert.ToDouble(row, "vel_z") ?? 0,
        RowConvert.ToLong(row, "timestamp") ?? 0,
        RowConvert.ToLong(row, "movement_state") ?? 0,
        RowConvert.ToLong(row, "move_type") ?? 0);

    public static List<MovementRow> LoadAll(ParquetTable table) =>
        table.Rows.Select(FromRow).ToList();
}
