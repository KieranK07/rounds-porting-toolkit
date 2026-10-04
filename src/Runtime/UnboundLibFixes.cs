using System.Reflection;
using HarmonyLib;

namespace RoundsPort.Runtime
{
    // Bknibb's UnboundLib 4 colours a health bar pink while its player has respawns left: a postfix on HealthBar.Update
    // reading data.stats. Health bars on things that aren't players (Cards+ snakes carry a CharacterData without
    // stats) threw it every frame; UnboundLib 3 had no such patch. Skip it where there are no stats to read.
    [HarmonyPatch]
    internal static class UL_HealthBarRespawns_Fix
    {
        static MethodBase Target() => AccessTools.Method(AccessTools.TypeByName("UnboundLib.Patches.HealthBar_Patch_Update"), "Postfix");
        static bool Prepare() => Target() != null;
        static MethodBase TargetMethod() => Target();

        // the target's parameters are (HealthBar __instance, CharacterData ___data): read by position
        static bool Prefix(object[] __args) => __args.Length > 1 && __args[1] is CharacterData data && data != null && data.stats != null;
    }
}
