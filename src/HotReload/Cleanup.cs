using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoundsHotReload
{
    // Undoes everything an unloaded mod left behind. Every step is independent: one failing doesn't stop the rest.
    static class Cleanup
    {
        public sealed class Report
        {
            public readonly SortedDictionary<string, int> Counts = new SortedDictionary<string, int>();
            public void Add(string what, int n) { if (n > 0) Counts[what] = (Counts.TryGetValue(what, out var c) ? c : 0) + n; }
            public override string ToString() => Counts.Count == 0 ? "nothing to clean up" : string.Join(", ", Counts.Select(kv => $"{kv.Value} {kv.Key}"));
        }

        public static void Unload(HotMod m, Report r)
        {
            var a = m.Assembly;
            // Unity objects destroyed here, to purge from registries. By reference: Unity resets a destroyed object's id.
            var destroyed = new HashSet<object>(ReferenceEqualityComparer.Instance);

            Step("plugins", () =>
            {
                foreach (var p in m.Plugins) if (p != null && Traverse.Create(p).Property("Logger").GetValue() is BepInEx.Logging.ILogSource log) BepInEx.Logging.Logger.Sources.Remove(log);
                foreach (var g in m.Guids)
                    if (Chainloader.PluginInfos.TryGetValue(g, out var info) && (info.Instance == null || info.Instance.GetType().Assembly == a))
                        Chainloader.PluginInfos.Remove(g);
                if (m.Host != null) Object.DestroyImmediate(m.Host);   // runs the plugins' OnDisable/OnDestroy now
                r.Add("plugins", m.Plugins.Count);
            });
            Step("libraries", () => Libraries.Unload(m, destroyed, r));
            Step("Harmony", () => r.Add("Harmony patches", UnpatchHarmony(a)));
            Step("MonoMod hooks", () => r.Add("MonoMod hooks", RemoveHookGen(a)));
            Step("detours", () => r.Add("detours", Trackers.DisposeDetours(a)));
            Step("components", () => r.Add("components", DestroyComponents(a, destroyed)));
            Step("handlers", () => new Scrubber(a, destroyed, r).Run());
            Step("bundles", () => r.Add("asset bundles", Trackers.UnloadBundles(a)));
        }

        static void Step(string what, Action act)
        {
            try { act(); }
            catch (Exception e) { HotReloadPlugin.Log.LogWarning($"cleanup ({what}) failed: {e}"); }
        }

        static int UnpatchHarmony(Assembly a)
        {
            int n = 0;
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(original);
                if (info == null) continue;
                var all = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators);
                foreach (var patch in all.ToList())
                    if (patch.PatchMethod?.DeclaringType?.Assembly == a)
                    {
                        try { new Harmony(patch.owner).Unpatch(original, patch.PatchMethod); n++; }
                        catch (Exception e) { HotReloadPlugin.Log.LogWarning($"couldn't unpatch {original.FullDescription()}: {e.Message}"); }
                    }
            }
            return n;
        }

        // MMHOOK's On.X.Y += handlers: MonoMod files them under the handler's assembly.
        static int RemoveHookGen(Assembly a)
        {
            var mgr = AccessTools.TypeByName("MonoMod.RuntimeDetour.HookGen.HookEndpointManager");
            if (mgr == null) return 0;
            var owned = AccessTools.Field(mgr, "OwnedHookLists")?.GetValue(null) as IDictionary;
            int n = owned != null && owned.Contains(a) && owned[a] is ICollection c ? c.Count : 0;
            AccessTools.Method(mgr, "RemoveAllOwnedBy").Invoke(null, new object[] { a });
            return n;
        }

        // Components whose type comes from the old assembly, on scene and DontDestroyOnLoad objects (not prefabs
        // inside asset bundles). Their GameObjects stay: they may belong to the game (a player, a card bar).
        static int DestroyComponents(Assembly a, HashSet<object> destroyed)
        {
            int n = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (c == null || c.GetType().Assembly != a || !c.gameObject.scene.IsValid()) continue;
                destroyed.Add(c);
                try { Object.DestroyImmediate(c); n++; } catch { }
            }
            return n;
        }
    }

    // Removes the old assembly from shared state: event handlers and callback lists (static fields, and fields of
    // singletons they point to), and registry entries that are its objects, hold only its callbacks, or point at
    // Unity objects destroyed during this unload.
    sealed class Scrubber
    {
        readonly Assembly dead;
        readonly HashSet<object> destroyed;
        readonly Cleanup.Report report;
        readonly HashSet<object> seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public Scrubber(Assembly dead, HashSet<object> destroyed, Cleanup.Report report) { this.dead = dead; this.destroyed = destroyed; this.report = report; }

        public void Run()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!Scanned(asm)) continue;
                foreach (var t in Types(asm))
                {
                    if (t.IsGenericTypeDefinition || t.IsInterface) continue;
                    FieldInfo[] fields;
                    try { fields = t.GetFields(Stat); } catch { continue; }
                    foreach (var f in fields)
                    {
                        if (f.IsLiteral || !Interesting(f.FieldType, singletons: true)) continue;
                        object v;
                        try { v = f.GetValue(null); } catch { continue; }
                        Visit(null, f, v, depth: 0);
                    }
                }
            }
        }

        // The game, Unity's core module and every non-hot mod or library in BepInEx/plugins.
        bool Scanned(Assembly asm)
        {
            if (asm.IsDynamic || HotLoader.IsHot(asm)) return false;
            var n = asm.GetName().Name;
            if (n == "Assembly-CSharp" || n == "Assembly-CSharp-firstpass" || n == "UnityEngine.CoreModule") return true;
            string loc;
            try { loc = asm.Location; } catch { return false; }
            return !string.IsNullOrEmpty(loc) && loc.Replace('\\', '/').Contains("/BepInEx/plugins/") && !n.StartsWith("MMHOOK");
        }

        static IEnumerable<Type> Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
            catch { return Enumerable.Empty<Type>(); }
        }

        // Handlers and collections; with singletons, also objects from the game or a mod that a static field holds
        // (`CardChoice.instance`, `GameModeManager` state...), whose own fields get one look.
        bool Interesting(Type t, bool singletons) =>
            typeof(Delegate).IsAssignableFrom(t) || typeof(IEnumerable).IsAssignableFrom(t) && t != typeof(string)
            || singletons && t.IsClass && !t.IsArray && t != typeof(string) && Scanned(t.Assembly)
               && (!typeof(Object).IsAssignableFrom(t) || typeof(MonoBehaviour).IsAssignableFrom(t));

        void Visit(object owner, FieldInfo f, object v, int depth)
        {
            if (v == null) return;
            switch (v)
            {
                case Delegate d:
                    var s = Strip(d);
                    if (s != d) { Set(owner, f, s); report.Add("event handlers", Count(d) - Count(s)); }
                    return;
                case Array arr:
                    var keep = arr.Cast<object>().Where(x => !Dead(x)).ToArray();
                    if (keep.Length == arr.Length || f == null || !f.FieldType.IsArray) return;
                    var na = Array.CreateInstance(arr.GetType().GetElementType(), keep.Length);
                    Array.Copy(keep, na, keep.Length);
                    Set(owner, f, na);
                    report.Add("registry entries", arr.Length - keep.Length);
                    return;
                case IDictionary dict:
                    if (!seen.Add(dict)) return;
                    foreach (var k in dict.Keys.Cast<object>().ToList())
                    {
                        var val = dict[k];
                        if (Dead(k) || Dead(val)) { dict.Remove(k); report.Add("registry entries", 1); continue; }
                        if (val is Delegate vd) { var vs = Strip(vd); if (vs != vd) { if (vs == null) dict.Remove(k); else dict[k] = vs; report.Add("event handlers", Count(vd) - Count(vs)); } }
                        else if (val is IList || val is IDictionary) Visit(null, null, val, depth + 1);
                    }
                    return;
                case IList list:
                    if (!seen.Add(list) || list.IsFixedSize || list.IsReadOnly) return;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var x = list[i];
                        if (x is Delegate ld) { var ls = Strip(ld); if (ls == ld) continue; if (ls == null) list.RemoveAt(i); else list[i] = ls; report.Add("event handlers", Count(ld) - Count(ls)); }
                        else if (Dead(x)) { list.RemoveAt(i); report.Add("registry entries", 1); }
                        else if (x is IList || x is IDictionary) Visit(null, null, x, depth + 1);
                    }
                    return;
                case IEnumerable set when v.GetType().IsGenericType && v.GetType().GetMethod("Remove", new[] { v.GetType().GetGenericArguments()[0] }) is MethodInfo remove
                                          && v.GetType().GetGenericArguments().Length == 1:
                    if (!seen.Add(set)) return;
                    foreach (var x in set.Cast<object>().Where(Dead).ToList()) { remove.Invoke(set, new[] { x }); report.Add("registry entries", 1); }
                    return;
            }
            // A singleton (static `instance` field, manager object): look at its own handler and list fields once.
            if (depth > 0 || !seen.Add(v)) return;
            if (v is Object uo && uo == null) return;
            foreach (var inner in Fields(v.GetType()))
            {
                if (!Interesting(inner.FieldType, singletons: false)) continue;
                object iv;
                try { iv = inner.GetValue(v); } catch { continue; }
                Visit(v, inner, iv, depth + 1);
            }
        }

        static FieldInfo[] Fields(Type t)
        {
            if (fieldCache.TryGetValue(t, out var fs)) return fs;
            var list = new List<FieldInfo>();
            for (var c = t; c != null && c != typeof(object) && c != typeof(MonoBehaviour); c = c.BaseType)
                try { list.AddRange(c.GetFields(Inst | BindingFlags.DeclaredOnly)); } catch { }
            return fieldCache[t] = list.ToArray();
        }

        void Set(object owner, FieldInfo f, object value)
        {
            if (f == null) return;
            try { f.SetValue(owner, value); } catch (Exception e) { HotReloadPlugin.Log.LogDebug($"can't update {f.DeclaringType?.Name}.{f.Name}: {e.Message}"); }
        }

        bool FromDead(Delegate d) => d.Method?.DeclaringType?.Assembly == dead || d.Target != null && d.Target.GetType().Assembly == dead;

        Delegate Strip(Delegate d)
        {
            if (d == null) return null;
            var parts = d.GetInvocationList();
            if (!parts.Any(FromDead)) return d;
            return Delegate.Combine(parts.Where(p => !FromDead(p)).ToArray());
        }

        static int Count(Delegate d) => d == null ? 0 : d.GetInvocationList().Length;

        // An entry to drop: the old assembly's object, a callback from it, a Unity object destroyed during this
        // unload, or a small record (e.g. a menu or hook registration) whose callbacks all come from it.
        bool Dead(object x)
        {
            switch (x)
            {
                case null: return false;
                case Delegate d: return d.GetInvocationList().All(FromDead);
                case Object u: return destroyed.Contains(u) || u.GetType().Assembly == dead;
            }
            var t = x.GetType();
            if (t.Assembly == dead) return true;
            if (t.IsPrimitive || t.IsEnum || x is string || x is IEnumerable) return false;
            bool any = false;
            foreach (var f in Fields(t))
            {
                object v;
                try { v = f.GetValue(x); } catch { continue; }
                if (v is Delegate d) { if (d.GetInvocationList().All(FromDead)) any = true; else return false; }
                else if (v is Object u && !ReferenceEquals(u, null) && destroyed.Contains(u)) any = true;
            }
            return any;
        }
    }

    sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }
}
