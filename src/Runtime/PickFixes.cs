using HarmonyLib;

namespace RoundsPort.Runtime
{
    // The 2025 game clears CardChoice.pickrID the moment a player picks (DoPlayerSelect); the old game kept it until the
    // next pick, so ReplaceCards, which deals the next hand after the pick animation, ran with it set. Mods that replace
    // ReplaceCards (Pick Phase Improvements) or deal more than one pick (Pick N Cards, through the game's own
    // SpawnUniqueCard) read the picker from it, throw on -1 and leave the game stuck in the pick phase. Put it back for
    // ReplaceCards. The hand is already cleared by then, so nothing can be picked twice.
    [HarmonyPatch(typeof(CardChoice))]
    internal static class PickerId_Fix
    {
        static int lastPicker = -1;

        [HarmonyPrefix, HarmonyPatch("RPCA_DoEndPick")]
        static void DoEndPick(int pickId)
        {
            if (pickId != -1) lastPicker = pickId;
        }

        [HarmonyPrefix, HarmonyPatch("ReplaceCards"), HarmonyPriority(Priority.First)]
        static void ReplaceCards(CardChoice __instance)
        {
            if (__instance.pickrID == -1 && lastPicker != -1) __instance.pickrID = lastPicker;
            lastPicker = -1;
        }
    }
}
