using VrfInsights.Analysis.Identity;
using VrfInsights.Data;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Movement;

public sealed record MovementSample(
    long TimeMs,
    double PosX,
    double PosY,
    double PosZ,
    double Yaw,
    double Pitch,
    double VelX,
    double VelY,
    double VelZ);

public sealed record PlayerTrack(
    PlayerIdentity Player,
    IReadOnlyList<MovementSample> Samples);

/// <summary>Groups <c>movement.parquet</c> by <c>character_net_guid</c> and attaches player
/// identity, sorted into a per-player time series on the global (<see cref="MovementRow.TimeMs"/>)
/// clock. Use <see cref="MovementRow.Timestamp"/> instead only when you specifically need the
/// 128 Hz in-round tick (it resets every round).</summary>
public static class MovementTimelineBuilder
{
    public static IReadOnlyList<PlayerTrack> Build(VrfExportSet export, IReadOnlyList<PlayerIdentity> players)
    {
        var byCharacterGuid = new Dictionary<long, List<MovementRow>>();
        foreach (MovementRow row in export.Movement)
        {
            if (!byCharacterGuid.TryGetValue(row.CharacterNetGuid, out List<MovementRow>? list))
            {
                list = new List<MovementRow>();
                byCharacterGuid[row.CharacterNetGuid] = list;
            }

            list.Add(row);
        }

        var tracks = new List<PlayerTrack>(players.Count);
        foreach (PlayerIdentity player in players)
        {
            if (player.CharacterNetGuid is not long guid || !byCharacterGuid.TryGetValue(guid, out List<MovementRow>? rows))
            {
                tracks.Add(new PlayerTrack(player, Array.Empty<MovementSample>()));
                continue;
            }

            rows.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            var samples = new List<MovementSample>(rows.Count);
            foreach (MovementRow row in rows)
            {
                samples.Add(new MovementSample(
                    row.TimeMs, row.PosX, row.PosY, row.PosZ, row.Yaw, row.Pitch, row.VelX, row.VelY, row.VelZ));
            }

            tracks.Add(new PlayerTrack(player, samples));
        }

        return tracks;
    }
}
