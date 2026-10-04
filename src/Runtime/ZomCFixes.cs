using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RoundsPort.Runtime
{
    // ZomC's Perseverance (Pristine) adds a ModdingUtils HealthBasedEffect to the picker in OnAddCard and at once uses the
    // stats that the effect's Awake fills in. The picker is the round's loser, dead (inactive) during the pick, so Awake
    // hasn't run, the stats are null, and the pick threw: the pick phase never finished. Same cause as Supcom2's Darkenoid
    // (Supcom2Fixes.cs). Right after the AddComponent, fill in the stats if they're still empty; the card's own code then
    // runs as written. Awake fills in the same object when the player comes back.
    [HarmonyPatch]
    internal static class ZomCPerseverance_Fix
    {
        static MethodBase Target() => AccessTools.Method(Types.Find("ZomC_Cards.Cards.PerseverancePristine"), "OnAddCard");
        static bool Prepare() => Target() != null;
        static MethodBase TargetMethod() => Target();

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            foreach (var ins in code)
            {
                yield return ins;
                if (ins.opcode == OpCodes.Callvirt && ins.operand is MethodInfo m && m.Name == "AddComponent" && m.IsGenericMethod)
                {
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ZomCPerseverance_Fix), nameof(Prime)));
                }
            }
        }

        public static void Prime(Component effect)
        {
            if (effect == null || effect.gameObject.activeInHierarchy) return;
            var f = AccessTools.Field(effect.GetType(), "characterStatModifiers");
            if (f != null && f.GetValue(effect) == null) f.SetValue(effect, effect.GetComponent<CharacterStatModifiers>());
        }
    }
}
