using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Mod.Hooks;
using SilksongCoop.Mod.State;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
foreach (var id in new[] { "hasDash", "hasWalljump", "hasDoubleJump", "hasHarpoonDash", "hasSilkSpecial", "hasNeedleThrow", "hasThreadSphere", "hasParry", "hasSilkCharge", "hasSilkBomb", "hasSilkBossNeedle", "hasNeedolin", "hasNeedolinMemoryPowerup", "hasBrolly", "hasChargeSlash", "hasSuperJump", "hasQuill" }) Check(InventoryHooks.IsAllowedSaveBool(id, ItemCategory.Ability), id);
foreach (var id in new[] { "simpleKey", "bellTowerKey", "cryptKey", "abbeyKey", "boneBottomKey", "citadelCrestKey" }) Check(InventoryHooks.IsAllowedSaveBool(id, ItemCategory.KeyItem), id);
foreach (var id in new[] { "hasDash", "simpleKey" }) foreach (var category in new[] { ItemCategory.Crest, ItemCategory.Tool }) Check(!InventoryHooks.IsAllowedSaveBool(id, category), $"wrong category {id}");
Check(!InventoryHooks.IsAllowedSaveBool(null!, ItemCategory.Ability), "null");
Check(!InventoryHooks.IsAllowedSaveBool("HASDASH", ItemCategory.Ability), "case");
Check(InventoryHooks.IsSharedAbilityBool("hasDash"), "ability member");
Check(!InventoryHooks.IsSharedAbilityBool("unknown"), "unknown ability");
Check(InventoryHooks.IsSharedAbilityBool("hasQuill"), "allowlisted PlayerData member is shared");
Check(!InventoryHooks.IsSharedAbilityBool(null!), "null ability");
Check(!InventoryHooks.IsSharedAbilityBool("HASDASH"), "ability case");
using (RemoteApplyScope.Begin()) Check(RemoteApplyScope.IsActive, "remote scope");
Check(!RemoteApplyScope.IsActive, "remote scope disposed");
var sink = new SilksongCoop.Mod.Inventory();
var state = new SilksongCoop.Mod.InstanceState { SessionState = new() { IsConnected = true }, InventorySync = sink };
SilksongCoop.Mod.SilksongCoopPlugin.Instance = state;
void NoForward(Action invoke, string reason)
{
    int before = sink.Items.Count;
    invoke();
    Check(sink.Items.Count == before, reason);
}
InventoryHooks.PostfixSetBool("hasDash", true);
Check(sink.Items.Count == 1 && sink.Items[0] == ("hasDash", ItemCategory.Ability), "bool forwards ability");
NoForward(() => InventoryHooks.PostfixSetBool("hasDash", false), "false bool");
NoForward(() => InventoryHooks.PostfixSetBool("simpleKey", true), "key not ability");
NoForward(() => InventoryHooks.PostfixSetBool("HASDASH", true), "wrong case bool");
NoForward(() => InventoryHooks.PostfixSetBool(null!, true), "null bool");
InventoryHooks.PostfixToolItemUnlock(new() { name = "skill", Type = ToolItemType.Skill });
Check(sink.Items.Last() == ("skill", ItemCategory.Ability), "skill category");
InventoryHooks.PostfixToolItemUnlock(new() { name = "tool", Type = ToolItemType.Other });
Check(sink.Items.Last() == ("tool", ItemCategory.Tool), "tool category");
InventoryHooks.PostfixToolCrestUnlock(new() { name = "crest" });
Check(sink.Items.Last() == ("crest", ItemCategory.Crest), "crest category");
void AllHooks()
{
    InventoryHooks.PostfixSetBool("hasDash", true);
    InventoryHooks.PostfixToolItemUnlock(new());
    InventoryHooks.PostfixToolCrestUnlock(new());
}
state.SessionState!.IsConnected = false;
NoForward(AllHooks, "disconnected suppresses all hooks");
state.SessionState.IsConnected = true;
using (RemoteApplyScope.Begin()) NoForward(AllHooks, "remote apply suppresses all hooks");
state.InventorySync = null;
NoForward(AllHooks, "missing inventory suppresses all hooks");
state.InventorySync = sink;
state.SessionState = null;
NoForward(AllHooks, "missing session suppresses all hooks");
SilksongCoop.Mod.SilksongCoopPlugin.Instance = null;
NoForward(AllHooks, "missing plugin suppresses all hooks");
SilksongCoop.Mod.SilksongCoopPlugin.Instance = state;
state.SessionState = new() { IsConnected = true };
NoForward(() => { InventoryHooks.PostfixToolItemUnlock(null!); InventoryHooks.PostfixToolCrestUnlock(null!); }, "null instances");
NoForward(() => { InventoryHooks.PostfixToolItemUnlock(new() { name = "" }); InventoryHooks.PostfixToolCrestUnlock(new() { name = "" }); }, "empty names");
InventoryHooks.SharedAbilityBools.Add("injected");
InventoryHooks.AllowedKeyItemBools.Add("injected");
Check(!InventoryHooks.IsAllowedSaveBool("injected", ItemCategory.Ability), "compatibility field cannot expand ability authorization");
Check(!InventoryHooks.IsAllowedSaveBool("injected", ItemCategory.KeyItem), "compatibility field cannot expand key authorization");
Check(!InventoryHooks.IsSharedAbilityBool("injected"), "compatibility field cannot expand shared abilities");
InventoryHooks.SharedAbilityBools.Remove("injected");
InventoryHooks.AllowedKeyItemBools.Remove("injected");
Console.WriteLine($"Production InventoryHooks source checks passed: {passed}");

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)] public sealed class HarmonyPatch : Attribute { public HarmonyPatch() { } public HarmonyPatch(Type _, string __) { } }
    public sealed class HarmonyPostfix : Attribute { }
}
public enum ToolItemType { Skill, Other }
public sealed class ToolCrest { public string name = "crest"; public void Unlock() { } }
public sealed class ToolItem { public string name = "tool"; public ToolItemType Type; public void Unlock() { } }
public sealed class PlayerData
{
    public bool hasDash, hasWalljump, hasDoubleJump, hasHarpoonDash, hasSilkSpecial, hasNeedleThrow, hasThreadSphere, hasParry, hasSilkCharge, hasSilkBomb, hasSilkBossNeedle, hasNeedolin, hasNeedolinMemoryPowerup, hasBrolly, hasChargeSlash, hasSuperJump, hasQuill;
    public void SetBool(string _, bool __) { }
}
namespace SilksongCoop.Mod
{
    public sealed class Session { public bool IsConnected; }
    public sealed class Inventory
    {
        public List<(string, ItemCategory)> Items { get; } = new();
        public void OnLocalItemDiscovered(string id, ItemCategory category) => Items.Add((id, category));
    }
    public sealed class InstanceState { public Session? SessionState; public Inventory? InventorySync; }
    public static class SilksongCoopPlugin { public static InstanceState? Instance; public static void LogWarn(string _) { } }
}
