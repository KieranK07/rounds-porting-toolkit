using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using Mono.Cecil;

// What AutoFix does to BepInEx/plugins, before the Chainloader loads it: every mod that rounds-port's fix changes is
// replaced in place by its fixed copy. The original goes to <cache>/originals/<sha256>.dll, so a newer rounds-port or
// a game update re-fixes from the original, and RestoreOriginals can put it back. An index of what was checked (size,
// write time, hashes) means a start with no new or updated mods doesn't read any of them.
sealed class AutoFix(AutoFix.Settings settings, ManualLogSource log)
{
    public sealed record Settings(string GameDir, string Managed, string Core, string Plugins, string Cache,
        bool FixWhenManualLeft, string[] Exclude, string[] FixAnyway);

    sealed class Entry
    {
        public long Size, Time;
        public string Sha = "", Orig = "", Result = "", Note = "";
        public bool Retry;   // couldn't write: try again next start even if the file doesn't change
    }

    // index results
    const string Fixed = "fixed", Unchanged = "unchanged", NotMod = "not-a-mod", Manual = "manual", Excluded = "excluded", Error = "error";

    readonly string indexPath = Path.Combine(settings.Cache, "index.tsv");
    readonly string originals = Path.Combine(settings.Cache, "originals");
    Game? game;
    Scanner? scanner;

    // Everything AutoFix's result depends on besides the mod itself. A change re-checks every mod, from its original.
    string Key()
    {
        string gameMvid;
        using (var m = ModuleDefinition.ReadModule(Path.Combine(settings.Managed, "Assembly-CSharp.dll"))) gameMvid = m.Mvid.ToString();
        return string.Join(" ", typeof(AutoFix).Assembly.ManifestModule.ModuleVersionId, gameMvid, settings.FixWhenManualLeft,
            string.Join(",", settings.Exclude), string.Join(",", settings.FixAnyway));
    }

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(settings.Cache);
        var (files, old) = PluginFiles();
        foreach (var o in old)
            log.LogWarning($"BepInEx/plugins/{o} is a package that doesn't work on the current game (UnboundLib 3, MMHook or RoundsWithFriends 2). " +
                           "Use Bknibb's ports instead (github.com/Bknibb/UnboundLib, github.com/Bknibb/RoundsWithFriends). Mods aren't fixed against it");

        var key = Key();
        var (oldKey, index) = ReadIndex();
        if (oldKey == key + " old-game") { log.LogInfo("this is the old game build (old-rounds-for-mods): mods work on it as they are"); return; }
        bool keyChanged = oldKey != key;
        var next = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        var todo = new List<(string rel, FileInfo fi)>();
        foreach (var (rel, fi) in files)
        {
            if (!keyChanged && index.TryGetValue(rel, out var e) && e.Size == fi.Length && e.Time == fi.LastWriteTimeUtc.Ticks) next[rel] = e;
            else todo.Add((rel, fi));
        }

        if (todo.Count > 0)
        {
            log.LogInfo(keyChanged && index.Count > 0
                ? $"checking {todo.Count} DLLs again (rounds-port, the game or the settings changed)"
                : $"checking {todo.Count} new or updated DLLs");
            try { game = new Game(settings.GameDir, settings.Managed, settings.Core, settings.Plugins); }
            catch (UserError)
            {
                WriteIndex(key + " old-game", new SortedDictionary<string, Entry>());
                log.LogInfo("this is the old game build (old-rounds-for-mods): mods work on it as they are");
                return;
            }
            if (game.UnboundLib == null)
                log.LogWarning("UnboundLib 4 isn't installed: most mods need it (Bknibb's port, github.com/Bknibb/UnboundLib)");
            scanner = new Scanner(game);
            foreach (var (rel, fi) in todo)
            {
                index.TryGetValue(rel, out var e);
                Entry result;
                try { result = Check(rel, fi.FullName, e, keyChanged); }
                catch (Exception ex)
                {
                    // keeps the old hashes: if the file is still our fixed copy, its original must stay in the cache
                    result = new Entry { Result = Error, Note = OneLine($"{ex.GetType().Name}: {ex.Message}"), Sha = e?.Sha ?? "", Orig = e?.Orig ?? "" };
                    log.LogWarning($"couldn't check {rel}, left as it is: {ex}");
                }
                var now = new FileInfo(fi.FullName);
                if (!now.Exists) continue;
                result.Size = result.Retry ? -1 : now.Length;
                result.Time = now.LastWriteTimeUtc.Ticks;
                next[rel] = result;
            }
            game.Resolver.Dispose();
            game = null;
            scanner = null;
            GC.Collect();   // a big mod is a few hundred MB of Cecil objects; give it back before the game starts
        }

        // Originals that no fixed mod needs any more (the mod was updated or removed).
        var keep = new HashSet<string>(next.Values.Where(IsCopy).Select(e => e.Orig), StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(originals))
            foreach (var f in Directory.GetFiles(originals, "*.dll"))
                if (!keep.Contains(Path.GetFileNameWithoutExtension(f))) TryDelete(f);
        if (todo.Count > 0 || next.Count != index.Count) WriteIndex(key, next);

        int Count(string r) => next.Values.Count(e => e.Result == r);
        var mods = next.Count - Count(NotMod);
        var summary = $"{mods} mods: {Count(Fixed)} fixed, {Count(Unchanged)} need nothing";
        if (Count(Manual) > 0) summary += $", {Count(Manual)} left as they are (problems only their authors can fix)";
        if (Count(Excluded) > 0) summary += $", {Count(Excluded)} excluded";
        if (Count(Error) > 0) summary += $", {Count(Error)} with errors";
        log.LogInfo($"{summary} ({clock.ElapsedMilliseconds} ms)");
        if (todo.Count == 0)
            foreach (var kv in next.Where(kv => kv.Value.Result is Manual or Error))
                log.LogInfo($"  {(kv.Value.Result == Manual ? "left as it is" : "error")}: {kv.Key}: {kv.Value.Note}");
    }

    // One mod DLL, new or changed since the last start (or everything changed: e is the old entry).
    Entry Check(string rel, string path, Entry? e, bool keyChanged)
    {
        var bytes = File.ReadAllBytes(path);
        var sha = Sha(bytes);
        if (e != null && !keyChanged && sha == e.Sha && e.Size >= 0) return e;   // only touched (-1: retry, see Entry.Retry)

        // Still our fixed copy (rounds-port, the game or the settings changed): start again from the original.
        bool ours = e != null && IsCopy(e) && sha == e.Sha;
        var orig = bytes;
        var origSha = sha;
        if (ours)
        {
            var o = Path.Combine(originals, e!.Orig + ".dll");
            if (File.Exists(o)) { orig = File.ReadAllBytes(o); origSha = e.Orig; }
            else { ours = false; log.LogWarning($"{rel}: its original isn't in {originals} any more; checking the file as it is"); }
        }
        // ours: the file on disk isn't the original, so every outcome other than "fixed" puts the original back
        Entry Done(string result, string note = "")
        {
            if (ours) { Replace(path, orig); log.LogInfo($"put back the original of {rel}"); }
            return new Entry { Sha = origSha, Orig = origSha, Result = result, Note = note };
        }

        if (Matches(rel, settings.Exclude)) return Done(Excluded);
        ModuleDefinition module;
        var rp = new ReaderParameters { AssemblyResolver = game!.Resolver, ReadingMode = ReadingMode.Immediate };
        try
        {
            // the same DLLs the CLI picks from a folder (Program.Expand): ones that use the game or UnboundLib
            using (var peek = ModuleDefinition.ReadModule(new MemoryStream(orig)))
                if (!peek.AssemblyReferences.Any(a => a.Name is "Assembly-CSharp" or "UnboundLib") || peek.Name.StartsWith("MMHOOK")) return Done(NotMod);
            module = ModuleDefinition.ReadModule(new MemoryStream(orig), rp);
        }
        catch (Exception ex)
        {
            log.LogWarning($"couldn't read {rel}, left as it is: {ex.GetType().Name}: {OneLine(ex.Message)}");
            return Done(Error, OneLine($"{ex.GetType().Name}: {ex.Message}"));
        }

        scanner!.Scan(module);
        var fixer = new Fixer(module, game, scanner);
        fixer.Run();
        if (!fixer.Changed) return Done(Unchanged);
        var ms = new MemoryStream();
        module.Write(ms);
        var fixedBytes = ms.ToArray();
        var left = scanner.Scan(ModuleDefinition.ReadModule(new MemoryStream(fixedBytes), rp));
        var manual = left.Where(i => i.Fix == Fix.Manual).ToList();
        if (manual.Count > 0 && !settings.FixWhenManualLeft && !Matches(rel, settings.FixAnyway))
        {
            var note = $"{manual.Count} MANUAL item{(manual.Count == 1 ? "" : "s")}: " + string.Join("; ", manual.Take(3).Select(i => i.What)) + (manual.Count > 3 ? "; ..." : "");
            log.LogWarning($"left {rel} as it is: {note}. Only its author can fix these (FixAnyway in rounds-port.autofix.cfg rewrites it anyway)");
            return Done(Manual, OneLine(note));
        }

        var fixedSha = Sha(fixedBytes);
        try
        {
            SaveOriginal(origSha, orig);
            if (fixedSha != sha) Replace(path, fixedBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning($"couldn't save the fixed copy of {rel}, left as it is: {ex.Message}");
            return new Entry { Sha = sha, Orig = origSha, Result = Error, Note = OneLine("can't write: " + ex.Message), Retry = true };
        }
        var review = left.Where(i => i.Fix == Fix.Review).Select(i => i.Kind).Distinct().ToList();
        log.LogInfo($"fixed {rel}: {fixer.Changes.Count()} kind{(fixer.Changes.Count() == 1 ? "" : "s")} of change" +
                    (review.Count > 0 ? $"; to check in game: {string.Join(", ", review)}" : "") +
                    (manual.Count > 0 ? $"; {manual.Count} MANUAL item{(manual.Count == 1 ? "" : "s")} left" : ""));
        return new Entry { Sha = fixedSha, Orig = origSha, Result = Fixed };
    }

    // Puts every fixed mod that is still our copy back to its original, and forgets the index.
    public void RestoreOriginals()
    {
        var (_, index) = ReadIndex();
        int n = 0;
        foreach (var kv in index.Where(kv => IsCopy(kv.Value)))
        {
            var path = Path.Combine(settings.Plugins, kv.Key);
            var o = Path.Combine(originals, kv.Value.Orig + ".dll");
            try
            {
                if (!File.Exists(path) || !File.Exists(o) || Sha(File.ReadAllBytes(path)) != kv.Value.Sha) continue;
                Replace(path, File.ReadAllBytes(o));
                n++;
            }
            catch (Exception ex) { log.LogWarning($"couldn't put back the original of {kv.Key}: {ex.Message}"); }
        }
        TryDelete(indexPath);
        log.LogInfo($"put back {n} original mod{(n == 1 ? "" : "s")}; AutoFix is off now (Enabled in rounds-port.autofix.cfg)");
    }

    // The file on disk is our fixed copy, not the original.
    static bool IsCopy(Entry e) => e.Orig.Length > 0 && e.Sha != e.Orig;

    // Every *.dll under plugins (paths relative to it, sorted), and the old packages that are skipped.
    // Also finishes a replace that was interrupted (see Replace).
    (List<(string rel, FileInfo fi)> files, List<string> old) PluginFiles()
    {
        var files = new List<(string, FileInfo)>();
        var old = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(settings.Plugins)) return (files, old.ToList());
        foreach (var bak in Directory.GetFiles(settings.Plugins, "*.rpbak", SearchOption.AllDirectories))
        {
            var dll = bak.Substring(0, bak.Length - ".rpbak".Length);
            if (File.Exists(dll)) TryDelete(bak);
            else File.Move(bak, dll);
        }
        foreach (var tmp in Directory.GetFiles(settings.Plugins, "*.rptmp", SearchOption.AllDirectories)) TryDelete(tmp);

        foreach (var f in Directory.GetFiles(settings.Plugins, "*.dll", SearchOption.AllDirectories))
        {
            if (!f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = f.Substring(settings.Plugins.Length).TrimStart('/', '\\');
            var top = rel.Split('/', '\\')[0];
            if (Game.OldPackages.Any(o => top.StartsWith(o, StringComparison.OrdinalIgnoreCase))) { old.Add(top); continue; }
            files.Add((rel, new FileInfo(f)));
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return (files, old.ToList());
    }

    // A file name ("MapsExtended.dll" or "MapsExtended") or a folder name ("olavim-MapsExtended") anywhere in rel.
    static bool Matches(string rel, string[] names)
    {
        var parts = rel.Split('/', '\\');
        var file = parts[parts.Length - 1];
        return names.Any(n => parts.Any(p => p.Equals(n, StringComparison.OrdinalIgnoreCase))
                              || Path.GetFileNameWithoutExtension(file).Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    // Swaps in new bytes without ever writing into the existing file: a mod manager may hard-link plugins to its own
    // download cache (Gale does), and that copy must stay the original. Renames only, so a crash leaves either the old
    // file or <name>.rpbak, which the next start puts back.
    static void Replace(string path, byte[] bytes)
    {
        string tmp = path + ".rptmp", bak = path + ".rpbak";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(bak)) File.Delete(bak);
        File.Move(path, bak);
        File.Move(tmp, path);
        File.Delete(bak);
    }

    void SaveOriginal(string sha, byte[] bytes)
    {
        var dest = Path.Combine(originals, sha + ".dll");
        if (File.Exists(dest)) return;
        Directory.CreateDirectory(originals);
        File.WriteAllBytes(dest + ".rptmp", bytes);
        File.Move(dest + ".rptmp", dest);
    }

    // index.tsv: a key line, then one line per DLL: path, size, write time, sha256, sha256 of the original, result, note
    (string key, Dictionary<string, Entry> index) ReadIndex()
    {
        var index = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!File.Exists(indexPath)) return ("", index);
        try
        {
            var lines = File.ReadAllLines(indexPath);
            var key = lines.FirstOrDefault(l => l.StartsWith("key\t"))?.Substring(4) ?? "";
            foreach (var p in lines.Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("key\t")).Select(l => l.Split('\t')))
                if (p.Length >= 6)
                    index[p[0]] = new Entry { Size = long.Parse(p[1]), Time = long.Parse(p[2]), Sha = p[3], Orig = p[4], Result = p[5], Note = p.Length > 6 ? p[6] : "" };
            return (key, index);
        }
        catch (Exception ex)
        {
            log.LogWarning($"{indexPath} is unreadable ({ex.Message}); checking every mod");
            return ("", new Dictionary<string, Entry>(StringComparer.Ordinal));
        }
    }

    void WriteIndex(string key, SortedDictionary<string, Entry> index)
    {
        var sb = new StringBuilder();
        sb.Append("# rounds-port AutoFix: mods checked at the last start. Delete this file to check them all again.\n");
        sb.Append("key\t").Append(key).Append('\n');
        foreach (var kv in index)
            sb.Append(string.Join("\t", kv.Key, kv.Value.Size, kv.Value.Time, kv.Value.Sha, kv.Value.Orig, kv.Value.Result, kv.Value.Note)).Append('\n');
        File.WriteAllText(indexPath + ".rptmp", sb.ToString());
        if (File.Exists(indexPath)) File.Delete(indexPath);
        File.Move(indexPath + ".rptmp", indexPath);
    }

    static string Sha(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    static string OneLine(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
