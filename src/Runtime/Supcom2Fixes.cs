using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RoundsPort.Runtime
{
    // Supcom2 Cards colours Darkenoid's and Wilfindja's lasers with the player it finds by GetComponentInParent<Player>()
    // on the player's own object, from OnAddCard. Cards are picked by the round's loser, who is dead (inactive) during the
    // pick, and on the 2025 game (Unity 2022) that lookup finds nothing on an inactive object: the pick threw and the
    // pick phase never finished. Not proven against the old build (the mod shipped two releases with Darkenoid fixes after
    // adding the colours, so it worked for its author). Only acts where the original lookup comes back empty and would
    // throw: look again including inactive objects.
    [HarmonyPatch]
    internal static class Supcom2TeamColor_Fix
    {
        static MethodInfo byPlayer;

        static MethodBase Target()
        {
            var methods = Types.Find("Supcom2Cards.ExtensionMethods")?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "SetTeamColor" && m.GetParameters().Length == 3).ToList();
            if (methods == null) return null;
            byPlayer = methods.FirstOrDefault(m => m.GetParameters()[1].ParameterType == typeof(Player));
            return byPlayer == null ? null : methods.FirstOrDefault(m => m.GetParameters()[1].ParameterType == typeof(GameObject));
        }
        static bool Prepare() => Target() != null;
        static MethodBase TargetMethod() => Target();

        // (List<Laser> lasers, GameObject gameObjectMono, float brightness_mult)
        static bool Prefix(object[] __args)
        {
            var go = __args[1] as GameObject;
            if (go == null || go.GetComponentInParent<Player>() != null) return true;
            var player = go.GetComponentInParent<Player>(true);
            if (player == null) return true;
            byPlayer.Invoke(null, new[] { __args[0], player, __args[2] });
            return false;
        }
    }
}
