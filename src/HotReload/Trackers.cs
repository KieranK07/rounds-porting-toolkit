using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RoundsHotReload
{
    // Records what each hot mod creates that has to be released when it unloads but that nothing else tracks:
    // asset bundles (a second copy of a loaded bundle can't be opened) and MonoMod Hook/ILHook/Detour objects.
    static class Trackers
    {
        internal static Assembly Loading;   // set while a hot mod's plugins are being added
        static readonly Dictionary<Assembly, List<AssetBundle>> bundles = new Dictionary<Assembly, List<AssetBundle>>();
        static readonly Dictionary<Assembly, List<IDisposable>> detours = new Dictionary<Assembly, List<IDisposable>>();

        public static void Install(Harmony h)
        {
            var bundlePost = new HarmonyMethod(typeof(Trackers), nameof(BundleLoaded));
            foreach (var m in typeof(AssetBundle).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name.StartsWith("LoadFrom") && !m.Name.EndsWith("Async") && m.ReturnType == typeof(AssetBundle) && m.GetMethodBody() != null)
                    Try(() => h.Patch(m, postfix: bundlePost));

            var detourPost = new HarmonyMethod(typeof(Trackers), nameof(DetourApplied));
            foreach (var name in new[] { "MonoMod.RuntimeDetour.Hook", "MonoMod.RuntimeDetour.ILHook", "MonoMod.RuntimeDetour.Detour" })
            {
                var apply = AccessTools.TypeByName(name) is Type t ? AccessTools.Method(t, "Apply", Type.EmptyTypes) : null;
                if (apply != null) Try(() => h.Patch(apply, postfix: detourPost));
            }
            Libraries.Install(h);
        }

        static void Try(Action a)
        {
            try { a(); } catch (Exception e) { HotReloadPlugin.Log.LogWarning("tracker not installed: " + e.Message); }
        }

        // The hot mod whose code is running: the one being loaded, else the nearest hot frame on the stack.
        // Returns null when Harmony or MonoMod's HookGen is on the stack: those own their detours and manage them.
        internal static Assembly Owner(bool skipManaged)
        {
            Assembly found = null;
            foreach (var f in new StackTrace(2, false).GetFrames() ?? new StackFrame[0])
            {
                var t = f.GetMethod()?.DeclaringType;
                if (t == null) continue;
                if (skipManaged && (t.Namespace?.StartsWith("HarmonyLib") == true || t.Namespace == "MonoMod.RuntimeDetour.HookGen")) return null;
                if (found == null && HotLoader.IsLive(t.Assembly)) found = t.Assembly;
            }
            return found ?? Loading;
        }

        static void BundleLoaded(AssetBundle __result)
        {
            if (__result == null) return;
            var owner = Owner(false);
            if (owner == null) return;
            if (!bundles.TryGetValue(owner, out var list)) bundles[owner] = list = new List<AssetBundle>();
            list.Add(__result);
        }

        static void DetourApplied(object __instance)
        {
            if (!(__instance is IDisposable d)) return;
            var owner = Owner(true);
            if (owner == null) return;
            if (!detours.TryGetValue(owner, out var list)) detours[owner] = list = new List<IDisposable>();
            if (!list.Contains(d)) list.Add(d);
        }

        public static int UnloadBundles(Assembly a)
        {
            if (!bundles.TryGetValue(a, out var list)) return 0;
            bundles.Remove(a);
            int n = 0;
            // Unload(false): objects already made from the bundle stay valid; the new copy can open it again.
            foreach (var b in list) if (b != null) { b.Unload(false); n++; }
            return n;
        }

        public static int DisposeDetours(Assembly a)
        {
            if (!detours.TryGetValue(a, out var list)) return 0;
            detours.Remove(a);
            int n = 0;
            foreach (var d in list) { try { d.Dispose(); n++; } catch { } }
            return n;
        }
    }
}
