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

    /// <summary>
    /// The final path segment of a <c>class_path</c> like
    /// <c>/Game/Characters/Wraith/S0/Ability_4/Zone_Wraith_4_Smoke.Zone_Wraith_4_Smoke_C</c> is
    /// <c>Zone_Wraith_4_Smoke</c> (everything after the last <c>/</c>, before the first <c>.</c>)
    /// — the actual class name, as opposed to its folder path. Exposed for anything (like
    /// <see cref="UtilityTimelineBuilder"/>) that needs to reason about *which specific actor*
    /// within an ability's class family a row is, not just its keyword category.
    /// </summary>
    public static string? ClassNameSegment(string? classPath)
    {
        if (string.IsNullOrEmpty(classPath)) return null;
        int slash = classPath.LastIndexOf('/');
        string afterSlash = slash >= 0 ? classPath[(slash + 1)..] : classPath;
        int dot = afterSlash.IndexOf('.');
        return dot >= 0 ? afterSlash[..dot] : afterSlash;
    }

    /// <summary>
    /// True for the actor whose class name itself starts with <c>Ability_</c> (e.g.
    /// <c>Ability_Wraith_4_Smoke</c>, as opposed to <c>Projectile_Wraith_4_Smoke</c> or
    /// <c>Zone_Wraith_4_Smoke</c>).
    ///
    /// <para><b>Confirmed via <c>dump-actors</c> against a real export</b> (both Omen's smoke,
    /// class <c>Ability_Wraith_4_Smoke</c>, and Jett's, <c>Ability_Wushu_4_Smoke</c>): this actor
    /// opens once per player near the very start of the game (<c>t≈72ms</c>) at that player's
    /// spawn position, and never reopens — it's the per-player ability-slot container that exists
    /// for the whole match, not a per-cast placement. That also matches the user's exact bug
    /// report ("some of them just end up in spawn"): this is the actor whose recorded position
    /// would always be near spawn, for every single cast. <see cref="UtilityTimelineBuilder"/>
    /// excludes these entirely rather than trying to interpret their position as a placement.</para>
    /// </summary>
    public static bool IsAbilityContainerActor(string? classPath) =>
        ClassNameSegment(classPath)?.StartsWith("Ability_", StringComparison.Ordinal) == true;

    /// <summary>
    /// Groups actors belonging to the same specific ability (same agent, same ability slot) so
    /// sibling actor types for one ability — e.g. <c>Projectile_Wraith_4_Smoke</c> and
    /// <c>Zone_Wraith_4_Smoke</c>, both under <c>.../Wraith/S0/Ability_4/...</c> — can be told
    /// apart from an unrelated ability that happens to fall in the same <see cref="UtilityCategory"/>.
    /// Derived from the <c>/Game/Characters/&lt;Agent&gt;/.../Ability_&lt;Slot&gt;/...</c> folder
    /// structure, which vrfkit's own class paths use consistently across every agent seen so far
    /// (confirmed via <c>dump-classes</c>) — falls back to just the agent folder if no
    /// <c>Ability_*</c> folder segment is present (e.g. a passive).
    /// </summary>
    public static string? ExtractAbilityGroupKey(string? classPath)
    {
        if (string.IsNullOrEmpty(classPath)) return null;

        string[] segments = classPath.Split('/');
        // segments[0] is "" (leading slash), [1]="Game", [2]="Characters", [3]=Agent.
        string? agent = segments.Length > 3 && segments[2] == "Characters" ? segments[3] : null;
        if (agent is null) return null;

        // Only look at *folder* segments (everything but the last, which is the
        // "ClassName.ClassName_C" file component) -- the class name itself can start with
        // "Ability_" too (e.g. a passive's own class), which isn't the same thing as being
        // inside an "Ability_<slot>" folder and shouldn't be treated as one.
        string? abilityFolder = segments[..^1].FirstOrDefault(s => s.StartsWith("Ability_", StringComparison.Ordinal));
        return abilityFolder is not null ? $"{agent}/{abilityFolder}" : agent;
    }
}
