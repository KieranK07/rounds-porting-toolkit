using System.Text.RegularExpressions;
using Mono.Cecil;

// The CLI's side of Game: finding the install through Steam, mods in mod-manager profiles, and dependencies
// downloaded from Thunderstore. The load-time patcher builds without this file.
sealed partial class Game
{
    // installedMods: false leaves out BepInEx/plugins and mod-manager profiles (sweep: same results on every machine).
    public Game(string? dir, IEnumerable<string> refs, IEnumerable<string> inputs, bool installedMods = true)
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
        if (installedMods && Directory.Exists(plugins)) AddTree(plugins, skipOld: true);
        // Mod-manager profiles (r2modman, Thunderstore Mod Manager, Gale) keep mods outside the game folder.
        var profiles = installedMods ? ModManagerProfiles().ToList() : new();
        foreach (var p in profiles) AddTree(p.plugins, skipOld: true);
        if (profiles.Count > 0)
            Out.Note("also reading mods from " + string.Join(", ", profiles.Select(p => $"{p.manager} profile \"{p.profile}\"")));
        // Not in the game (a plain install, or still the old UnboundLib 3): fetch what's needed to read mods.
        if (Resolver.Path("BepInEx") == null && Deps.BepInExCore() is string bc)
            foreach (var f in Directory.GetFiles(bc, "*.dll")) Resolver.Add(f);
        if ((Resolver.Get("UnboundLib")?.Name.Version.Major ?? 0) < 4 && Deps.UnboundLib4() is string ul4)
            foreach (var f in Directory.GetFiles(ul4, "*.dll")) Resolver.Set(f);

        AssemblyCSharp = ReadGame(out UnboundLib);
    }

    // Mods this one uses that aren't installed anywhere: known libraries come from Thunderstore (pinned version,
    // checksum-checked), RoundsWithFriends as Bknibb's 3.x. Their own dependencies too. Only for reading the mod.
    public void EnsureDependencies(IEnumerable<string> dlls)
    {
        var queue = new Queue<string>(dlls);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var dll = queue.Dequeue();
            List<string> names;
            try { using var m = ModuleDefinition.ReadModule(dll); names = m.AssemblyReferences.Select(a => a.Name).ToList(); }
            catch { continue; }
            var missing = names.Where(n => Resolver.Path(n) == null && tried.Add(n)).ToList();
            if (missing.Remove("RoundsWithFriends")) EnsureRoundsWithFriends3();
            var libs = missing.Where(Deps.Libraries.ContainsKey).ToList();
            if (libs.Count == 0) continue;
            Out.Note($"{Path.GetFileName(dll)} uses {string.Join(", ", libs)}, not installed: checking against them from Thunderstore");
            foreach (var name in libs)
            {
                var lib = Deps.Libraries[name];
                string? dir;
                try { dir = Deps.ThunderstorePackage(lib.package, lib.version, lib.sha); }
                catch (Exception e) { Out.Warn($"couldn't get {lib.package} {lib.version} ({e.Message})"); continue; }
                if (dir == null) continue;
                AddTree(dir, skipOld: false);
                foreach (var f in Directory.GetFiles(dir, "*.dll")) queue.Enqueue(f);
            }
        }
    }

    static IEnumerable<(string manager, string profile, string plugins)> ModManagerProfiles()
    {
        var bases = new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData }
            .Select(Environment.GetFolderPath).Where(b => b.Length > 0).Distinct();
        var managers = new[] { ("r2modman", "r2modmanPlus-local"), ("Thunderstore Mod Manager", Path.Combine("Thunderstore Mod Manager", "DataFolder")), ("Gale", "com.kesomannen.gale") };
        var found = new List<(string, string, string, DateTime)>();
        foreach (var b in bases)
            foreach (var (manager, sub) in managers)
            {
                var root = Path.Combine(b, sub);
                if (!Directory.Exists(root)) continue;
                foreach (var game in Directory.GetDirectories(root).Where(d => Path.GetFileName(d).Equals("ROUNDS", StringComparison.OrdinalIgnoreCase)))
                {
                    var profiles = Path.Combine(game, "profiles");
                    if (!Directory.Exists(profiles)) continue;
                    foreach (var prof in Directory.GetDirectories(profiles))
                    {
                        var plugins = Path.Combine(prof, "BepInEx", "plugins");
                        if (Directory.Exists(plugins)) found.Add((manager, Path.GetFileName(prof), plugins, Directory.GetLastWriteTimeUtc(plugins)));
                    }
                }
            }
        // most recently changed profile first: it wins when two profiles have different versions of a mod
        return found.OrderByDescending(f => f.Item4).Select(f => (f.Item1, f.Item2, f.Item3)).Distinct();
    }

    // RoundsWithFriends 3 (Bknibb's port) from the game, else downloaded.
    public void EnsureRoundsWithFriends3()
    {
        if ((Resolver.Get("RoundsWithFriends")?.Name.Version.Major ?? 0) >= 3) return;
        if (Deps.RoundsWithFriends3() is string rwf) foreach (var f in Directory.GetFiles(rwf, "*.dll")) Resolver.Set(f);
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
}
