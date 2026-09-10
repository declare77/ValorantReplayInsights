using VrfInsights.Analysis.Identity;
using VrfInsights.Analysis.Movement;
using VrfInsights.Analysis.Vision;
using Xunit;

namespace VrfInsights.Tests;

public class VisionConeCalculatorTests
{
    private static PlayerIdentity MakePlayer() => new(
        Subject: "subject-1",
        ActorNetGuid: 1,
        CharacterNetGuid: 2,
        AgentName: "TestAgent",
        CharacterId: "char-id",
        SkinId: null,
        SprayIds: System.Array.Empty<string>());

    [Fact]
    public void Build_OneConePerMovementSample_WithEyeHeightAddedToZ()
    {
        var track = new PlayerTrack(
            MakePlayer(),
            new List<MovementSample>
            {
                new(TimeMs: 100, PosX: 0, PosY: 0, PosZ: 0, Yaw: 90, Pitch: 0, VelX: 0, VelY: 0, VelZ: 0),
                new(TimeMs: 200, PosX: 10, PosY: 20, PosZ: 30, Yaw: 180, Pitch: 0, VelX: 0, VelY: 0, VelZ: 0),
            });

        IReadOnlyList<VisionCone> cones = VisionConeCalculator.Build(track, fovDegrees: 100, rangeCm: 1000, eyeHeightCm: 155);

        Assert.Equal(2, cones.Count);

        Assert.Equal(100, cones[0].TimeMs);
        Assert.Equal(0, cones[0].OriginX);
        Assert.Equal(0, cones[0].OriginY);
        Assert.Equal(155, cones[0].OriginZ);
        Assert.Equal(90, cones[0].ForwardYawDeg);
        Assert.Equal(50, cones[0].HalfAngleDeg);
        Assert.Equal(1000, cones[0].RangeCm);

        Assert.Equal(30 + 155, cones[1].OriginZ);
    }

    [Fact]
    public void ToTopDownPolygon_StartsAtOriginAndSpansTheConfiguredArc()
    {
        var cone = new VisionCone(TimeMs: 0, OriginX: 0, OriginY: 0, OriginZ: 0, ForwardYawDeg: 0, HalfAngleDeg: 45, RangeCm: 100);

        IReadOnlyList<(double X, double Y)> polygon = cone.ToTopDownPolygon(arcSegments: 4);

        // origin + (arcSegments + 1) arc points
        Assert.Equal(6, polygon.Count);
        Assert.Equal((0.0, 0.0), polygon[0]);

        // Every arc point should be exactly RangeCm from the origin.
        for (int i = 1; i < polygon.Count; i++)
        {
            double distance = System.Math.Sqrt((polygon[i].X * polygon[i].X) + (polygon[i].Y * polygon[i].Y));
            Assert.True(System.Math.Abs(distance - 100) < 1e-6, $"point {i} was {distance} cm from origin, expected ~100");
        }
    }
}
