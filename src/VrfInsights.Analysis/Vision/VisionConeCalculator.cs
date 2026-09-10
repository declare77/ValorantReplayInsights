using VrfInsights.Analysis.Movement;

namespace VrfInsights.Analysis.Vision;

/// <summary>
/// VALORANT does not replicate anything called a "vision cone" — this derives one from what
/// <c>movement.parquet</c> actually gives us (position + yaw, both exact per vrfkit's
/// cross-validation against the reference C# parser) plus VALORANT's publicly known default
/// horizontal field of view. This is ordinary trigonometry on already-decoded data, not
/// something reverse-engineered from the replay format.
///
/// <para>The defaults below (103° FOV, 180 m range, ~155 cm eye height) are reasonable
/// approximations, not values read from the replay — a player's actual FOV setting is a local
/// client option that isn't part of what a replay contains, and per-agent eye height varies
/// slightly. Override them if you need a closer match.</para>
/// </summary>
public static class VisionConeCalculator
{
    public const double DefaultFovDegrees = 103.0;
    public const double DefaultRangeCm = 18_000.0;
    public const double EyeHeightCm = 155.0;

    public static IReadOnlyList<VisionCone> Build(
        PlayerTrack track,
        double fovDegrees = DefaultFovDegrees,
        double rangeCm = DefaultRangeCm,
        double eyeHeightCm = EyeHeightCm)
    {
        double halfAngle = fovDegrees / 2.0;
        var cones = new List<VisionCone>(track.Samples.Count);
        foreach (MovementSample sample in track.Samples)
        {
            cones.Add(new VisionCone(
                TimeMs: sample.TimeMs,
                OriginX: sample.PosX,
                OriginY: sample.PosY,
                OriginZ: sample.PosZ + eyeHeightCm,
                ForwardYawDeg: sample.Yaw,
                HalfAngleDeg: halfAngle,
                RangeCm: rangeCm));
        }

        return cones;
    }
}
