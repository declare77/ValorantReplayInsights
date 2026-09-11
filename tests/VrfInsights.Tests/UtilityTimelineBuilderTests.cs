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

    // The tests below use the exact class paths from the user's real `dump-classes`/`dump-actors`
    // output against a real export (Omen's smoke = codename Wraith, Jett's = codename Wushu),
    // confirming: (1) the per-player Ability_* container -- which opens once near t=0 at that
    // player's spawn point and is exactly why markers were landing at spawn -- is excluded
    // entirely, and (2) when a cast's Projectile_* and Zone_*/..Zone actors are both present, the
    // Zone one (whose open/close window matches the real effect duration) is kept and the
    // Projectile one (a much shorter in-flight window, different recorded position) is dropped.

    [Fact]
    public void Build_ExcludesAbilityContainerActors_EvenThoughTheyClassifyAsUtility()
    {
        var actors = new List<ActorRow>
        {
            // Opens once near match start at the player's spawn position and never reopens --
            // exactly what a real Ability_Wraith_4_Smoke / Ability_Wushu_4_Smoke row looks like.
            MakeActor(670, 72, "open",
                classPath: "/Game/Characters/Wraith/S0/Ability_4/Ability_Wraith_4_Smoke.Ability_Wraith_4_Smoke_C",
                x: 5700, y: -200, z: 400.4),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        Assert.Empty(events);
    }

    [Fact]
    public void Build_PrefersTheZoneActorsPosition_OverItsProjectileSibling_ForTheSameCast()
    {
        var actors = new List<ActorRow>
        {
            // Projectile: opens, travels briefly, closes.
            MakeActor(4088, 52664, "open",
                classPath: "/Game/Characters/Wraith/S0/Ability_4/Projectile_Wraith_4_Smoke.Projectile_Wraith_4_Smoke_C",
                x: 255.7, y: -2092.5, z: 376.4),
            MakeActor(4088, 55116, "close", classPath: null, x: null, y: null, z: null),
            // Zone: opens shortly after (while the projectile is still in flight), stays open
            // much longer -- matching the smoke's real on-field duration -- at a different
            // position (the actual landing spot).
            MakeActor(4174, 54613, "open",
                classPath: "/Game/Characters/Wraith/S0/Ability_4/Zone_Wraith_4_Smoke.Zone_Wraith_4_Smoke_C",
                x: -1042.3, y: -5020.7, z: 403.3),
            MakeActor(4174, 70613, "close", classPath: null, x: null, y: null, z: null),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        PersistentEffectEvent ev = Assert.Single(events);
        Assert.Equal(-1042.3, ev.X);
        Assert.Equal(-5020.7, ev.Y);
        Assert.Equal(54613, ev.SpawnTimeMs);
        Assert.Equal(70613, ev.DespawnTimeMs);
        Assert.Contains("Zone", ev.ClassPath);
    }

    [Fact]
    public void Build_PrefersZoneActor_UsingJettsGameObjectSmokeZoneNamingToo()
    {
        // Jett's smoke zone class is named "GameObject_Wushu_4_SmokeZone", not "Zone_..." --
        // confirming the "contains Zone" check (not an exact prefix match) is what's needed.
        var actors = new List<ActorRow>
        {
            MakeActor(6136, 181768, "open",
                classPath: "/Game/Characters/Wushu/S0/Ability_4/Projectile_Wushu_4_Smoke.Projectile_Wushu_4_Smoke_C",
                x: 2937.6, y: -912.2, z: 646.4),
            MakeActor(6136, 183449, "close", classPath: null, x: null, y: null, z: null),
            MakeActor(6178, 181948, "open",
                classPath: "/Game/Characters/Wushu/S0/Ability_4/GameObject_Wushu_4_SmokeZone.GameObject_Wushu_4_SmokeZone_C",
                x: 2468.3, y: -931.5, z: 466.1),
            MakeActor(6178, 184941, "close", classPath: null, x: null, y: null, z: null),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        PersistentEffectEvent ev = Assert.Single(events);
        Assert.Equal(2468.3, ev.X);
        Assert.Equal(-931.5, ev.Y);
    }

    [Fact]
    public void Build_DoesNotSuppressAProjectileActor_WhenNoZoneSiblingExistsForThatSameAbility()
    {
        // KAY/O's flash has a projectile but no separate "Zone" actor -- should be completely
        // unaffected by the zone-preference logic.
        var actors = new List<ActorRow>
        {
            MakeActor(1, 1000, "open",
                classPath: "/Game/Characters/Grenadier/S0/Ability_4/Projectile_C_Grenadier_Flash.Projectile_C_Grenadier_Flash_C",
                x: 10, y: 20, z: 30),
            MakeActor(1, 2000, "close", classPath: null, x: null, y: null, z: null),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        PersistentEffectEvent ev = Assert.Single(events);
        Assert.Equal(10, ev.X);
    }

    [Fact]
    public void Build_DoesNotLetAZoneActorForOneAbilitySuppressAProjectileForAnUnrelatedOne()
    {
        // A Zone actor exists for Omen's smoke (Ability_4) -- it must not suppress a Projectile
        // actor belonging to a *different* ability slot/agent that happens to share a category.
        var actors = new List<ActorRow>
        {
            MakeActor(4174, 54613, "open",
                classPath: "/Game/Characters/Wraith/S0/Ability_4/Zone_Wraith_4_Smoke.Zone_Wraith_4_Smoke_C",
                x: -1042.3, y: -5020.7, z: 403.3),
            MakeActor(4174, 70613, "close", classPath: null, x: null, y: null, z: null),
            MakeActor(2, 1000, "open",
                classPath: "/Game/Characters/Grenadier/S0/Ability_4/Projectile_C_Grenadier_Flash.Projectile_C_Grenadier_Flash_C",
                x: 10, y: 20, z: 30),
            MakeActor(2, 2000, "close", classPath: null, x: null, y: null, z: null),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.X == 10);
        Assert.Contains(events, e => e.X == -1042.3);
    }

    [Fact]
    public void Build_NeverPlacesGekkosPlayerControllerAsAUtilityMarker()
    {
        // AggroBot_PC opens once near match start (t=72, in the real export) at Gekko's spawn
        // point and stays open the whole match -- exactly the "permanent marker at spawn" bug
        // this was chasing, caused by "AggroBot" itself containing the "Bot" keyword. Confirmed
        // fixed at the classifier level (UtilityEffectClassifierTests), this is the end-to-end
        // regression guard.
        var actors = new List<ActorRow>
        {
            MakeActor(670, 72, "open",
                classPath: "/Game/Characters/AggroBot/AggroBot_PC.AggroBot_PC_C",
                x: 5700, y: -200, z: 400.4),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        Assert.Empty(events);
    }

    [Fact]
    public void Build_ExcludesWeaponModelActors_EvenIfTheirClassNameAccidentallyMatchesAKeyword()
    {
        var actors = new List<ActorRow>
        {
            MakeActor(1, 1000, "open",
                classPath: "/Game/Characters/Deadeye/S0/Ability_X/Gun_Giantslayer/Gun_Deadeye_X_Giantslayer_Prototype_FIreRatePrototype.Gun_Deadeye_X_Giantslayer_Prototype_FireRatePrototype_C",
                x: 10, y: 20, z: 30),
        };

        IReadOnlyList<PersistentEffectEvent> events = UtilityTimelineBuilder.Build(actors);

        Assert.Empty(events);
    }
}
