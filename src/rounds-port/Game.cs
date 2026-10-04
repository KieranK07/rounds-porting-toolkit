using Mono.Cecil;

// The ROUNDS install and the assembly resolver that scan/fix check mods against:
// game Managed/ > BepInEx/core > --ref folders > the mods' own folders > BepInEx/plugins.
// Shared with the load-time patcher (DuctTape's AutoFix); the CLI's own part (Steam lookup, downloads) is in Game.Cli.cs.
sealed partial class Game
{
    public readonly string Dir, Managed;
    public readonly MapResolver Resolver = new();
    public readonly ModuleDefinition AssemblyCSharp;
    public string? UnboundLib;   // "4.2.5 (path)" when found

    // For the load-time patcher (DuctTape's AutoFix): BepInEx's own paths. Under a mod manager, plugins is in its profile.
    public Game(string dir, string managed, string core, string plugins)
    {
        Dir = dir;
        Managed = managed;
        Resolver.InMemory = true;
        foreach (var f in Directory.GetFiles(Managed, "*.dll")) Resolver.Add(f);
        if (Directory.Exists(core)) foreach (var f in Directory.GetFiles(core, "*.dll")) Resolver.Add(f);
        AddTree(plugins, skipOld: true);
        AssemblyCSharp = ReadGame(out UnboundLib);
    }

    // Assembly-CSharp, once everything is added; refuses the old game. unboundLib: "4.2.5 (path)" when found.
    ModuleDefinition ReadGame(out string? unboundLib)
    {
        var game = Resolver.Get("Assembly-CSharp")?.MainModule ?? throw new UserError("Can't read Assembly-CSharp.dll");
        var player = game.GetType("Player");
        if (player == null || !player.Properties.Any(p => p.Name == "PlayerID"))
            throw new UserError($"{Dir} looks like the old game (the old-rounds-for-mods branch): mods built for it already " +
                                "work there. Point --game at an install of the current build.");

        unboundLib = null;
        if (Resolver.Path("UnboundLib") is string ul)
        {
            var v = Resolver.Get("UnboundLib")!.Name.Version;
            unboundLib = $"{v} ({ul})";
            if (v.Major < 4) unboundLib += "  !! this is the old UnboundLib; get Bknibb's 4.x port (github.com/Bknibb/UnboundLib) and pass --ref <its folder>";
        }
        return game;
    }

    // Adds every DLL under dir. When two files have the same assembly name, the higher version wins
    // (so Bknibb's UnboundLib 4 beats an old 3.x copy), and MMHOOK sits next to the UnboundLib that won.
    // skipOld: leaves out the old libraries (OldLibrary), for installed mods.
    void AddTree(string dir, bool skipOld)
    {
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)
            .Select(f => (path: f, name: MapResolver.ReadName(f)))
            .Where(x => x.name != null && !(skipOld && OldLibrary(x.path, x.name) != null))
            .GroupBy(x => x.name!.Name, StringComparer.OrdinalIgnoreCase);
        string? unboundDir = null;
        foreach (var g in files.OrderBy(g => g.Key == "UnboundLib" ? 0 : 1))
        {
            var best = g.OrderByDescending(x => x.name!.Version)
                        .ThenByDescending(x => unboundDir != null && Path.GetDirectoryName(x.path) == unboundDir)
                        .First();
            Resolver.Add(best.path);
            if (g.Key == "UnboundLib") unboundDir = Path.GetDirectoryName(Resolver.Path("UnboundLib"));
        }
    }

    // An old build of a library the 2025 update broke, which would shadow its port: UnboundLib 3, RoundsWithFriends 2,
    // or an MMHOOK made against the old Assembly-CSharp (willis81808-MMHook). Returns what it is, or null. Decided by
    // content, never by folder: a mod manager may put Bknibb's ports into the old packages' folders.
    public static string? OldLibrary(string path, AssemblyNameDefinition name)
    {
        if ((name.Name == "UnboundLib" && name.Version.Major < 4) || (name.Name == "RoundsWithFriends" && name.Version.Major < 3))
            return $"{name.Name} {name.Version.ToString(3)}";
        if (name.Name != "MMHOOK_Assembly-CSharp") return null;
        // Player.SetPlayerID is new in the 2025 build: an MMHOOK made against it has a hook for it
        try
        {
            using var m = ModuleDefinition.ReadModule(path);
            if (m.GetType("On.Player") is TypeDefinition p && !p.NestedTypes.Any(t => t.Name == "hook_SetPlayerID")) return "an MMHOOK for the old game";
        }
        catch { }
        return null;
    }

    public static string? ManagedDir(string game)
    {
        var mac = Path.Combine(game, "ROUNDS.app", "Contents", "Resources", "Data", "Managed");
        if (File.Exists(Path.Combine(mac, "Assembly-CSharp.dll"))) return mac;
        if (Directory.Exists(game))
            foreach (var d in Directory.GetDirectories(game, "*_Data"))
            {
                var m = Path.Combine(d, "Managed");
                if (File.Exists(Path.Combine(m, "Assembly-CSharp.dll"))) return m;
            }
        if (File.Exists(Path.Combine(game, "Assembly-CSharp.dll"))) return game;   // --game pointed at Managed itself
        return null;
    }
}

// Resolves assemblies by simple name from a fixed map; the first file added for a name wins.
sealed class MapResolver : IAssemblyResolver
{
    readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AssemblyDefinition> cache = new(StringComparer.OrdinalIgnoreCase);
    // Read whole files instead of keeping them open: the load-time patcher replaces plugins it has read (Windows
    // won't replace an open file).
    public bool InMemory { get; set; }

    public static AssemblyNameDefinition? ReadName(string path)
    {
        try { using var a = AssemblyDefinition.ReadAssembly(path); return a.Name; } catch { return null; }
    }

    public void Add(string path)
    {
        var n = ReadName(path)?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path);
        paths.TryAdd(n, path);
    }

    // Replaces whatever was added under this name (e.g. UnboundLib 4 over an old 3.x).
    public void Set(string path)
    {
        var n = ReadName(path)?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path);
        paths[n] = path;
        if (cache.Remove(n, out var old)) old.Dispose();
    }

    public string? Path(string name) => paths.TryGetValue(name, out var p) ? p : null;
    public IEnumerable<AssemblyDefinition> Loaded() => cache.Values.ToList();
    public AssemblyDefinition? Get(string name) { try { return Resolve(new AssemblyNameReference(name, null)); } catch { return null; } }
    public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (cache.TryGetValue(name.Name, out var a)) return a;
        if (!paths.TryGetValue(name.Name, out var p)) throw new AssemblyResolutionException(name);
        a = AssemblyDefinition.ReadAssembly(p, new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred, InMemory = InMemory });
        cache[name.Name] = a;
        return a;
    }
    public void Dispose() { foreach (var a in cache.Values) a.Dispose(); }
}

sealed class UserError(string message) : Exception(message);
