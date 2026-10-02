using System.Text.RegularExpressions;
using Mono.Cecil;

// Finds the ROUNDS install and builds the assembly resolver that scan/fix check mods against:
// game Managed/ > BepInEx/core > --ref folders > the mods' own folders > BepInEx/plugins.
sealed class Game
{
    public readonly string Dir, Managed;
    public readonly MapResolver Resolver = new();
    public readonly ModuleDefinition AssemblyCSharp;
    public string? UnboundLib;   // "4.2.5 (path)" when found

    // Old packages that are known not to work on the 2025 build and would shadow their replacements.
    static readonly string[] OldPackages = { "willis81808-UnboundLib", "willis81808-MMHook", "olavim-RoundsWithFriends" };

    public Game(string? dir, IEnumerable<string> refs, IEnumerable<string> inputs)
    {
        Dir = dir ?? Find() ?? throw new UserError(
            "Couldn't find ROUNDS. Pass its folder with --game <path> (the folder that has ROUNDS.app or Rounds_Data).");
        Managed = ManagedDir(Dir) ?? throw new UserError($"No Managed folder with Assembly-CSharp.dll under {Dir}");

        foreach (var f in Directory.GetFiles(Managed, "*.dll")) Resolver.Add(f);
        var core = Path.Combine(Dir, "BepInEx", "core");
        if (Directory.Exists(core)) foreach (var f in Directory.GetFiles(core, "*.dll")) Resolver.Add(f);
        foreach (var r in refs) AddTree(r, skipOld: false);
        foreach (var i in inputs) AddTree(Directory.Exists(i) ? i : Path.GetDirectoryName(Path.GetFullPath(i))!, skipOld: false);
        var plugins = Path.Combine(Dir, "BepInEx", "plugins");
        if (Directory.Exists(plugins)) AddTree(plugins, skipOld: true);
        // Not in the game (a plain install, or still the old UnboundLib 3): fetch what's needed to read mods.
        if (Resolver.Path("BepInEx") == null && Deps.BepInExCore() is string bc)
            foreach (var f in Directory.GetFiles(bc, "*.dll")) Resolver.Add(f);
        if ((Resolver.Get("UnboundLib")?.Name.Version.Major ?? 0) < 4 && Deps.UnboundLib4() is string ul4)
            foreach (var f in Directory.GetFiles(ul4, "*.dll")) Resolver.Set(f);

        AssemblyCSharp = Resolver.Get("Assembly-CSharp")?.MainModule ?? throw new UserError("Can't read Assembly-CSharp.dll");
        var player = AssemblyCSharp.GetType("Player");
        if (player == null || !player.Properties.Any(p => p.Name == "PlayerID"))
            throw new UserError($"{Dir} looks like the old game (the old-rounds-for-mods branch): mods built for it already " +
                                "work there. Point --game at an install of the current build.");

        if (Resolver.Path("UnboundLib") is string ul)
        {
            var v = Resolver.Get("UnboundLib")!.Name.Version;
            UnboundLib = $"{v} ({ul})";
            if (v.Major < 4) UnboundLib += "  !! this is the old UnboundLib; get Bknibb's 4.x port (github.com/Bknibb/UnboundLib) and pass --ref <its folder>";
        }
    }

    // Adds every DLL under dir. When two files have the same assembly name, the higher version wins
    // (so Bknibb's UnboundLib 4 beats an old 3.x copy), and MMHOOK sits next to the UnboundLib that won.
    void AddTree(string dir, bool skipOld)
    {
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)
            .Where(f => !skipOld || !OldPackages.Any(o => Path.GetRelativePath(dir, f).StartsWith(o, StringComparison.OrdinalIgnoreCase)))
            .Select(f => (path: f, name: MapResolver.ReadName(f)))
            .Where(x => x.name != null)
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

    public static string? Find()
    {
        foreach (var steam in SteamRoots())
            foreach (var lib in Libraries(steam))
            {
                var d = Path.Combine(lib, "steamapps", "common", "ROUNDS");
                if (Directory.Exists(d) && ManagedDir(d) != null) return d;
            }
        return null;
    }

    static IEnumerable<string> SteamRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            string? reg = null;
            try { reg = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; }
            catch { }
            if (reg != null) yield return reg.Replace('/', '\\');
            yield return @"C:\Program Files (x86)\Steam";
            yield return @"C:\Program Files\Steam";
        }
        else if (OperatingSystem.IsMacOS())
            yield return Path.Combine(home, "Library", "Application Support", "Steam");
        else
        {
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".local", "share", "Steam");
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        }
    }

    static IEnumerable<string> Libraries(string steam)
    {
        yield return steam;
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace("\\\\", "\\");
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
        a = AssemblyDefinition.ReadAssembly(p, new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred });
        cache[name.Name] = a;
        return a;
    }
    public void Dispose() { foreach (var a in cache.Values) a.Dispose(); }
}

sealed class UserError(string message) : Exception(message);
