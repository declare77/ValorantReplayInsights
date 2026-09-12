namespace VrfInsights.Analysis.Common;

/// <summary>A plain X/Y/Z point or direction, shared by anything that needs one (shot-effect
/// attack vectors, damage impact locations/directions). A named record rather than a
/// <c>ValueTuple</c> so it round-trips through <c>System.Text.Json</c> correctly —
/// <c>System.Text.Json</c> only serializes public properties by default, and a
/// <c>ValueTuple</c>'s <c>Item1</c>/<c>Item2</c>/<c>Item3</c> are fields, so a list of tuples
/// would silently serialize as a list of empty objects.</summary>
public sealed record Vec3(double X, double Y, double Z);
