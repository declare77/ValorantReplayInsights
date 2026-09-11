using System.Reflection;
using System.Text.Json;

namespace VrfInsights.Analysis.Identity;

/// <param name="Codename">Internal asset-path folder name (e.g. <c>Duality</c> for Bind) — what
/// actually appears in replay data, per Riot's own <c>mapUrl</c> field. Not shown to players.</param>
/// <param name="DisplayName">The name players know the map by (e.g. <c>Bind</c>).</param>
/// <param name="Uuid">valorant-api.com's map id — also usable to name a locally cached minimap
/// image file (see <c>scripts/Fetch-Assets.ps1</c>).</param>
/// <param name="XMultiplier">Riot's published fields for converting in-game X/Y (Unreal units)
/// to normalized [0,1] minimap coordinates: <c>u = gameX * XMultiplier + XScalarToAdd</c>,
/// <c>v = gameY * YMultiplier + YScalarToAdd</c>, then <c>pixel = (u * imageWidth, v *
/// imageHeight)</c>. This is Riot's own field naming/values, used here exactly as published —
/// not reverse-engineered — but the resulting pixel mapping hasn't been independently verified
/// here against a real replay overlaid on a real minimap image, so treat it as "the documented
/// formula," not "confirmed pixel-perfect."</param>
public sealed record MapInfo(
    string Codename,
    string DisplayName,
    string Uuid,
    double XMultiplier,
    double YMultiplier,
    double XScalarToAdd,
    double YScalarToAdd);

/// <summary>
/// Resolves which VALORANT map a replay was played on and how to project its coordinates onto a
/// minimap image. The map list is Riot's own public content data
/// (<see href="https://valorant-api.com/v1/maps"/>), not anything reverse-engineered from a
/// replay — see <c>Identity/maps.json</c>.
/// </summary>
public sealed class MapCatalog
{
    private readonly IReadOnlyList<MapInfo> _maps;

    private MapCatalog(IReadOnlyList<MapInfo> maps)
    {
        _maps = maps;
    }

    public IReadOnlyList<MapInfo> All => _maps;

    public static MapCatalog LoadEmbedded()
    {
        Assembly asm = typeof(MapCatalog).Assembly;
        const string resourceName = "VrfInsights.Analysis.Identity.maps.json";
        using Stream? stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return new MapCatalog(Array.Empty<MapInfo>());
        }

        using JsonDocument doc = JsonDocument.Parse(stream);
        var maps = new List<MapInfo>();
        if (doc.RootElement.TryGetProperty("maps", out JsonElement mapsElement) && mapsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in mapsElement.EnumerateArray())
            {
                maps.Add(new MapInfo(
                    Codename: entry.GetProperty("codename").GetString() ?? "",
                    DisplayName: entry.GetProperty("displayName").GetString() ?? "",
                    Uuid: entry.GetProperty("uuid").GetString() ?? "",
                    XMultiplier: entry.GetProperty("xMultiplier").GetDouble(),
                    YMultiplier: entry.GetProperty("yMultiplier").GetDouble(),
                    XScalarToAdd: entry.GetProperty("xScalarToAdd").GetDouble(),
                    YScalarToAdd: entry.GetProperty("yScalarToAdd").GetDouble()));
            }
        }

        return new MapCatalog(maps);
    }

    /// <summary>Finds the map whose <see cref="MapInfo.Codename"/> appears as an asset-path
    /// segment in <paramref name="assetPath"/> — e.g. a path containing
    /// <c>/Game/Maps/Duality/Duality.Duality</c> resolves to Bind. Case-insensitive; returns
    /// null if nothing matches.</summary>
    public MapInfo? ResolveByAssetPath(string? assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
        {
            return null;
        }

        foreach (MapInfo map in _maps)
        {
            string needle = $"/Maps/{map.Codename}/";
            if (assetPath.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return map;
            }
        }

        return null;
    }

    public MapInfo? ResolveByDisplayName(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName))
        {
            return null;
        }

        foreach (MapInfo map in _maps)
        {
            if (string.Equals(map.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
            {
                return map;
            }
        }

        return null;
    }
}
