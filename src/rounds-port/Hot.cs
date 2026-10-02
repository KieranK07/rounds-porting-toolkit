using Mono.Cecil;

// `rounds-port hot`: port a mod and drop it into the game's hot-reload folder, where the Hot Reload plugin
// (src/HotReload) swaps it in while the game runs. With --watch, do it again every time the mod's DLL changes.
static class Hot
{
    public static int Run(Game game, List<string> inputs, bool watch)
    {
        var bepinex = Path.Combine(game.Dir, "BepInEx");
        var plugins = Path.Combine(bepinex, "plugins");
        var hotDir = Path.Combine(bepinex, HotFolder(bepinex));
        Directory.CreateDirectory(hotDir);

        if (!Directory.Exists(plugins) || !Directory.GetFiles(plugins, "HotReload.dll", SearchOption.AllDirectories).Any())
            Out.Warn("the Hot Reload plugin isn't installed, so nothing will load these. Run: rounds-port install-hotreload");
        if (Directory.Exists(plugins) && Directory.GetFiles(plugins, "ScriptEngine.dll", SearchOption.AllDirectories).Any())
            Out.Warn("ScriptEngine is installed too and loads the same folder. Run: rounds-port install-hotreload (it moves ScriptEngine out)");

        var files = Program.Expand(inputs).Select(Path.GetFullPath).ToList();
        if (files.Count == 0) { Out.Error("no mod DLLs found"); return 3; }
        int worst = 0;
        foreach (var f in files) worst = Math.Max(worst, Deploy(game, f, hotDir, plugins));
        if (!watch) return worst;

        Out.Line($"\nwatching {files.Count} file{(files.Count == 1 ? "" : "s")}; every rebuild is ported and swapped in. Ctrl+C to stop.");
        var changed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var watchers = files.GroupBy(Path.GetDirectoryName).Select(g =>
        {
            var w = new FileSystemWatcher(g.Key!) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            void On(string p) { if (files.Contains(Path.GetFullPath(p), StringComparer.OrdinalIgnoreCase)) lock (changed) changed[Path.GetFullPath(p)] = DateTime.UtcNow; }
            w.Changed += (_, e) => On(e.FullPath); w.Created += (_, e) => On(e.FullPath); w.Renamed += (_, e) => On(e.FullPath);
            w.EnableRaisingEvents = true;
            return w;
        }).ToList();
        while (true)
        {
            Thread.Sleep(250);
            List<string> ready;
            lock (changed)
            {
                ready = changed.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromMilliseconds(600)).Select(kv => kv.Key).ToList();
                foreach (var r in ready) changed.Remove(r);
            }
            foreach (var r in ready)
            {
                if (!Readable(r)) { lock (changed) changed[r] = DateTime.UtcNow; continue; }   // build still writing
                Out.Line($"\n{DateTime.Now:HH:mm:ss} {Path.GetFileName(r)} changed");
                Deploy(game, r, hotDir, plugins);
            }
        }
    }

    // The folder the plugin watches (its config can change it).
    static string HotFolder(string bepinex)
    {
        var cfg = Path.Combine(bepinex, "config", "kieran.rounds.hotreload.cfg");
        if (File.Exists(cfg))
            foreach (var line in File.ReadLines(cfg))
                if (line.Trim().StartsWith("Folder") && line.Contains('=')) return line[(line.IndexOf('=') + 1)..].Trim();
        return "scripts";
    }

    static bool Readable(string path)
    {
        try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return true; } catch { return false; }
    }

    static int Deploy(Game game, string dll, string hotDir, string plugins)
    {
        try
        {
            var scanner = new Scanner(game);
            var rp = new ReaderParameters { AssemblyResolver = game.Resolver, ReadingMode = ReadingMode.Immediate, InMemory = true };
            var module = ModuleDefinition.ReadModule(dll, rp);
            var before = scanner.Scan(module);
            var fixer = new Fixer(module, game, scanner);
            if (before.Count > 0) fixer.Run();

            var guids = module.Types.SelectMany(t => t.CustomAttributes).Where(a => a.AttributeType.FullName == "BepInEx.BepInPlugin")
                              .Select(a => (string)a.ConstructorArguments[0].Value).ToList();
            TakeOver(plugins, module.Assembly.Name.Name, guids);

            // One write of the finished bytes, so the plugin never sees a half-written DLL.
            var ms = new MemoryStream();
            module.Write(ms);
            var dest = Path.Combine(hotDir, Path.GetFileName(dll));
            File.WriteAllBytes(dest, ms.ToArray());
            var stalePdb = Path.ChangeExtension(dest, ".pdb");
            var srcPdb = Path.ChangeExtension(dll, ".pdb");
            if (!fixer.Changed && File.Exists(srcPdb)) File.Copy(srcPdb, stalePdb, true);   // unchanged: its .pdb still matches
            else if (File.Exists(stalePdb)) File.Delete(stalePdb);

            var left = fixer.Changed ? scanner.Scan(ModuleDefinition.ReadModule(new MemoryStream(ms.ToArray()), rp)) : before;
            Out.Line($"{Path.GetFileName(dll)} -> {dest}" + (fixer.Changed ? $" (ported: {before.Count - left.Count} fixed)" : ""));
            foreach (var i in left.Where(i => i.Fix == Fix.Manual)) Out.Line($"  MANUAL {i.What}: {i.Detail}");
            Out.Unchecked(scanner.Unchecked);
            return left.Count == 0 ? 0 : left.Any(i => i.Fix == Fix.Manual) ? 2 : 1;
        }
        catch (Exception e) { Out.Error($"{Path.GetFileName(dll)}: {e.Message}"); return 3; }
    }

    // A copy in BepInEx/plugins would be loaded at startup too (same plugin twice); park it outside plugins.
    static void TakeOver(string plugins, string assemblyName, List<string> guids)
    {
        if (!Directory.Exists(plugins)) return;
        var parked = Path.Combine(Path.GetDirectoryName(plugins)!, "plugins-parked-by-rounds-port");
        foreach (var f in Directory.GetFiles(plugins, "*.dll", SearchOption.AllDirectories))
        {
            bool same;
            try
            {
                using var m = ModuleDefinition.ReadModule(f);
                same = m.Assembly.Name.Name == assemblyName || m.Types.SelectMany(t => t.CustomAttributes)
                    .Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin" && guids.Contains((string)a.ConstructorArguments[0].Value));
            }
            catch { continue; }
            if (!same) continue;
            var dest = Path.Combine(parked, Path.GetRelativePath(plugins, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try { File.Move(f, dest, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Windows keeps a DLL the running game loaded locked.
                Out.Warn($"couldn't move {Path.GetRelativePath(plugins, f)} out of BepInEx/plugins ({e.Message.Trim()}). " +
                         "Close ROUNDS and run this again; until then that copy loads too and Hot Reload skips the new one.");
                continue;
            }
            Out.Warn($"moved {Path.GetRelativePath(plugins, f)} out of BepInEx/plugins to {Path.GetRelativePath(Path.GetDirectoryName(plugins)!, dest)} " +
                     "(otherwise it loads twice). If the game is running with that copy, restart it once.");
        }
    }
}

// `rounds-port install-hotreload` / `uninstall-hotreload`: put the Hot Reload plugin (src/HotReload, built into this
// program) into the game's BepInEx/plugins, or take it out. Optional: scan and fix don't need it.
static class HotReloadSetup
{
    public static int Run(string? gameDir, bool install)
    {
        var dir = gameDir ?? Game.Find() ?? throw new UserError("Couldn't find ROUNDS. Pass its folder with --game <path>.");
        var bepinex = Path.Combine(dir, "BepInEx");
        if (!File.Exists(Path.Combine(bepinex, "core", "BepInEx.dll")))
            throw new UserError($"BepInEx isn't installed in {dir}. Hot Reload is a BepInEx plugin: install BepInEx 5 first " +
                                "(https://github.com/KieranK07/rounds-porting-toolkit/blob/main/docs/HOTRELOAD.md says how).");
        var plugins = Path.Combine(bepinex, "plugins");
        var target = Path.Combine(plugins, "HotReload");
        Out.Line($"game: {dir}");
        return install ? Install(bepinex, plugins, target) : Uninstall(bepinex, target);
    }

    static int Install(string bepinex, string plugins, string target)
    {
        var version = EmbeddedVersion();
        // Older copies anywhere else in plugins would load next to this one.
        foreach (var f in Directory.Exists(plugins) ? Directory.GetFiles(plugins, "HotReload.dll", SearchOption.AllDirectories) : Array.Empty<string>())
            if (!string.Equals(Path.GetDirectoryName(f), target, StringComparison.OrdinalIgnoreCase))
                Park(plugins, f, "another copy of Hot Reload");
        // ScriptEngine loads BepInEx/scripts too; Hot Reload stays off while it's there.
        foreach (var f in Directory.Exists(plugins) ? Directory.GetFiles(plugins, "ScriptEngine.dll", SearchOption.AllDirectories) : Array.Empty<string>())
            Park(plugins, f, "ScriptEngine (Hot Reload replaces it)");

        Directory.CreateDirectory(target);
        bool changed = false;
        foreach (var name in new[] { "HotReload.dll", "HotReload.pdb" })
        {
            var bytes = Embedded(name);
            var path = Path.Combine(target, name);
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
            try { File.WriteAllBytes(path, bytes); changed = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new UserError($"couldn't write {path} ({e.Message.Trim()}). Close ROUNDS and run this again."); }
        }
        Directory.CreateDirectory(Path.Combine(bepinex, "scripts"));
        Out.Line(changed ? $"Hot Reload {version} installed in BepInEx/plugins/HotReload." : $"Hot Reload {version} is already installed.");
        Out.Line("Mods in BepInEx/scripts load at startup and swap in when the file changes (F6 reloads all of them).");
        Out.Line((changed ? "Restart ROUNDS if it's running. " : "") + "Next: rounds-port hot MyMod.dll");
        return 0;
    }

    static int Uninstall(string bepinex, string target)
    {
        if (!Directory.Exists(target)) { Out.Line("Hot Reload isn't installed."); return 0; }
        try { Directory.Delete(target, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new UserError($"couldn't remove {target} ({e.Message.Trim()}). Close ROUNDS and run this again."); }
        Out.Line("Hot Reload removed. Restart ROUNDS if it's running.");
        var scripts = Path.Combine(bepinex, "scripts");
        if (Directory.Exists(scripts) && Directory.GetFiles(scripts, "*.dll").Length > 0)
            Out.Warn("mods in BepInEx/scripts don't load without it. To keep them, move them to BepInEx/plugins.");
        return 0;
    }

    static void Park(string plugins, string file, string what)
    {
        var dest = Path.Combine(Path.GetDirectoryName(plugins)!, "plugins-parked-by-rounds-port", Path.GetRelativePath(plugins, file));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try { File.Move(file, dest, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new UserError($"couldn't move {file} ({e.Message.Trim()}). Close ROUNDS and run this again."); }
        Out.Warn($"moved {what} to BepInEx/plugins-parked-by-rounds-port/{Path.GetRelativePath(plugins, file)}");
    }

    static byte[] Embedded(string name)
    {
        using var s = typeof(HotReloadSetup).Assembly.GetManifestResourceStream(name) ?? throw new UserError($"{name} is missing from this build");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static string EmbeddedVersion()
    {
        using var m = ModuleDefinition.ReadModule(new MemoryStream(Embedded("HotReload.dll")));
        var a = m.Types.SelectMany(t => t.CustomAttributes).FirstOrDefault(a => a.AttributeType.FullName == "BepInEx.BepInPlugin");
        return a?.ConstructorArguments[2].Value as string ?? "";
    }
}
