using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RoundsHotReload
{
    // Loads mods from BepInEx/scripts and swaps them while the game runs. Unlike ScriptEngine it reloads only the
    // DLL that changed (plus mods that depend on it), doesn't need a .pdb, keeps one broken mod from stopping the
    // rest, and cleans up after the old copy: Harmony and MonoMod patches, event handlers, cards and menus it
    // registered, asset bundles and components.
    [BepInPlugin(GUID, "Hot Reload", Version)]
    // After the shared libraries, so a hot mod registers with them at startup exactly like a normal plugin would.
    [BepInDependency("com.willis.rounds.unbound", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("pykess.rounds.plugins.moddingutils", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("root.rarity.lib", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("root.cardtheme.lib", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("root.classes.manager.reborn", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.willis.rounds.modsplus", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("io.olavim.rounds.rwf", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("io.olavim.rounds.mapsextended", BepInDependency.DependencyFlags.SoftDependency)]
    public class HotReloadPlugin : BaseUnityPlugin
    {
        public const string GUID = "kieran.rounds.hotreload", Version = "1.0.0";
        internal static ManualLogSource Log;
        internal static HotReloadPlugin Instance;

        ConfigEntry<string> folder;
        ConfigEntry<KeyboardShortcut> reloadKey;
        ConfigEntry<bool> loadOnStart, watch;
        ConfigEntry<float> delay;

        FileSystemWatcher watcher;
        readonly object pendingLock = new object();
        readonly Dictionary<string, float> pending = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        internal string Folder => Path.Combine(Paths.BepInExRootPath, folder.Value);

        void Awake()
        {
            Instance = this;
            Log = Logger;
            folder = Config.Bind("General", "Folder", "scripts", "Folder under BepInEx that holds hot-reloadable mods.");
            loadOnStart = Config.Bind("General", "LoadOnStart", true, "Load the mods in that folder when the game starts.");
            reloadKey = Config.Bind("General", "ReloadKey", new KeyboardShortcut(KeyCode.F6), "Reload every mod in the folder.");
            watch = Config.Bind("AutoReload", "Watch", true, "Reload a mod as soon as its DLL changes.");
            delay = Config.Bind("AutoReload", "Delay", 1f, "Seconds to wait after the last change before reloading (lets a build finish writing).");

            Directory.CreateDirectory(Folder);
            if (folder.Value == "scripts" && Directory.GetFiles(Paths.PluginPath, "ScriptEngine.dll", SearchOption.AllDirectories).Length > 0)
            {
                Log.LogError("ScriptEngine is installed too and loads BepInEx/scripts as well, so every mod there would load twice. " +
                             "Remove BepInEx/plugins/ScriptEngine.dll (Hot Reload replaces it). Hot Reload is off until then.");
                enabled = false;
                return;
            }
            Trackers.Install(new Harmony(GUID));
            AppDomain.CurrentDomain.AssemblyResolve += HotLoader.Resolve;

            if (loadOnStart.Value)
            {
                // Load once every plugin has loaded (so all dependencies exist) but before anything's Start runs,
                // which is where RarityLib stops accepting new rarities: right before it locks, or at our own Start.
                var finalize = AccessTools.Method(AccessTools.TypeByName("RarityLib.Utils.RarityUtils"), "FinalizeRaritys");
                if (finalize != null)
                    try { new Harmony(GUID + ".startup").Patch(finalize, prefix: new HarmonyMethod(typeof(HotReloadPlugin), nameof(LoadStartup))); }
                    catch (Exception e) { Log.LogWarning("couldn't hook RarityLib startup: " + e.Message); }
            }
            if (watch.Value) StartWatcher();
        }

        bool startupLoaded;
        static void LoadStartup()
        {
            var self = Instance;
            if (self == null || self.startupLoaded || !self.loadOnStart.Value || !self.enabled) return;
            self.startupLoaded = true;
            HotLoader.LoadAll(self.Files(), deferMissingDependencies: false);
        }

        void Start() => LoadStartup();

        IEnumerable<string> Files() => Directory.GetFiles(Folder, "*.dll", SearchOption.TopDirectoryOnly);

        void StartWatcher()
        {
            try
            {
                watcher = new FileSystemWatcher(Folder, "*.dll") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler h = (s, e) => Queue(e.FullPath);
                watcher.Changed += h; watcher.Created += h; watcher.Deleted += h;
                watcher.Renamed += (s, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) { Log.LogWarning("File watching unavailable, use the reload key: " + e.Message); }
        }

        // Called from the watcher thread; Update does the work on the main thread once the file has been quiet.
        void Queue(string path)
        {
            lock (pendingLock) pending[Path.GetFullPath(path)] = 0f;
        }

        void Update()
        {
            if (reloadKey.Value.IsDown())
            {
                lock (pendingLock) pending.Clear();
                HotLoader.ReloadAll(Files());
                return;
            }
            List<string> ready = null;
            lock (pendingLock)
            {
                foreach (var k in pending.Keys.ToList())
                {
                    pending[k] += Time.unscaledDeltaTime;
                    if (pending[k] >= delay.Value) (ready ??= new List<string>()).Add(k);
                }
                if (ready != null) foreach (var k in ready) pending.Remove(k);
            }
            if (ready == null) return;
            // A file still being written fails to open exclusively; try again next time it changes or in a second.
            var stable = ready.Where(p => !File.Exists(p) || CanRead(p)).ToList();
            foreach (var p in ready.Except(stable)) Queue(p);
            if (stable.Count > 0) HotLoader.ReloadChanged(stable);
        }

        static bool CanRead(string path)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
            catch { return false; }
        }

        void OnDestroy()
        {
            if (watcher != null) watcher.EnableRaisingEvents = false;
        }
    }
}
