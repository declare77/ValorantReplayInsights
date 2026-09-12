using VrfInsights.Analysis.Common;

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
        ("Cloud", UtilityCategory.Smoke),  // must stay ahead of "Poison" below -- Viper's Poison
                                            // Cloud (Q) should classify as Smoke, not Wall, and its
                                            // class name almost certainly contains both words.
        ("Molly", UtilityCategory.IncendiaryOrMolly),
        ("Incendiary", UtilityCategory.IncendiaryOrMolly),
        ("Fire", UtilityCategory.IncendiaryOrMolly),
        ("Wall", UtilityCategory.Wall),
        ("Barrier", UtilityCategory.Wall),
        // "Toxic"/"Poison" -- a defensive guess at Viper's Toxic Screen (E), added because it
        // wasn't matching "Wall"/"Barrier" at all (see UtilityTimelineBuilder: an unclassified
        // actor is dropped before it ever reaches utility.json, which is almost certainly why it
        // wasn't drawing). Viper has never actually been seen in a real export by this project
        // (see docs/AGENT_ABILITIES.md), so her ability class names, unlike the 8 confirmed
        // agents', are a total unknown -- "Toxic"/"Poison" are a bet that Riot's own internal name
        // uses her ability's real English name the way "Molly"/"Incendiary"/"Flash" above already
        // do for other agents, not something confirmed via dump-classes. If this still doesn't
        // show up, run `dump-classes ./export | grep -i pandemic` (her dev codename) against a
        // real export with Viper in it and add whatever her actual class name uses here instead.
        ("Toxic", UtilityCategory.Wall),
        ("Poison", UtilityCategory.Wall),
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

        string maskedPath = MaskAgentCodenames(classPath);

        foreach ((string keyword, UtilityCategory category) in Keywords)
        {
            if (maskedPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return UtilityCategory.Unclassified;
    }

    // Longest codename first, so e.g. a shorter codename that happens to be a substring of a
    // longer one (none currently, but cheap insurance) can't mask over part of the longer one
    // first and leave a stray fragment behind.
    private static readonly string[] CodenamesByDescendingLength = AgentCodenames.CodenameToRealName.Keys
        .OrderByDescending(c => c.Length)
        .ToArray();

    /// <summary>
    /// Strips every known agent codename (<see cref="AgentCodenames.CodenameToRealName"/>) out of
    /// a class path before keyword matching.
    ///
    /// <para><b>Confirmed necessary against a real export:</b> Gekko's codename <c>AggroBot</c>
    /// itself contains the <c>"Bot"</c> keyword, so without this mask every single one of Gekko's
    /// actors -- including <c>AggroBot_PC</c>, the player controller itself, which opens once near
    /// match start and stays open for the whole match -- gets misclassified as utility
    /// <c>DroneOrDeployable</c>. That produces exactly the reported symptom: a permanent "utility"
    /// marker sitting at Gekko's spawn point for the entire game, never actually deployed. Checked
    /// against every codename in <see cref="AgentCodenames.CodenameToRealName"/>: `AggroBot`/`Bot`
    /// is (so far) the only such collision, but this masks all of them defensively rather than
    /// special-casing just that one, so a future agent whose codename happens to embed a keyword
    /// doesn't reintroduce the same bug silently.</para>
    /// </summary>
    private static string MaskAgentCodenames(string classPath)
    {
        string masked = classPath;
        foreach (string codename in CodenamesByDescendingLength)
        {
            if (masked.Contains(codename, StringComparison.OrdinalIgnoreCase))
            {
                masked = masked.Replace(codename, string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }
        return masked;
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
    /// True for a weapon/gun model actor (e.g. <c>Gun_Deadeye_Q_Pistol</c>,
    /// <c>Gun_Deadeye_X_Giantslayer_Prototype_FIreRatePrototype</c>) -- never a utility placement,
    /// but confirmed (via a real export) to sometimes accidentally match a keyword anyway: Chamber's
    /// ultimate gun class name contains "FIreRatePrototype" (a fire-*rate* stat, nothing to do with
    /// incendiary utility), which the plain substring match in <see cref="Classify"/> would
    /// otherwise read as "Fire" -&gt; <see cref="UtilityCategory.IncendiaryOrMolly"/>.
    /// </summary>
    public static bool IsWeaponModelActor(string? classPath) =>
        ClassNameSegment(classPath)?.StartsWith("Gun_", StringComparison.Ordinal) == true;

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

    private static readonly HashSet<string> ActorTypePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ability", "Projectile", "Zone", "GameObject", "Patch", "Pawn",
    };

    private static readonly HashSet<string> KnownCodenames = new(
        AgentCodenames.CodenameToRealName.Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pulls the human-readable "what this actually is" fragment out of a class name, for
    /// fuzzy-matching against real ability names/descriptions (e.g. from valorant-api.com) --
    /// not for categorization (see <see cref="Classify"/> for that). Strips the leading
    /// actor-type token (<c>Ability_</c>, <c>Projectile_</c>, <c>Zone_</c>, <c>GameObject_</c>,
    /// <c>Patch_</c>, <c>Pawn_</c>), every known agent codename (<see cref="AgentCodenames"/>),
    /// and any leftover token that's purely numeric or a single character -- VALORANT's internal
    /// class names use exactly those as ability-slot markers (e.g. the <c>4</c>/<c>Q</c>/<c>E</c>/
    /// <c>X</c>/<c>C</c> seen in real names like <c>Ability_Wraith_4_Smoke</c> or
    /// <c>Ability_Grenadier_C_Flash</c>), and no real English ability word is one character long,
    /// so this is a safe general filter rather than an enumerated slot-letter list.
    ///
    /// <para>Verified against every real class path this project has actual <c>dump-classes</c>
    /// evidence for (see <see cref="UtilityEffectClassifierTests"/>): e.g. <c>Zone_Wraith_4_Smoke</c>
    /// → <c>"Smoke"</c>, <c>Ability_Q_Aggrobot_SeekerNade</c> → <c>"SeekerNade"</c>,
    /// <c>GameObject_Phoenix_Q_FlameWallManager_Production</c> → <c>"FlameWallManager Production"</c>.
    /// This is a hint for fuzzy-matching against real ability text, not a guaranteed-correct
    /// ability identifier by itself -- the caller (the viewer, matching against valorant-api.com
    /// data) decides how confident a match needs to be before trusting it.</para>
    /// </summary>
    public static string? ExtractDescriptiveKeyword(string? classPath)
    {
        string? className = ClassNameSegment(classPath);
        if (string.IsNullOrEmpty(className)) return null;

        string[] tokens = className.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (i == 0 && ActorTypePrefixes.Contains(token)) continue;
            if (token.Length <= 1) continue;
            if (token.All(char.IsDigit)) continue;
            if (KnownCodenames.Contains(token)) continue;
            kept.Add(token);
        }
        return kept.Count > 0 ? string.Join(' ', kept) : null;
    }
}
