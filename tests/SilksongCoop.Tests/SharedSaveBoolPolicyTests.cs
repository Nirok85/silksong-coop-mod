using System;
using System.Collections.Generic;
using System.Linq;
using SilksongCoop.Core.Progression;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Tests.E2ERemediationTests;
using Xunit;

namespace SilksongCoop.Tests;

public sealed class SharedSaveBoolPolicyTests
{
    public static IEnumerable<object[]> AllowedBools()
    {
        foreach (string id in new[]
        {
            "hasDash", "hasWalljump", "hasDoubleJump", "hasHarpoonDash", "hasSilkSpecial",
            "hasNeedleThrow", "hasThreadSphere", "hasParry", "hasSilkCharge", "hasSilkBomb",
            "hasSilkBossNeedle", "hasNeedolin", "hasNeedolinMemoryPowerup", "hasBrolly",
            "hasChargeSlash", "hasSuperJump", "hasQuill"
        }) yield return new object[] { id, ItemCategory.Ability };
        foreach (string id in new[]
        {
            "simpleKey", "bellTowerKey", "cryptKey", "abbeyKey", "boneBottomKey", "citadelCrestKey"
        }) yield return new object[] { id, ItemCategory.KeyItem };
    }

    [Theory]
    [MemberData(nameof(AllowedBools))]
    public void EveryExplicitAllowedBoolRequiresExactCategoryAndOrdinalSpelling(string id, ItemCategory category)
    {
        for (int raw = byte.MinValue; raw <= byte.MaxValue; raw++)
        {
            var candidate = (ItemCategory)raw;
            Assert.Equal(candidate == category, SharedSaveBoolPolicy.IsAllowed(id, candidate));
            Assert.Equal(candidate == category, RemediationTestHarness.IsSaveBoolAllowed(id, candidate));
        }
        foreach (string invalid in new[] { id.ToUpperInvariant(), id.ToLowerInvariant(), " " + id, id + " ", id + "\0", id + "\n" })
        {
            Assert.False(SharedSaveBoolPolicy.IsAllowed(invalid, category), invalid);
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(invalid, category), invalid);
        }
    }

    [Fact]
    public void ProductionListsMatchExplicitContractWithoutExtrasOrDuplicates()
    {
        Assert.Equal(AllowedBools().Where(x => (ItemCategory)x[1] == ItemCategory.Ability).Select(x => (string)x[0]), SharedSaveBoolPolicy.AbilityBools);
        Assert.Equal(AllowedBools().Where(x => (ItemCategory)x[1] == ItemCategory.KeyItem).Select(x => (string)x[0]), SharedSaveBoolPolicy.KeyItemBools);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("hasDash\0hasWalljump")]
    [InlineData("unknownSaveBool")]
    [InlineData("isInvincible")]
    [InlineData("killedBoss")]
    [InlineData("unlockedAllCrests")]
    [InlineData("hasDashExtra")]
    public void InvalidNamesAreDeniedForEveryCategory(string? id)
    {
        for (int raw = byte.MinValue; raw <= byte.MaxValue; raw++)
        {
            Assert.False(SharedSaveBoolPolicy.IsAllowed(id!, (ItemCategory)raw));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(id!, (ItemCategory)raw));
        }
    }
}
