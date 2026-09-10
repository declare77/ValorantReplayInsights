namespace VrfInsights.Analysis.Vision;

/// <param name="OriginX">Eye position — movement's PosX/Y/Z plus <see cref="VisionConeCalculator.EyeHeightCm"/>.</param>
/// <param name="ForwardYawDeg">The UE yaw this cone points along, in degrees [0, 360).</param>
/// <param name="HalfAngleDeg">Half of the configured field of view.</param>
/// <param name="RangeCm">How far the cone extends, in centimetres.</param>
public sealed record VisionCone(
    long TimeMs,
    double OriginX,
    double OriginY,
    double OriginZ,
    double ForwardYawDeg,
    double HalfAngleDeg,
    double RangeCm)
{
    /// <summary>A flat, top-down polygon approximating this cone (origin + an arc of
    /// <paramref name="arcSegments"/> points), suitable for drawing on a 2D minimap. Z is not
    /// modeled — pitch is reported alongside the source movement sample for callers that want a
    /// 3D frustum instead.</summary>
    public IReadOnlyList<(double X, double Y)> ToTopDownPolygon(int arcSegments = 12)
    {
        var points = new List<(double X, double Y)>(arcSegments + 2) { (OriginX, OriginY) };

        double startDeg = ForwardYawDeg - HalfAngleDeg;
        double stepDeg = (2 * HalfAngleDeg) / arcSegments;
        for (int i = 0; i <= arcSegments; i++)
        {
            double angleRad = DegToRad(startDeg + (stepDeg * i));
            points.Add((OriginX + (RangeCm * Math.Cos(angleRad)), OriginY + (RangeCm * Math.Sin(angleRad))));
        }

        return points;
    }

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;
}
