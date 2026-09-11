using VrfInsights.Analysis.Utility;
using Xunit;

namespace VrfInsights.Tests;

// Class paths below are the exact strings from the user's real `dump-classes`/`dump-actors`
// output (Omen's smoke = codename Wraith, Jett's = codename Wushu, KAY/O's = codename Grenadier).
public class UtilityEffectClassifierTests
{
    [Theory]
    [InlineData("/Game/Characters/Wraith/S0/Ability_4/Zone_Wraith_4_Smoke.Zone_Wraith_4_Smoke_C", "Zone_Wraith_4_Smoke")]
    [InlineData("/Game/Characters/Wushu/S0/Ability_4/GameObject_Wushu_4_SmokeZone.GameObject_Wushu_4_SmokeZone_C", "GameObject_Wushu_4_SmokeZone")]
    [InlineData("FXC_Test_Smoke_C", "FXC_Test_Smoke_C")] // no folder/dot at all -- returns the whole string
    public void ClassNameSegment_TakesTheClassNameAfterTheLastSlashAndBeforeTheFirstDot(string classPath, string expected)
    {
        Assert.Equal(expected, UtilityEffectClassifier.ClassNameSegment(classPath));
    }

    [Theory]
    [InlineData("/Game/Characters/Wraith/S0/Ability_4/Ability_Wraith_4_Smoke.Ability_Wraith_4_Smoke_C", true)]
    [InlineData("/Game/Characters/Wushu/S0/Ability_4/Ability_Wushu_4_Smoke.Ability_Wushu_4_Smoke_C", true)]
    [InlineData("/Game/Characters/Wraith/S0/Ability_4/Projectile_Wraith_4_Smoke.Projectile_Wraith_4_Smoke_C", false)]
    [InlineData("/Game/Characters/Wraith/S0/Ability_4/Zone_Wraith_4_Smoke.Zone_Wraith_4_Smoke_C", false)]
    [InlineData(null, false)]
    public void IsAbilityContainerActor_MatchesOnlyTheAbilitySlotContainerItself(string? classPath, bool expected)
    {
        Assert.Equal(expected, UtilityEffectClassifier.IsAbilityContainerActor(classPath));
    }

    [Fact]
    public void ExtractAbilityGroupKey_GroupsSiblingActorTypesOfTheSameAbility()
    {
        string? projectileGroup = UtilityEffectClassifier.ExtractAbilityGroupKey(
            "/Game/Characters/Wraith/S0/Ability_4/Projectile_Wraith_4_Smoke.Projectile_Wraith_4_Smoke_C");
        string? zoneGroup = UtilityEffectClassifier.ExtractAbilityGroupKey(
            "/Game/Characters/Wraith/S0/Ability_4/Zone_Wraith_4_Smoke.Zone_Wraith_4_Smoke_C");

        Assert.Equal(projectileGroup, zoneGroup);
        Assert.Equal("Wraith/Ability_4", projectileGroup);
    }

    [Fact]
    public void ExtractAbilityGroupKey_DoesNotConfuseTwoDifferentAgentsOrSlots()
    {
        string? wraithSmoke = UtilityEffectClassifier.ExtractAbilityGroupKey(
            "/Game/Characters/Wraith/S0/Ability_4/Zone_Wraith_4_Smoke.Zone_Wraith_4_Smoke_C");
        string? wushuSmoke = UtilityEffectClassifier.ExtractAbilityGroupKey(
            "/Game/Characters/Wushu/S0/Ability_4/GameObject_Wushu_4_SmokeZone.GameObject_Wushu_4_SmokeZone_C");
        string? wraithTeleport = UtilityEffectClassifier.ExtractAbilityGroupKey(
            "/Game/Characters/Wraith/S0/Ability_E/Ability_Wraith_E_ShortTeleport.Ability_Wraith_E_ShortTeleport_C");

        Assert.NotEqual(wraithSmoke, wushuSmoke);
        Assert.NotEqual(wraithSmoke, wraithTeleport);
    }

    [Fact]
    public void ExtractAbilityGroupKey_FallsBackToAgentOnly_WhenNoAbilityFolderIsPresent()
    {
        string? key = UtilityEffectClassifier.ExtractAbilityGroupKey(
            "/Game/Characters/Phoenix/S0/Passive_Fire/Ability_Phoenix_Passive.Ability_Phoenix_Passive_C");

        // No "Ability_<slot>" *folder* segment here (the folder is "Passive_Fire") -- only the
        // agent is available to group by.
        Assert.Equal("Phoenix", key);
    }

    [Fact]
    public void ExtractAbilityGroupKey_ReturnsNull_ForPathsWithNoCharactersFolder()
    {
        Assert.Null(UtilityEffectClassifier.ExtractAbilityGroupKey("FXC_Test_Smoke_C"));
        Assert.Null(UtilityEffectClassifier.ExtractAbilityGroupKey(null));
    }

    // Classify()'s agent-codename masking -- confirmed necessary against the user's real
    // dump-classes output: Gekko's codename "AggroBot" itself contains the "Bot" keyword, so
    // every one of Gekko's actors (including the player controller, AggroBot_PC, which opens once
    // near match start and stays open for the whole match) used to be misclassified as
    // "DroneOrDeployable" -- a permanent marker sitting at Gekko's spawn point all game. See
    // MaskAgentCodenames's doc comment.

    [Theory]
    [InlineData("/Game/Characters/AggroBot/AggroBot_PC.AggroBot_PC_C")] // the player controller itself
    [InlineData("/Game/Characters/AggroBot/S0/Ability_Q/Pawn_Aggrobot_SeekerNade.Pawn_Aggrobot_SeekerNade_C")]
    [InlineData("/Game/Characters/AggroBot/S0/Ability_X/Pawn_Aggrobot_RollyPolly.Pawn_Aggrobot_RollyPolly_C")]
    [InlineData("/Game/Characters/AggroBot/S0/Ability_E/Projectile_Aggrobot_Zamboni_Rocket.Projectile_Aggrobot_Zamboni_Rocket_C")]
    public void Classify_DoesNotMatchTheBotKeywordJustBecauseTheAgentCodenameIsAggroBot(string classPath)
    {
        Assert.Equal(UtilityCategory.Unclassified, UtilityEffectClassifier.Classify(classPath));
    }

    [Fact]
    public void Classify_StillMatchesALegitimateKeywordOnAnAggroBotActor()
    {
        // "DiscTurret" is Gekko's Dizzy -- a real, intentional "Turret" match that has nothing to
        // do with the AggroBot/Bot false positive above; masking "AggroBot" out of the path must
        // not touch the unrelated "Turret" substring that follows it.
        UtilityCategory category = UtilityEffectClassifier.Classify(
            "/Game/Characters/AggroBot/S0/Ability_E/Ability_E_Aggrobot_DiscTurret.Ability_E_Aggrobot_DiscTurret_C");

        Assert.Equal(UtilityCategory.Turret, category);
    }

    [Fact]
    public void Classify_StillMatchesFireInAGenuineFireAbility_OnAnUnrelatedAgent()
    {
        // Regression guard: masking codenames must not accidentally eat legitimate matches on
        // agents whose codename isn't involved at all (Phoenix's molotov fire patch).
        UtilityCategory category = UtilityEffectClassifier.Classify(
            "/Game/Characters/Phoenix/S0/Ability_4/Production/NewMolotov/Patch_Phoenix_MolotovFire.Patch_Phoenix_MolotovFire_C");

        Assert.Equal(UtilityCategory.IncendiaryOrMolly, category);
    }

    [Theory]
    [InlineData("/Game/Characters/Deadeye/S0/Ability_Q/Gun/Gun_Deadeye_Q_Pistol.Gun_Deadeye_Q_Pistol_C", true)]
    [InlineData("/Game/Characters/Deadeye/S0/Ability_X/Gun_Giantslayer/Gun_Deadeye_X_Giantslayer_Prototype_FIreRatePrototype.Gun_Deadeye_X_Giantslayer_Prototype_FireRatePrototype_C", true)]
    [InlineData("/Game/Characters/Wraith/S0/Ability_4/Zone_Wraith_4_Smoke.Zone_Wraith_4_Smoke_C", false)]
    [InlineData(null, false)]
    public void IsWeaponModelActor_MatchesOnlyGunPrefixedClasses(string? classPath, bool expected)
    {
        Assert.Equal(expected, UtilityEffectClassifier.IsWeaponModelActor(classPath));
    }

    [Fact]
    public void Classify_WouldOtherwiseMisreadChambersUltimateGunAsIncendiary_WithoutTheGunExclusion()
    {
        // This class isn't excluded by Classify() itself (that's IsWeaponModelActor's job, applied
        // by UtilityTimelineBuilder) -- documenting *why* the Gun_ exclusion exists: Chamber's
        // ultimate gun class name contains "FIreRatePrototype" (a fire-*rate* stat), which plain
        // substring matching reads as "Fire".
        UtilityCategory category = UtilityEffectClassifier.Classify(
            "/Game/Characters/Deadeye/S0/Ability_X/Gun_Giantslayer/Gun_Deadeye_X_Giantslayer_Prototype_FIreRatePrototype.Gun_Deadeye_X_Giantslayer_Prototype_FireRatePrototype_C");

        Assert.Equal(UtilityCategory.IncendiaryOrMolly, category);
    }
}
