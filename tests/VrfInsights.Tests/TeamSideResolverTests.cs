using VrfInsights.Analysis.Identity;
using VrfInsights.Analysis.Rounds;
using VrfInsights.Data;
using VrfInsights.Data.Manifest;
using VrfInsights.Data.Tables;
using Xunit;

namespace VrfInsights.Tests;

public class TeamSideResolverTests
{
    private static PlayerIdentity MakePlayer(string subject, long actorNetGuid, long characterNetGuid) =>
        new(Subject: subject, ActorNetGuid: actorNetGuid, CharacterNetGuid: characterNetGuid,
            AgentName: "Agent_" + subject, CharacterId: null, SkinId: null, SprayIds: Array.Empty<string>());

    private static ActorRow MakeSpawn(long actorNetGuid, double x, double y, long timeMs = 0) =>
        new(TimeMs: timeMs, PacketId: 0, ChannelIndex: 0, ActorNetGuid: actorNetGuid, Event: "open",
            ClassPath: "/Game/Characters/SpawnedCharacter.SpawnedCharacter_C", ArchetypePath: null,
            SpawnX: x, SpawnY: y, SpawnZ: 0, SpawnPitch: 0, SpawnYaw: 0, SpawnRoll: 0);

    private static ActorRow MakeBombActor(long bombActorNetGuid, long timeMs) =>
        new(TimeMs: timeMs, PacketId: 0, ChannelIndex: 0, ActorNetGuid: bombActorNetGuid, Event: "open",
            ClassPath: "/Game/Weapons/BombEquippable.BombEquippable_C", ArchetypePath: null,
            SpawnX: null, SpawnY: null, SpawnZ: null, SpawnPitch: null, SpawnYaw: null, SpawnRoll: null);

    private static FieldRow MakeOwnerWrite(long bombActorNetGuid, long timeMs, long ownerCharacterGuid) =>
        new(TimeMs: timeMs, PacketId: 0, ChannelIndex: 0, ActorNetGuid: bombActorNetGuid, ObjectNetGuid: null,
            GroupPath: "BombEquippable", Handle: 0, FieldName: "Owner", BitCount: 0, RawBits: null,
            ValueI64: ownerCharacterGuid, ValueF64: null, ValueBool: null, ValueStr: null, CompatibleChecksum: null);

    private static VrfExportSet MakeExport(IReadOnlyList<ActorRow> actors, IReadOnlyList<FieldRow> fields) => new()
    {
        Manifest = new ReplayManifest(),
        Fields = fields,
        Movement = Array.Empty<MovementRow>(),
        Actors = actors,
        NetGuids = Array.Empty<NetGuidRow>(),
        Events = Array.Empty<EventRow>(),
    };

    // Two teams of two, spawning in two rooms far apart: {0,0}/{10,10} vs {1000,1000}/{1010,990}.
    private static readonly PlayerIdentity RoomAPlayer1 = MakePlayer("subA1", actorNetGuid: 1, characterNetGuid: 101);
    private static readonly PlayerIdentity RoomAPlayer2 = MakePlayer("subA2", actorNetGuid: 2, characterNetGuid: 102);
    private static readonly PlayerIdentity RoomBPlayer1 = MakePlayer("subB1", actorNetGuid: 3, characterNetGuid: 201);
    private static readonly PlayerIdentity RoomBPlayer2 = MakePlayer("subB2", actorNetGuid: 4, characterNetGuid: 202);

    private static List<PlayerIdentity> FourPlayers() => new() { RoomAPlayer1, RoomAPlayer2, RoomBPlayer1, RoomBPlayer2 };

    private static List<ActorRow> FourPlayerSpawns() => new()
    {
        MakeSpawn(101, 0, 0),
        MakeSpawn(102, 10, 10),
        MakeSpawn(201, 1000, 1000),
        MakeSpawn(202, 1010, 990),
    };

    [Fact]
    public void Build_UsesSpikeCarrierToPickAttackingRoom_AndAppliesItToTheWholeHalf()
    {
        List<ActorRow> actors = FourPlayerSpawns();
        actors.Add(MakeBombActor(bombActorNetGuid: 500, timeMs: 1400));
        var fields = new List<FieldRow> { MakeOwnerWrite(bombActorNetGuid: 500, timeMs: 1500, ownerCharacterGuid: 201) };

        var rounds = new List<RoundInfo>
        {
            new RoundInfo(RoundNumber: 1, StartTimeMs: 1000, EndTimeMs: 5000),
            new RoundInfo(RoundNumber: 2, StartTimeMs: 5000, EndTimeMs: 9000),
        };

        IReadOnlyList<RoundSides> sides = TeamSideResolver.Build(
            MakeExport(actors, fields), FourPlayers(), rounds, events: Array.Empty<MatchEvent>());

        Assert.Equal(2, sides.Count);
        foreach (RoundSides round in sides)
        {
            Assert.Equal(new long[] { 3, 4 }, round.AttackingActorNetGuids.OrderBy(g => g));
            Assert.Equal(new long[] { 1, 2 }, round.DefendingActorNetGuids.OrderBy(g => g));
        }
    }

    [Fact]
    public void Build_FlipsSidesAfterSwitchTeams_EvenWithoutFreshCarrierEvidence()
    {
        List<ActorRow> actors = FourPlayerSpawns();
        actors.Add(MakeBombActor(bombActorNetGuid: 500, timeMs: 1400));
        var fields = new List<FieldRow> { MakeOwnerWrite(bombActorNetGuid: 500, timeMs: 1500, ownerCharacterGuid: 201) };

        var rounds = new List<RoundInfo>
        {
            new RoundInfo(RoundNumber: 1, StartTimeMs: 1000, EndTimeMs: 5000),
            new RoundInfo(RoundNumber: 2, StartTimeMs: 9000, EndTimeMs: 13000), // after the half-swap, no bomb evidence at all
        };
        var events = new List<MatchEvent> { new MatchEvent("switchTeams", TimeMs: 9000, Word0: null, Word1: null, PayloadName: null, RoundNumber: null) };

        IReadOnlyList<RoundSides> sides = TeamSideResolver.Build(MakeExport(actors, fields), FourPlayers(), rounds, events);

        Assert.Equal(2, sides.Count);
        RoundSides round1 = sides.Single(r => r.RoundNumber == 1);
        Assert.Equal(new long[] { 3, 4 }, round1.AttackingActorNetGuids.OrderBy(g => g));

        RoundSides round2 = sides.Single(r => r.RoundNumber == 2);
        Assert.Equal(new long[] { 1, 2 }, round2.AttackingActorNetGuids.OrderBy(g => g)); // flipped
        Assert.Equal(new long[] { 3, 4 }, round2.DefendingActorNetGuids.OrderBy(g => g));
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenThereIsNoCarrierEvidenceAnywhere()
    {
        List<ActorRow> actors = FourPlayerSpawns(); // spawns exist, but the bomb is never touched
        var rounds = new List<RoundInfo> { new RoundInfo(RoundNumber: 1, StartTimeMs: 1000, EndTimeMs: 5000) };

        IReadOnlyList<RoundSides> sides = TeamSideResolver.Build(
            MakeExport(actors, Array.Empty<FieldRow>()), FourPlayers(), rounds, events: Array.Empty<MatchEvent>());

        Assert.Empty(sides);
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenSpawnPositionsAreMissing()
    {
        var actors = new List<ActorRow>(); // no spawn data at all
        var rounds = new List<RoundInfo> { new RoundInfo(RoundNumber: 1, StartTimeMs: 1000, EndTimeMs: 5000) };

        IReadOnlyList<RoundSides> sides = TeamSideResolver.Build(
            MakeExport(actors, Array.Empty<FieldRow>()), FourPlayers(), rounds, events: Array.Empty<MatchEvent>());

        Assert.Empty(sides);
    }
}
