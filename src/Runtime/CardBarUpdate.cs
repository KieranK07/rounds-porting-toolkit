using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RoundsPort.Runtime
{
    // The current game's CardBar has no Update: nothing on it runs every frame. AutoFix disables Harmony patches on
    // CardBar.Update (HarmonyX throws on a missing target, which stops the mod's PatchAll) and renames them
    // __RoundsCompat_Disabled_CardBar_Update_<name>. This calls them every frame for each active CardBar, as Update did,
    // with __instance and ___field arguments bound the way Harmony binds them. LocalZoom keeps the hovered card's zoom
    // and position in step with the camera this way.
    internal class CardBarUpdateRunner : MonoBehaviour
    {
        internal static BepInEx.Logging.ManualLogSource Log;
        const string Disabled = "__RoundsCompat_Disabled_CardBar_Update_";

        sealed class Patch
        {
            public MethodInfo Method;
            public object[] Args;
            public FieldInfo[] Fields;   // per parameter: the CardBar field a ___name parameter reads, else null
            public bool Told;
        }

        readonly List<Patch> patches = new List<Patch>();
        readonly HashSet<Assembly> scanned = new HashSet<Assembly>();
        bool newAssemblies = true;
        CardBar[] bars = new CardBar[0];
        float nextFind;

        void OnEnable() => AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        void OnDisable() => AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e) => newAssemblies = true;

        void Update()
        {
            if (newAssemblies) { newAssemblies = false; Scan(); }
            if (patches.Count == 0) return;
            if (Time.unscaledTime >= nextFind) { nextFind = Time.unscaledTime + 1f; bars = FindObjectsOfType<CardBar>(); }
            foreach (var bar in bars)
            {
                if (bar == null || !bar.isActiveAndEnabled) continue;
                foreach (var p in patches) Run(p, bar);
            }
        }

        void Scan()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!scanned.Add(asm) || asm.IsDynamic) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch (Exception) { continue; }
                foreach (var t in types)
                    foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!m.Name.StartsWith(Disabled)) continue;
                        var ps = m.GetParameters();
                        var p = new Patch { Method = m, Args = new object[ps.Length], Fields = new FieldInfo[ps.Length] };
                        for (int i = 0; i < ps.Length; i++)
                            if (ps[i].Name.StartsWith("___"))
                            {
                                // fields that became private gained an m_ prefix (currentCard -> m_currentCard)
                                var name = ps[i].Name.Substring(3);
                                p.Fields[i] = AccessTools.DeclaredField(typeof(CardBar), name) ?? AccessTools.DeclaredField(typeof(CardBar), "m_" + name);
                            }
                        patches.Add(p);
                        Log?.LogInfo($"CardBar.Update has no counterpart now: running {t.FullName}.{m.Name} every frame for each card bar");
                    }
            }
        }

        static void Run(Patch p, CardBar bar)
        {
            var ps = p.Method.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].Name == "__instance") p.Args[i] = bar;
                else if (p.Fields[i] != null) p.Args[i] = p.Fields[i].GetValue(bar);
                else
                {
                    var type = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
                    p.Args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
                }
            }
            try { p.Method.Invoke(null, p.Args); }
            catch (Exception e)
            {
                if (!p.Told) { p.Told = true; Log?.LogWarning($"{p.Method.DeclaringType.FullName}.{p.Method.Name}: {e.GetBaseException()}"); }
                return;
            }
            for (int i = 0; i < ps.Length; i++)
                if (p.Fields[i] != null && ps[i].ParameterType.IsByRef) p.Fields[i].SetValue(bar, p.Args[i]);
        }
    }
}
