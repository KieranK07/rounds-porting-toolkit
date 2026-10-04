using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RoundsPort.Runtime
{
    // MapsExtended keeps its map-object manager on a "Root Map Object Manager" GameObject of its own, and on the current
    // build that object gets destroyed (as BepInEx's manager does without HideManagerGameObject). The host never
    // notices. A client starts one coroutine on the manager per networked map object (boxes, ropes, balls, saws): on the
    // destroyed component that throws, which aborts the whole map load, so clients get the map without its physics
    // objects. The coroutine runs on our helper object instead. Not Mac-specific.
    [HarmonyPatch]
    internal static class MapsExtClientSync_Fix
    {
        internal static BepInEx.Logging.ManualLogSource Log;
        internal static MonoBehaviour Host;   // a component on the plugin's hidden helper object
        static bool told;

        static System.Type Manager => Types.Find("MapsExt.NetworkedMapObjectManager");
        static bool Prepare() => Manager != null && TargetMethod() != null;
        // Instantiate(MapObjectData data, Transform parent, Action<GameObject> onInstantiate): there are other overloads
        internal static MethodBase TargetMethod() => AccessTools.DeclaredMethod(Manager, "Instantiate",
            new[] { Types.Find("MapsExt.MapObjects.MapObjectData"), typeof(Transform), typeof(System.Action<GameObject>) });

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var start = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), new[] { typeof(IEnumerator) });
            var ours = AccessTools.Method(typeof(MapsExtClientSync_Fix), nameof(StartCoroutine));
            foreach (var ins in instructions)
            {
                if (ins.Calls(start)) yield return new CodeInstruction(OpCodes.Call, ours) { labels = ins.labels, blocks = ins.blocks };
                else yield return ins;
            }
        }

        internal static bool Active(string harmonyId)
        {
            var t = Manager == null ? null : TargetMethod();
            return t != null && Harmony.GetPatchInfo(t)?.Owners.Contains(harmonyId) == true;
        }

        public static Coroutine StartCoroutine(MonoBehaviour self, IEnumerator routine)
        {
            if (self != null) return self.StartCoroutine(routine);
            if (!told) { told = true; Log?.LogInfo("MapsExtended's map object manager was destroyed; its client sync runs on rounds-port's helper object"); }
            return Host.StartCoroutine(routine);
        }
    }

    internal class CoroutineHost : MonoBehaviour { }
}
