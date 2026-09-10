using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Rounds;

/// <param name="RoundNumber">From the <c>roundStarted</c> event's Word0, per vrfkit's
/// documented Event-chunk payload layout.</param>
/// <param name="StartTimeMs">Global timeline (<see cref="EventRow.Time1"/>) of the
/// <c>roundStarted</c> event.</param>
/// <param name="EndTimeMs">The next round's start, or null for the last round in the replay
/// (i.e. "runs to the end of the file").</param>
public sealed record RoundInfo(int RoundNumber, long StartTimeMs, long? EndTimeMs)
{
    public bool Contains(long timeMs) => timeMs >= StartTimeMs && (EndTimeMs is null || timeMs < EndTimeMs);
}

/// <summary>A server-authored timeline event from <c>events.parquet</c>, kept close to the raw
/// row — <see cref="Group"/> and the raw <see cref="Word0"/>/<see cref="Word1"/> are always
/// present from the wire; <see cref="PayloadName"/> is vrfkit's structural overlay and is only
/// non-null when its arity/tag/name/time cross-check passed for this row.</summary>
public sealed record MatchEvent(
    string Group,
    long TimeMs,
    long? Word0,
    long? Word1,
    string? PayloadName,
    int? RoundNumber);

public static class RoundTimelineBuilder
{
    public static IReadOnlyList<RoundInfo> BuildRounds(IReadOnlyList<EventRow> events)
    {
        var starts = new List<(int RoundNumber, long TimeMs)>();
        foreach (EventRow ev in events)
        {
            if (ev.Group == "roundStarted" && ev.Word0 is long w0)
            {
                starts.Add(((int)w0, ev.Time1));
            }
        }

        starts.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

        var rounds = new List<RoundInfo>(starts.Count);
        for (int i = 0; i < starts.Count; i++)
        {
            long? end = i + 1 < starts.Count ? starts[i + 1].TimeMs : null;
            rounds.Add(new RoundInfo(starts[i].RoundNumber, starts[i].TimeMs, end));
        }

        return rounds;
    }

    public static IReadOnlyList<MatchEvent> BuildEventTimeline(IReadOnlyList<EventRow> events, IReadOnlyList<RoundInfo> rounds)
    {
        var result = new List<MatchEvent>(events.Count);
        foreach (EventRow ev in events)
        {
            int? roundNumber = null;
            foreach (RoundInfo round in rounds)
            {
                if (round.Contains(ev.Time1))
                {
                    roundNumber = round.RoundNumber;
                    break;
                }
            }

            result.Add(new MatchEvent(ev.Group, ev.Time1, ev.Word0, ev.Word1, ev.PayloadName, roundNumber));
        }

        result.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return result;
    }
}
