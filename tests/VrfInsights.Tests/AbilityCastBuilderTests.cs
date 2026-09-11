using VrfInsights.Analysis.Abilities;
using VrfInsights.Data.Tables;
using Xunit;

namespace VrfInsights.Tests;

public class AbilityCastBuilderTests
{
    private const string GroupPath = "/Game/Characters/_Core/Comp_AbilityStatisticsReplicator.Comp_AbilityStatisticsReplicator_C";

    private static FieldRow MakeField(string fieldName, long timeMs, long actorNetGuid, string? valueStr = null, long? valueI64 = null, double? valueF64 = null) =>
        new(TimeMs: timeMs, PacketId: 0, ChannelIndex: 0, ActorNetGuid: actorNetGuid, ObjectNetGuid: null,
            GroupPath: GroupPath, Handle: 0, FieldName: fieldName, BitCount: 0, RawBits: null,
            ValueI64: valueI64, ValueF64: valueF64, ValueBool: null, ValueStr: valueStr, CompatibleChecksum: null);

    // Field names below match exactly what `vrf-insights dump-fields --group
    // Comp_AbilityStatisticsReplicator` showed against a real export: every member carries
    // vrfkit's own "_<number>_<hash>" disambiguation suffix, and CastLocation is one field whose
    // value is a bare "(X,Y,Z)" string -- not three separate .X/.Y/.Z fields.
    private static List<FieldRow> OneCastAt(long timeMs, long actorNetGuid, int index, string subject, string locationStr) => new()
    {
        MakeField($"AbilityCastsThisRound[{index}].Player_11_0963330440D68BDF1A8E34B035420342", timeMs, actorNetGuid, valueStr: subject),
        MakeField($"AbilityCastsThisRound[{index}].Slot_12_22D571914FAFD5F0EBD400B7E2F28B36", timeMs, actorNetGuid, valueI64: 1),
        MakeField($"AbilityCastsThisRound[{index}].Round_22_905E6CC0448D2C6270A94C9690101E49", timeMs, actorNetGuid, valueI64: 3),
        MakeField($"AbilityCastsThisRound[{index}].RoundPhase_25_84478C0047988409FEEC9E95C15DFB02", timeMs, actorNetGuid, valueI64: 2),
        MakeField($"AbilityCastsThisRound[{index}].CastTime_4_5AE288704801A9B74D6D159DFC2BD147", timeMs, actorNetGuid, valueF64: 12.5),
        MakeField($"AbilityCastsThisRound[{index}].CastLocation_21_61F4B6BC47A10FE8CD34D29141FC9B88", timeMs, actorNetGuid, valueStr: locationStr),
    };

    [Fact]
    public void Build_ParsesRealFieldNamingAndLocationString()
    {
        List<FieldRow> fields = OneCastAt(timeMs: 46135, actorNetGuid: 746, index: 0, subject: "player-subject-1",
            locationStr: "(1042.0601806640625,3786.67578125,282.1315002441406)");

        IReadOnlyList<AbilityCastEvent> events = AbilityCastBuilder.Build(fields);

        AbilityCastEvent cast = Assert.Single(events);
        Assert.Equal("player-subject-1", cast.Subject);
        Assert.Equal(1, cast.Slot);
        Assert.Equal(3, cast.Round);
        Assert.Equal(2, cast.RoundPhase);
        Assert.Equal(12.5, cast.CastTimeSeconds);
        Assert.Equal(46135, cast.FirstObservedAtMs);
        Assert.NotNull(cast.CastX);
        Assert.NotNull(cast.CastY);
        Assert.NotNull(cast.CastZ);
        Assert.Equal(1042.0601806640625, cast.CastX!.Value, precision: 6);
        Assert.Equal(3786.67578125, cast.CastY!.Value, precision: 6);
        Assert.Equal(282.1315002441406, cast.CastZ!.Value, precision: 6);
    }

    [Fact]
    public void Build_KeepsEarliestSnapshotPerActorAndIndex_IgnoringLaterResends()
    {
        List<FieldRow> fields = OneCastAt(timeMs: 46135, actorNetGuid: 746, index: 0, subject: "player-subject-1",
            locationStr: "(100,200,300)");
        // A later whole-array resend of the same slot -- should be ignored in favor of the earlier one.
        fields.AddRange(OneCastAt(timeMs: 90000, actorNetGuid: 746, index: 0, subject: "player-subject-1",
            locationStr: "(999,999,999)"));

        IReadOnlyList<AbilityCastEvent> events = AbilityCastBuilder.Build(fields);

        AbilityCastEvent cast = Assert.Single(events);
        Assert.Equal(46135, cast.FirstObservedAtMs);
        Assert.Equal(100, cast.CastX);
    }

    [Fact]
    public void Build_LeavesLocationNull_WhenCastLocationMemberIsMissing()
    {
        List<FieldRow> fields = OneCastAt(timeMs: 1000, actorNetGuid: 1, index: 0, subject: "s", locationStr: "(1,2,3)")
            .Where(f => f.FieldName is not null && !f.FieldName.Contains("CastLocation", StringComparison.Ordinal))
            .ToList();

        IReadOnlyList<AbilityCastEvent> events = AbilityCastBuilder.Build(fields);

        AbilityCastEvent cast = Assert.Single(events);
        Assert.Null(cast.CastX);
        Assert.Null(cast.CastY);
        Assert.Null(cast.CastZ);
        // Everything else should still resolve -- a missing/malformed location shouldn't take
        // down the rest of the cast record with it.
        Assert.Equal("s", cast.Subject);
        Assert.Equal(3, cast.Round);
    }

    [Fact]
    public void Build_TreatsDistinctIndicesAsSeparateCasts()
    {
        List<FieldRow> fields = OneCastAt(timeMs: 1000, actorNetGuid: 1, index: 0, subject: "s", locationStr: "(1,2,3)");
        fields.AddRange(OneCastAt(timeMs: 2000, actorNetGuid: 1, index: 1, subject: "s", locationStr: "(4,5,6)"));

        IReadOnlyList<AbilityCastEvent> events = AbilityCastBuilder.Build(fields);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.CastX == 1);
        Assert.Contains(events, e => e.CastX == 4);
    }
}
