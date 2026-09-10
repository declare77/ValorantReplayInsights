using VrfInsights.Analysis.Common;
using VrfInsights.Data.Tables;
using Xunit;

namespace VrfInsights.Tests;

public class FlattenedArrayPivotTests
{
    private static FieldRow MakeRow(long actor, long channel, long timeMs, string fieldName, object? value)
    {
        long? valueI64 = value as long?;
        double? valueF64 = value as double?;
        bool? valueBool = value as bool?;
        string? valueStr = value as string;

        return new FieldRow(
            TimeMs: timeMs,
            PacketId: 0,
            ChannelIndex: channel,
            ActorNetGuid: actor,
            ObjectNetGuid: null,
            GroupPath: "Comp_AbilityStatisticsReplicator",
            Handle: 1,
            FieldName: fieldName,
            BitCount: 32,
            RawBits: null,
            ValueI64: valueI64,
            ValueF64: valueF64,
            ValueBool: valueBool,
            ValueStr: valueStr,
            CompatibleChecksum: null);
    }

    [Fact]
    public void Pivot_GroupsMembersByActorChannelIndexAndTime()
    {
        var rows = new List<FieldRow>
        {
            MakeRow(actor: 10, channel: 1, timeMs: 5000, fieldName: "AbilityCastsThisRound[0].Player", value: "subject-abc"),
            MakeRow(actor: 10, channel: 1, timeMs: 5000, fieldName: "AbilityCastsThisRound[0].Slot", value: 2L),
            MakeRow(actor: 10, channel: 1, timeMs: 5000, fieldName: "AbilityCastsThisRound[0].CastTime", value: 12.5),
            // A later re-send of the same index should be a separate snapshot (different time_ms).
            MakeRow(actor: 10, channel: 1, timeMs: 9000, fieldName: "AbilityCastsThisRound[0].Player", value: "subject-abc"),
            // A different actor/index should not be merged in.
            MakeRow(actor: 20, channel: 2, timeMs: 5000, fieldName: "AbilityCastsThisRound[1].Player", value: "subject-def"),
        };

        IReadOnlyList<FlattenedArrayPivot.ArrayElementSnapshot> snapshots = FlattenedArrayPivot.Pivot(rows, "AbilityCastsThisRound");

        Assert.Equal(3, snapshots.Count);

        FlattenedArrayPivot.ArrayElementSnapshot first = Assert.Single(snapshots, s => s.ActorNetGuid == 10 && s.TimeMs == 5000);
        Assert.Equal(0, first.Index);
        Assert.Equal("subject-abc", first.GetString("Player"));
        Assert.Equal(2L, first.GetLong("Slot"));
        Assert.Equal(12.5, first.GetDouble("CastTime"));

        FlattenedArrayPivot.ArrayElementSnapshot resend = Assert.Single(snapshots, s => s.ActorNetGuid == 10 && s.TimeMs == 9000);
        Assert.Equal(0, resend.Index);
        Assert.Equal("subject-abc", resend.GetString("Player"));
        Assert.Null(resend.GetLong("Slot")); // not re-sent at this tick

        FlattenedArrayPivot.ArrayElementSnapshot other = Assert.Single(snapshots, s => s.ActorNetGuid == 20);
        Assert.Equal(1, other.Index);
        Assert.Equal("subject-def", other.GetString("Player"));
    }

    [Fact]
    public void Pivot_IgnoresFieldsThatDoNotMatchTheArrayPrefix()
    {
        var rows = new List<FieldRow>
        {
            MakeRow(actor: 10, channel: 1, timeMs: 1000, fieldName: "SomeOtherField", value: 1L),
            MakeRow(actor: 10, channel: 1, timeMs: 1000, fieldName: "AbilityCastsThisRoundExtra[0].Foo", value: 1L),
        };

        IReadOnlyList<FlattenedArrayPivot.ArrayElementSnapshot> snapshots = FlattenedArrayPivot.Pivot(rows, "AbilityCastsThisRound");

        Assert.Empty(snapshots);
    }
}
