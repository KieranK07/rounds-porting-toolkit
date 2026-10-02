using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;

namespace RoundsHotReload
{
    sealed class HotMod
    {
        public string Path, Name;            // file, original assembly name
        public Assembly Assembly;
        public GameObject Host;
        public readonly List<BaseUnityPlugin> Plugins = new List<BaseUnityPlugin>();
        public readonly List<string> Guids = new List<string>();
        public HashSet<string> References;    // original names of the assemblies it references
        public bool Reloading;                // being replaced by a new copy (not removed)
    }

    // A DLL read and ready to load: its plugin types and what they depend on.
    sealed class Prepared
    {
        public string Path, Name;
        public AssemblyDefinition Cecil;
        public bool HasSymbols;
        public readonly List<TypeDefinition> PluginTypes = new List<TypeDefinition>();
        public readonly List<string> Guids = new List<string>();
        public readonly List<string> HardDependencies = new List<string>();
        public HashSet<string> References;
    }

    static class HotLoader
    {
        static readonly Dictionary<string, HotMod> loaded = new Dictionary<string, HotMod>(StringComparer.OrdinalIgnoreCase);
        static readonly List<HotMod> order = new List<HotMod>();                    // load order
        static readonly Dictionary<string, Assembly> latest = new Dictionary<string, Assembly>();
        static readonly HashSet<Assembly> ever = new HashSet<Assembly>();
        static readonly HashSet<Assembly> live = new HashSet<Assembly>();
        static readonly List<string> deferred = new List<string>();
        static int counter;

        public static bool IsHot(Assembly a) => a != null && ever.Contains(a);
        public static HotMod ByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return loaded.TryGetValue(System.IO.Path.GetFullPath(path), out var m) ? m : null; } catch { return null; }
        }
        public static string PathOf(Assembly a) => order.FirstOrDefault(m => m.Assembly == a)?.Path;
        public static bool IsLive(Assembly a) => a != null && live.Contains(a);
        public static IEnumerable<HotMod> Loaded => order;

        // Hot mods that reference each other by their original names get the newest copy.
        public static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            var n = new AssemblyName(e.Name).Name;
            return latest.TryGetValue(n, out var a) ? a : null;
        }

        // ---------------------------------------------------------------- entry points
        public static void LoadAll(IEnumerable<string> files, bool deferMissingDependencies, bool live = false)
        {
            var preps = files.Select(Prepare).Where(p => p != null).ToList();
            foreach (var p in Sort(preps))
            {
                var provided = new HashSet<string>(order.SelectMany(m => m.Guids));
                if (deferMissingDependencies && p.HardDependencies.Any(d => !Chainloader.PluginInfos.ContainsKey(d) && !provided.Contains(d)))
                {
                    deferred.Add(p.Path);
                    p.Cecil.Dispose();
                    continue;
                }
                Load(p, live);
            }
        }

        public static void LoadDeferred()
        {
            if (deferred.Count == 0) return;
            var files = deferred.ToList(); deferred.Clear();
            LoadAll(files, deferMissingDependencies: false);
        }

        public static void ReloadAll(IEnumerable<string> files)
        {
            var sw = Stopwatch.StartNew();
            var list = files.ToList();
            foreach (var m in order.AsEnumerable().Reverse().ToList()) { m.Reloading = list.Contains(m.Path, StringComparer.OrdinalIgnoreCase); Unload(m); }
            LoadAll(list, deferMissingDependencies: false, live: true);
            if (!order.Any(x => x.Plugins.Count > 0)) Libraries.FlushPendingCategories();
            HotReloadPlugin.Log.LogMessage($"Reloaded every mod in {HotReloadPlugin.Instance.Folder} ({sw.ElapsedMilliseconds} ms)");
        }

        // Reload the changed files, and any hot mod that depends on one of them (it holds references to the old copy).
        public static void ReloadChanged(List<string> paths)
        {
            var sw = Stopwatch.StartNew();
            var affected = new List<HotMod>();
            void Add(HotMod m)
            {
                if (affected.Contains(m)) return;
                affected.Add(m);
                foreach (var d in order.Where(x => x.References.Contains(m.Name))) Add(d);
            }
            foreach (var p in paths) if (loaded.TryGetValue(p, out var m)) Add(m);
            foreach (var m in affected.OrderByDescending(order.IndexOf)) { m.Reloading = File.Exists(m.Path); Unload(m); }

            var toLoad = paths.Concat(affected.Select(m => m.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
            LoadAll(toLoad, deferMissingDependencies: false, live: true);
            if (!order.Any(x => toLoad.Contains(x.Path, StringComparer.OrdinalIgnoreCase) && x.Plugins.Count > 0)) Libraries.FlushPendingCategories();
            var removed = affected.Where(m => !File.Exists(m.Path)).Select(m => System.IO.Path.GetFileName(m.Path)).ToList();
            HotReloadPlugin.Log.LogMessage("Hot reload: " + string.Join("; ", new[] {
                toLoad.Count > 0 ? "loaded " + string.Join(", ", toLoad.Select(System.IO.Path.GetFileName)) : null,
                removed.Count > 0 ? "removed " + string.Join(", ", removed) : null }.Where(x => x != null)) + $" ({sw.ElapsedMilliseconds} ms)");
        }

        // ---------------------------------------------------------------- load
        static Prepared Prepare(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var pdbPath = System.IO.Path.ChangeExtension(path, ".pdb");
                var resolver = new DefaultAssemblyResolver();
                foreach (var d in SearchDirs()) resolver.AddSearchDirectory(d);
                AssemblyDefinition def = null; bool symbols = false;
                if (File.Exists(pdbPath))
                {
                    try
                    {
                        def = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters
                        { AssemblyResolver = resolver, ReadSymbols = true, SymbolStream = new MemoryStream(File.ReadAllBytes(pdbPath)) });
                        symbols = true;
                    }
                    catch { def = null; }   // stale or foreign .pdb: load without line numbers
                }
                def ??= AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });

                var p = new Prepared { Path = System.IO.Path.GetFullPath(path), Name = def.Name.Name, Cecil = def, HasSymbols = symbols,
                    References = new HashSet<string>(def.MainModule.AssemblyReferences.Select(r => r.Name)) };
                foreach (var t in def.MainModule.Types)
                {
                    var attr = t.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "BepInEx.BepInPlugin");
                    if (attr == null || t.IsAbstract) continue;
                    p.PluginTypes.Add(t);
                    p.Guids.Add((string)attr.ConstructorArguments[0].Value);
                    foreach (var dep in t.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency"))
                    {
                        var flags = dep.ConstructorArguments.Count > 1 ? Convert.ToInt32(dep.ConstructorArguments[1].Value) : 1;
                        if ((flags & 1) != 0) p.HardDependencies.Add((string)dep.ConstructorArguments[0].Value);
                    }
                }
                return p;
            }
            catch (Exception e)
            {
                HotReloadPlugin.Log.LogError($"{System.IO.Path.GetFileName(path)}: can't read it: {e.Message}");
                return null;
            }
        }

        static IEnumerable<string> SearchDirs()
        {
            yield return HotReloadPlugin.Instance.Folder;
            yield return Paths.ManagedPath;
            yield return Paths.BepInExAssemblyDirectory;
            foreach (var d in Directory.GetDirectories(Paths.PluginPath, "*", SearchOption.AllDirectories)) yield return d;
            yield return Paths.PluginPath;
        }

        // Dependencies first (by plugin GUID and by assembly reference), then by name.
        static List<Prepared> Sort(List<Prepared> preps)
        {
            var result = new List<Prepared>();
            var visiting = new HashSet<Prepared>();
            void Visit(Prepared p)
            {
                if (result.Contains(p) || !visiting.Add(p)) return;
                foreach (var q in preps.Where(q => q != p && (p.HardDependencies.Intersect(q.Guids).Any() || p.References.Contains(q.Name))))
                    Visit(q);
                result.Add(p);
            }
            foreach (var p in preps.OrderBy(p => p.Name)) Visit(p);
            return result;
        }

        // The first copy of a mod keeps its real assembly name, so it behaves exactly like a normal plugin (asset
        // bundle scripts and network messages find its types by that name). Later copies get "-hot<n>" added,
        // because one process can't hold two assemblies with the same name apart.
        static void Load(Prepared p, bool isLive)
        {
            var sw = Stopwatch.StartNew();
            var file = System.IO.Path.GetFileName(p.Path);
            try
            {
                foreach (var g in p.Guids)
                    if (Chainloader.PluginInfos.TryGetValue(g, out var existing) && !loaded.Values.Any(m => m.Guids.Contains(g)))
                    {
                        HotReloadPlugin.Log.LogError($"{file}: {g} is already loaded from {existing.Location}. Remove one copy (BepInEx/plugins vs {HotReloadPlugin.Instance.Folder}) and restart.");
                        return;
                    }

                if (ever.Any(x => x.GetName().Name == p.Name || x.GetName().Name.StartsWith(p.Name + "-hot")) || AppDomain.CurrentDomain.GetAssemblies().Any(x => x.GetName().Name == p.Name))
                    p.Cecil.Name.Name = $"{p.Name}-hot{++counter}";
                var dll = new MemoryStream(); MemoryStream pdb = null;
                if (p.HasSymbols)
                {
                    try { pdb = new MemoryStream(); p.Cecil.Write(dll, new WriterParameters { WriteSymbols = true, SymbolStream = pdb }); }
                    catch { pdb = null; dll = new MemoryStream(); p.Cecil.Write(dll); }
                }
                else p.Cecil.Write(dll);

                Assembly asm;
                try { asm = pdb != null ? Assembly.Load(dll.ToArray(), pdb.ToArray()) : Assembly.Load(dll.ToArray()); }
                catch when (pdb != null) { asm = Assembly.Load(dll.ToArray()); }

                var mod = new HotMod { Path = p.Path, Name = p.Name, Assembly = asm, References = p.References };
                ever.Add(asm); live.Add(asm);
                latest[p.Name] = asm;
                loaded[p.Path] = mod;
                order.Add(mod);

                mod.Host = new GameObject("HotReload " + p.Name);
                UnityEngine.Object.DontDestroyOnLoad(mod.Host);
                mod.Host.hideFlags = HideFlags.HideAndDontSave;

                foreach (var td in p.PluginTypes)
                {
                    var type = asm.GetType(td.FullName);
                    var info = type == null ? null : Chainloader.ToPluginInfo(td);
                    if (info == null) { HotReloadPlugin.Log.LogWarning($"{file}: skipped {td.FullName} (not a loadable plugin here)"); continue; }
                    var guid = info.Metadata.GUID;
                    var missing = info.Dependencies.Where(d => (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0
                                                             && !Chainloader.PluginInfos.ContainsKey(d.DependencyGUID)).Select(d => d.DependencyGUID).ToList();
                    if (missing.Count > 0) { HotReloadPlugin.Log.LogError($"{file}: {guid} needs {string.Join(", ", missing)}, which isn't loaded"); continue; }
                    try
                    {
                        Chainloader.PluginInfos[guid] = info;
                        Trackers.Loading = asm;
                        var plugin = (BaseUnityPlugin)mod.Host.AddComponent(type);
                        var tr = Traverse.Create(info);
                        tr.Property("Instance").SetValue(plugin);
                        tr.Property("Location").SetValue(p.Path);
                        mod.Plugins.Add(plugin);
                        mod.Guids.Add(guid);
                    }
                    catch (Exception e)
                    {
                        Chainloader.PluginInfos.Remove(guid);
                        HotReloadPlugin.Log.LogError($"{file}: {guid} failed to start: {e}");
                    }
                    finally { Trackers.Loading = null; }
                }
                HotReloadPlugin.Log.LogInfo($"Loaded {file}: {string.Join(", ", mod.Guids)} ({sw.ElapsedMilliseconds} ms{(p.HasSymbols ? ", with line numbers" : "")})");
                if (isLive && mod.Plugins.Count > 0) HotReloadPlugin.Instance.StartCoroutine(Libraries.AfterLiveLoad(mod));
            }
            catch (Exception e)
            {
                HotReloadPlugin.Log.LogError($"{file}: failed to load: {e}");
            }
            finally { p.Cecil.Dispose(); }
        }

        // ---------------------------------------------------------------- unload
        public static void Unload(HotMod m)
        {
            var file = System.IO.Path.GetFileName(m.Path);
            var report = new Cleanup.Report();
            try { Cleanup.Unload(m, report); }
            catch (Exception e) { HotReloadPlugin.Log.LogError($"{file}: cleanup failed part-way: {e}"); }
            live.Remove(m.Assembly);
            loaded.Remove(m.Path);
            order.Remove(m);
            if (latest.TryGetValue(m.Name, out var a) && a == m.Assembly) latest.Remove(m.Name);
            HotReloadPlugin.Log.LogInfo($"Unloaded {file}: {report}");
        }
    }
}
