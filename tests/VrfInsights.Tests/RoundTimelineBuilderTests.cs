using VrfInsights.Analysis.Rounds;
using VrfInsights.Data.Tables;
using Xunit;

namespace VrfInsights.Tests;

public class RoundTimelineBuilderTests
{
    private static EventRow MakeEvent(string group, long time1, long? word0 = null, long? word1 = null) =>
        new(Id: null, Group: group, Metadata: null, Time1: time1, Time2: time1, PayloadSize: 0, RawPayload: null,
            Word0: word0, Word1: word1, PayloadTag: null, PayloadName: null, PayloadSeconds: null);

    [Fact]
    public void BuildRounds_UsesRoundStartedWord0AsRoundNumber_AndNextStartAsEnd()
    {
        var events = new List<EventRow>
        {
            MakeEvent("roundStarted", time1: 1000, word0: 1),
            MakeEvent("roundStarted", time1: 5000, word0: 2),
            MakeEvent("characterDeath", time1: 3000, word0: 10, word1: 20),
        };

        IReadOnlyList<RoundInfo> rounds = RoundTimelineBuilder.BuildRounds(events);

        Assert.Equal(2, rounds.Count);
        Assert.Equal(1, rounds[0].RoundNumber);
        Assert.Equal(1000, rounds[0].StartTimeMs);
        Assert.Equal(5000, rounds[0].EndTimeMs);

        Assert.Equal(2, rounds[1].RoundNumber);
        Assert.Equal(5000, rounds[1].StartTimeMs);
        Assert.Null(rounds[1].EndTimeMs);
    }

    [Fact]
    public void BuildRounds_SortsOutOfOrderEvents()
    {
        var events = new List<EventRow>
        {
            MakeEvent("roundStarted", time1: 9000, word0: 3),
            MakeEvent("roundStarted", time1: 1000, word0: 1),
            MakeEvent("roundStarted", time1: 5000, word0: 2),
        };

        IReadOnlyList<RoundInfo> rounds = RoundTimelineBuilder.BuildRounds(events);

        Assert.Equal(new[] { 1, 2, 3 }, rounds.Select(r => r.RoundNumber));
        Assert.Equal(new long[] { 1000, 5000, 9000 }, rounds.Select(r => r.StartTimeMs));
    }

    [Fact]
    public void BuildEventTimeline_AttributesEventsToTheirEnclosingRound()
    {
        var events = new List<EventRow>
        {
            MakeEvent("roundStarted", time1: 1000, word0: 1),
            MakeEvent("roundStarted", time1: 5000, word0: 2),
            MakeEvent("characterDeath", time1: 3000, word0: 10, word1: 20),
            MakeEvent("characterDeath", time1: 6000, word0: 11, word1: 21),
        };

        IReadOnlyList<RoundInfo> rounds = RoundTimelineBuilder.BuildRounds(events);
        IReadOnlyList<MatchEvent> timeline = RoundTimelineBuilder.BuildEventTimeline(events, rounds);

        MatchEvent firstDeath = Assert.Single(timeline, e => e.TimeMs == 3000);
        Assert.Equal(1, firstDeath.RoundNumber);

        MatchEvent secondDeath = Assert.Single(timeline, e => e.TimeMs == 6000);
        Assert.Equal(2, secondDeath.RoundNumber);
    }

    [Fact]
    public void RoundInfo_Contains_IsHalfOpenInterval()
    {
        var round = new RoundInfo(RoundNumber: 1, StartTimeMs: 1000, EndTimeMs: 2000);

        Assert.False(round.Contains(999));
        Assert.True(round.Contains(1000));
        Assert.True(round.Contains(1999));
        Assert.False(round.Contains(2000));
    }
}
