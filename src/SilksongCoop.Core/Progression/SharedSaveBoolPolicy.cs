using System;
using System.Collections.Generic;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.Progression
{
    /// <summary>
    /// Authoritative allow-list for PlayerData save booleans that may cross the network.
    /// The Mod hook delegates to this policy so engine-agnostic tests exercise the same
    /// production decision rather than a copied fallback specification.
    /// </summary>
    public static class SharedSaveBoolPolicy
    {
        public static readonly string[] AbilityBools =
        {
            "hasDash", "hasWalljump", "hasDoubleJump", "hasHarpoonDash",
            "hasSilkSpecial", "hasNeedleThrow", "hasThreadSphere", "hasParry",
            "hasSilkCharge", "hasSilkBomb", "hasSilkBossNeedle", "hasNeedolin",
            "hasNeedolinMemoryPowerup", "hasBrolly", "hasChargeSlash", "hasSuperJump",
            "hasQuill"
        };

        public static readonly string[] KeyItemBools =
        {
            "simpleKey", "bellTowerKey", "cryptKey", "abbeyKey",
            "boneBottomKey", "citadelCrestKey"
        };

        private static readonly HashSet<string> AbilitySet =
            new HashSet<string>(AbilityBools, StringComparer.Ordinal);
        private static readonly HashSet<string> KeyItemSet =
            new HashSet<string>(KeyItemBools, StringComparer.Ordinal);

        public static bool IsAllowed(string itemId, ItemCategory category)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return false;
            if (category == ItemCategory.Ability) return AbilitySet.Contains(itemId);
            if (category == ItemCategory.KeyItem) return KeyItemSet.Contains(itemId);
            return false;
        }
    }
}
