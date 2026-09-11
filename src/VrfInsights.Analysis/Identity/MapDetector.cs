using VrfInsights.Data;

namespace VrfInsights.Analysis.Identity;

/// <summary>
/// Best-effort detection of which map a replay was played on. VALORANT's replay doesn't carry an
/// explicit "map name" field this project has confirmed, but the persistent level's own Unreal
/// asset path (something like <c>/Game/Maps/Duality/Duality.Duality:PersistentLevel</c>) shows up
/// incidentally wherever vrfkit records object paths — <c>net_guids.parquet</c> is the most
/// likely place (it's a path-per-NetGUID table), with actor class/archetype paths in
/// <c>actors.parquet</c> as a fallback. This is a heuristic substring search, not a confirmed
/// "here's the map" field — if it comes back null, the map genuinely couldn't be identified from
/// this export and callers should let the user pick it manually (e.g. from
/// <see cref="MapCatalog.All"/>) rather than guessing.
/// </summary>
public static class MapDetector
{
    public static MapInfo? Detect(VrfExportSet export, MapCatalog catalog)
    {
        foreach (var row in export.NetGuids)
        {
            MapInfo? hit = catalog.ResolveByAssetPath(row.Path);
            if (hit is not null)
            {
                return hit;
            }
        }

        foreach (var row in export.Actors)
        {
            MapInfo? hit = catalog.ResolveByAssetPath(row.ClassPath) ?? catalog.ResolveByAssetPath(row.ArchetypePath);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }
}
