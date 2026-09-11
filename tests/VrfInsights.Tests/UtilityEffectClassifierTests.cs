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
}
