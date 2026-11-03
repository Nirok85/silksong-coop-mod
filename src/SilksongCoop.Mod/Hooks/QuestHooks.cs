using HarmonyLib;

namespace SilksongCoop.Mod.Hooks
{
    /// <summary>
    /// Captures quest-related PlayerData changes around NPC dialogues. Hooked at
    /// <see cref="NPCControlBase"/> so quest boards and interaction events are covered as
    /// well as plain conversations. Pure visual dialogues produce an empty diff and send
    /// nothing.
    /// </summary>
    [HarmonyPatch]
    public static class QuestHooks
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NPCControlBase), "StartDialogue")]
        public static void PrefixStartDialogue()
        {
            SilksongCoopPlugin.Instance?.QuestStateSync?.BeginDialogue();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NPCControlBase), "EndDialogue")]
        public static void PostfixEndDialogue()
        {
            SilksongCoopPlugin.Instance?.QuestStateSync?.EndDialogue();
        }

        // Fallback: some interactions close the box directly without going through
        // NPCControlBase.EndDialogue. EndDialogue() is idempotent at depth 0, so whichever
        // callback fires first schedules the diff and the second becomes a no-op.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DialogueBox), "EndConversation")]
        public static void PostfixEndConversation()
        {
            SilksongCoopPlugin.Instance?.QuestStateSync?.EndDialogue();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DialogueBox), "OnLeavingScene")]
        public static void PostfixDialogueLeavingScene()
        {
            SilksongCoopPlugin.Instance?.QuestStateSync?.CancelDialogue();
        }
    }
}
