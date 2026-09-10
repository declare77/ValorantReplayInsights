using VrfInsights.Analysis.Utility;
using VrfInsights.Data.Tables;
using Xunit;

namespace VrfInsights.Tests;

public class UtilityTimelineBuilderTests
{
    private static ActorRow MakeActor(long guid, long timeMs, string @event, string? classPath = "FXC_Test_Smoke_C", double? x = 1, double? y = 2, double? z = 3) =>
        new(TimeMs: timeMs, PacketId: 0, ChannelIndex: guid, ActorNetGuid: guid, Event: @event,
            ClassPath: classPath, ArchetypePath: null, SpawnX: x, SpawnY: y, SpawnZ: z,
            SpawnPitch: null, SpawnYaw: null, SpawnRoll: null);

    [Fact]
    public void Build_PairsOpenWithNonDormantClose()
    {
        var actors = new List<ActorRow>
        {
            MakeActor(1, 1000, "open"),
            MakeActor(1, 5000, "close", classPath: null, x: null, y: null, z: null),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        PersistentEffectEvent ev = Assert.Single(events);
        Assert.Equal(1000, ev.SpawnTimeMs);
        Assert.Equal(5000, ev.DespawnTimeMs);
        Assert.Equal(UtilityCategory.Smoke, ev.Category);
        Assert.Equal(1, ev.X);
    }

    [Fact]
    public void Build_DormancyDoesNotEndTheEffect_AndWakeUpIsNotANewSpawn()
    {
        var actors = new List<ActorRow>
        {
            MakeActor(1, 1000, "open"),
            MakeActor(1, 2000, "dormant", classPath: null),
            MakeActor(1, 3000, "open", classPath: null), // wake-up, not a new instance
            MakeActor(1, 9000, "close", classPath: null),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        PersistentEffectEvent ev = Assert.Single(events);
        Assert.Equal(1000, ev.SpawnTimeMs); // original spawn, not the 3000 wake-up
        Assert.Equal(9000, ev.DespawnTimeMs);
    }

    [Fact]
    public void Build_SkipsActorsThatDoNotClassifyAsUtility()
    {
        var actors = new List<ActorRow>
        {
            MakeActor(1, 1000, "open", classPath: "Gun_Vandal_C"),
            MakeActor(1, 5000, "close", classPath: null),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        Assert.Empty(events);
    }

    [Fact]
    public void Build_LeavesStillOpenActorsWithNullDespawnTime()
    {
        var actors = new List<ActorRow>
        {
            MakeActor(1, 1000, "open"),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        PersistentEffectEvent ev = Assert.Single(events);
        Assert.Null(ev.DespawnTimeMs);
    }
}
