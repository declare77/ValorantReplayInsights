namespace VrfInsights.Analysis.Common;

/// <summary>
/// VALORANT's own asset paths (<c>class_path</c> in <c>actors.parquet</c>, and the folder names
/// under <c>/Game/Characters/...</c> generally) use Riot's internal development codenames for
/// agents, not their public release names — e.g. Reyna's folder is <c>/Game/Characters/Vampire/...</c>,
/// Omen's is <c>/Game/Characters/Wraith/...</c>. Those codenames can't be changed since they're
/// literally Riot's asset structure baked into the replay, but this project (and anyone talking
/// about it) should use real agent names everywhere else.
///
/// <para>This table is reference data the user supplied directly (they looked it up), not
/// something derived from this replay or from vrfkit. Two entries (Killjoy, Breach) keep their
/// release name as their codename too — Riot didn't rename those internally.</para>
///
/// <para><b>Only 8 of these agents are actually present in the sample export this project has
/// been developed/tested against</b> (confirmed via <c>vrf-insights dump-classes</c>): Gekko
/// (<c>AggroBot</c>), Chamber (<c>Deadeye</c>), KAY/O (<c>Grenadier</c>), Phoenix (<c>Phoenix</c>),
/// Waylay (<c>Terra</c>), Reyna (<c>Vampire</c>), Omen (<c>Wraith</c>), Jett (<c>Wushu</c>). The
/// other 22 are here for completeness/future exports, not because they've been seen in real data.</para>
/// </summary>
public static class AgentCodenames
{
    /// <summary>Codename (as it appears in class paths) → real/release agent name.</summary>
    public static readonly IReadOnlyDictionary<string, string> CodenameToRealName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Sarge"] = "Brimstone",
        ["Pandemic"] = "Viper",
        ["Wraith"] = "Omen",
        ["Killjoy"] = "Killjoy", // codename == release name
        ["Gumshoe"] = "Cypher",
        ["Hunter"] = "Sova",
        ["Thorne"] = "Sage",
        ["Phoenix"] = "Phoenix", // codename == release name
        ["Wushu"] = "Jett",
        ["Vampire"] = "Reyna",
        ["Clay"] = "Raze",
        ["Breach"] = "Breach", // codename == release name
        ["Guide"] = "Skye",
        ["Stealth"] = "Yoru",
        ["Rift"] = "Astra",
        ["Grenadier"] = "KAY/O",
        ["Deadeye"] = "Chamber",
        ["Sprinter"] = "Neon",
        ["BountyHunter"] = "Fade",
        ["Mage"] = "Harbor",
        ["AggroBot"] = "Gekko",
        ["Cable"] = "Deadlock",
        ["Sequoia"] = "Iso",
        ["Smonk"] = "Clove",
        ["Nox"] = "Vyse",
        ["Cashew"] = "Tejo",
        ["Terra"] = "Waylay",
        ["Pine"] = "Veto",
        ["Iris"] = "Miks",
    };

    /// <summary>
    /// Looks for a known codename as a path segment inside a <c>class_path</c>-shaped string
    /// (e.g. <c>/Game/Characters/Wraith/S0/Ability_4/Ability_Wraith_4_Smoke.Ability_Wraith_4_Smoke_C</c>)
    /// and returns the matching real agent name, or null if none of the known codenames appear.
    /// This is a convenience for logging/debugging output — nothing in the pipeline depends on it.
    /// </summary>
    public static string? ResolveRealName(string? classPath)
    {
        if (string.IsNullOrEmpty(classPath))
        {
            return null;
        }

        foreach (KeyValuePair<string, string> entry in CodenameToRealName)
        {
            // Match as a whole path segment (surrounded by '/' or '_') rather than a bare
            // substring, so e.g. "Rift" doesn't accidentally match something unrelated.
            if (classPath.Contains('/' + entry.Key + '/', StringComparison.OrdinalIgnoreCase) ||
                classPath.Contains('_' + entry.Key + '_', StringComparison.OrdinalIgnoreCase) ||
                classPath.StartsWith(entry.Key + '_', StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }
}
