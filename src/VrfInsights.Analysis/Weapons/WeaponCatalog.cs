using System.Reflection;
using System.Text.Json;

namespace VrfInsights.Analysis.Weapons;

/// <summary>Real weapon name + category for one recognized internal codename. See <c>weapons.json</c>
/// for where this table comes from and how confident to be in it.</summary>
public sealed record WeaponInfo(string Codename, string DisplayName, string Category);

/// <summary>
/// Resolves an actor's <c>class_path</c>/<c>archetype_path</c> (from <c>actors.parquet</c>) to a
/// real VALORANT weapon name, by substring-matching a known internal codename — the same technique
/// <see cref="Utility.UtilityEffectClassifier"/> already uses for ability class paths, not an exact
/// full-path match (vrfkit's exact path formatting could vary slightly by build; the codename
/// itself, e.g. <c>AssaultRifle_AK</c>, is the stable part).
///
/// <para>The table (<c>weapons.json</c>) is vrfkit's OWN mapping — its <c>tools/equippable_table.py</c>,
/// generated from a companion C# replay parser's hand-maintained resolver — not something guessed
/// or reverse-engineered here, and cross-checked against valorant-api.com's own <c>assetPath</c>
/// field for several weapons during research. It has NOT been independently confirmed against a
/// real decoded export by this project the way most of the rest of this codebase's tables have.</para>
/// </summary>
public sealed class WeaponCatalog
{
    private readonly IReadOnlyList<WeaponInfo> _entries;

    private WeaponCatalog(IReadOnlyList<WeaponInfo> entries)
    {
        _entries = entries;
    }

    public static WeaponCatalog LoadEmbedded()
    {
        Assembly asm = typeof(WeaponCatalog).Assembly;
        const string resourceName = "VrfInsights.Analysis.Weapons.weapons.json";
        using Stream? stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Should only happen if the csproj's EmbeddedResource entry or this resource name
            // ever drift apart -- fail soft with an empty catalog rather than crashing the CLI.
            return new WeaponCatalog(Array.Empty<WeaponInfo>());
        }

        using JsonDocument doc = JsonDocument.Parse(stream);
        var entries = new List<WeaponInfo>();
        if (doc.RootElement.TryGetProperty("weapons", out JsonElement weapons) && weapons.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement w in weapons.EnumerateArray())
            {
                string? codename = w.TryGetProperty("codename", out JsonElement c) ? c.GetString() : null;
                string? displayName = w.TryGetProperty("displayName", out JsonElement d) ? d.GetString() : null;
                string? category = w.TryGetProperty("category", out JsonElement cat) ? cat.GetString() : null;
                if (codename is not null && displayName is not null)
                {
                    entries.Add(new WeaponInfo(codename, displayName, category ?? ""));
                }
            }
        }

        // Order matters: this project's own JSON lists more-specific codenames (e.g.
        // "Gun_Deadeye_X_Giantslayer") before shorter, more-general ones that would otherwise
        // match first ("Gun_Deadeye") -- Resolve() below returns the FIRST substring match, in
        // list order, so preserve that order rather than re-sorting it here.
        return new WeaponCatalog(entries);
    }

    /// <summary>Returns the recognized weapon whose codename appears anywhere in
    /// <paramref name="classPath"/>, or null if nothing matches (an unrecognized/new weapon, or a
    /// non-weapon actor).</summary>
    public WeaponInfo? Resolve(string? classPath)
    {
        if (string.IsNullOrEmpty(classPath))
        {
            return null;
        }

        foreach (WeaponInfo entry in _entries)
        {
            if (classPath.Contains(entry.Codename, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}
