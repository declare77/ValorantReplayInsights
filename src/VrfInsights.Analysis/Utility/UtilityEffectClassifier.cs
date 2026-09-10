namespace VrfInsights.Analysis.Utility;

public enum UtilityCategory
{
    Unclassified,
    Smoke,
    IncendiaryOrMolly,
    Wall,
    TrapOrMine,
    Turret,
    ProjectileOrGrenade,
    DroneOrDeployable,
}

/// <summary>
/// Best-effort classification of an actor's <c>class_path</c> (from <c>actors.parquet</c>) into
/// a utility category, by matching substrings that VALORANT's own Blueprint asset names tend to
/// use (e.g. vrfkit's docs note real observed names like
/// <c>FXC_Wushu_4_SmokeNearsight_C</c>). This is plain string matching against human-readable
/// names the replay already carries — nothing here is reverse-engineered.
///
/// <para><b>This is not an exhaustive per-agent catalogue.</b> Across ~25 agents there are far
/// more unique ability class names than are listed here; anything that doesn't match a keyword
/// comes back as <see cref="UtilityCategory.Unclassified"/> rather than being guessed at, and
/// <see cref="PersistentEffectEvent.ClassPath"/> always preserves the original string so you can
/// extend <see cref="Keywords"/> once you've looked at your own export's actual class names
/// (e.g. via <c>vrf-insights dump-classes</c>).</para>
/// </summary>
public static class UtilityEffectClassifier
{
    // Longest/most-specific keywords should be checked first so e.g. "Wall" doesn't shadow a
    // more specific "Firewall"-style name. Extend freely — this is intentionally a flat,
    // editable list rather than a generated table.
    public static readonly (string Keyword, UtilityCategory Category)[] Keywords =
    {
        ("Smoke", UtilityCategory.Smoke),
        ("Cloud", UtilityCategory.Smoke),
        ("Molly", UtilityCategory.IncendiaryOrMolly),
        ("Incendiary", UtilityCategory.IncendiaryOrMolly),
        ("Fire", UtilityCategory.IncendiaryOrMolly),
        ("Wall", UtilityCategory.Wall),
        ("Barrier", UtilityCategory.Wall),
        ("Trap", UtilityCategory.TrapOrMine),
        ("Mine", UtilityCategory.TrapOrMine),
        ("Cage", UtilityCategory.TrapOrMine),
        ("Turret", UtilityCategory.Turret),
        ("Sentry", UtilityCategory.Turret),
        ("Drone", UtilityCategory.DroneOrDeployable),
        ("Bot", UtilityCategory.DroneOrDeployable),
        ("Nanoswarm", UtilityCategory.DroneOrDeployable),
        ("Dart", UtilityCategory.ProjectileOrGrenade),
        ("Grenade", UtilityCategory.ProjectileOrGrenade),
        ("Flash", UtilityCategory.ProjectileOrGrenade),
        ("Orb", UtilityCategory.ProjectileOrGrenade),
        ("Missile", UtilityCategory.ProjectileOrGrenade),
    };

    public static UtilityCategory Classify(string? classPath)
    {
        if (string.IsNullOrEmpty(classPath))
        {
            return UtilityCategory.Unclassified;
        }

        foreach ((string keyword, UtilityCategory category) in Keywords)
        {
            if (classPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return UtilityCategory.Unclassified;
    }
}
