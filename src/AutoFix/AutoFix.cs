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
        bool LeaveManualMods, string[] Exclude, string[] FixAnyway);

    sealed class Entry
    {
        public long Size, Time;
        public string Sha = "", Orig = "", Result = "", Note = "";
        public bool Retry;   // couldn't write: try again next start even if the file doesn't change
    }

    // index results
    const string Fixed = "fixed", Unchanged = "unchanged", NotMod = "not-a-mod", Manual = "manual", Excluded = "excluded", Error = "error", Old = "old";

    readonly string indexPath = Path.Combine(settings.Cache, "index.tsv");
    readonly string originals = Path.Combine(settings.Cache, "originals");
    readonly Curated curated = new(settings.Cache, log);
    Game? game;
    Scanner? scanner;

    // Everything AutoFix's result depends on besides the mod itself. A change re-checks every mod, from its original.
    string Key()
    {
        string gameMvid;
        using (var m = ModuleDefinition.ReadModule(Path.Combine(settings.Managed, "Assembly-CSharp.dll"))) gameMvid = m.Mvid.ToString();
        return string.Join(" ", typeof(AutoFix).Assembly.ManifestModule.ModuleVersionId, gameMvid, settings.LeaveManualMods,
            string.Join(",", settings.Exclude), string.Join(",", settings.FixAnyway));
    }

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(settings.Cache);
        var files = PluginFiles();
        var key = Key();
        var (oldKey, index) = ReadIndex();
        if (oldKey == key + " old-game") { log.LogInfo("this is the old game build (old-rounds-for-mods): mods work on it as they are"); return; }
        bool keyChanged = oldKey != key;
        var ports = curated.Plan(files.Select(f => f.fi.FullName)).ToList();
        var next = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        var todo = new List<(string rel, FileInfo fi)>();
        foreach (var (rel, fi) in files)
        {
            // an old library left as it was (its port couldn't be downloaded then) is looked at again once it can be
            if (!keyChanged && index.TryGetValue(rel, out var e) && e.Size == fi.Length && e.Time == fi.LastWriteTimeUtc.Ticks
                && !(e.Result == Old && ports.Count > 0)) next[rel] = e;
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
                // mods fixed for the current game don't work on the old one: they get their originals back
                if (index.Count > 0) RestoreOriginals(off: false);
                WriteIndex(key + " old-game", new SortedDictionary<string, Entry>());
                log.LogInfo("this is the old game build (old-rounds-for-mods): mods work on it as they are");
                return;
            }
            foreach (var p in ports) game.Resolver.Set(p);   // mods are fixed against the ports going in this start
            if (game.Resolver.Path("UnboundLib") == null)
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

        foreach (var kv in next.Where(kv => kv.Value.Result == Old))
            log.LogWarning($"BepInEx/plugins/{kv.Key} is {kv.Value.Note}, which doesn't work on the current game: use Bknibb's port " +
                           "(github.com/Bknibb/UnboundLib, github.com/Bknibb/RoundsWithFriends). Mods aren't fixed against it");
        int Count(string r) => next.Values.Count(e => e.Result == r);
        var mods = next.Count - Count(NotMod) - Count(Old);
        int partly = next.Values.Count(e => e.Result == Fixed && e.Note.Length > 0);
        var summary = $"{mods} mods: {Count(Fixed)} fixed" + (partly > 0 ? $" ({partly} with problems only their authors can fix)" : "") +
                      $", {Count(Unchanged)} need nothing";
        if (Count(Manual) > 0) summary += $", {Count(Manual)} left as they are (LeaveManualMods)";
        if (Count(Excluded) > 0) summary += $", {Count(Excluded)} excluded";
        if (Count(Error) > 0) summary += $", {Count(Error)} with errors";
        if (Count(Old) > 0) summary += $"; {Count(Old)} old librar{(Count(Old) == 1 ? "y" : "ies")} skipped";
        log.LogInfo($"{summary} ({clock.ElapsedMilliseconds} ms)");
        if (todo.Count == 0)
            foreach (var kv in next.Where(kv => kv.Value.Result is Manual or Error))
                log.LogInfo($"  {(kv.Value.Result == Manual ? "left as it is" : "error")}: {kv.Key}: {kv.Value.Note}");
    }

    // One mod DLL, new or changed since the last start (or everything changed: e is the old entry).
    Entry Check(string rel, string path, Entry? e, bool keyChanged)
    {
        var bytes = ReadShared(path);
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
        // bytes become the plugin; its original is kept in the cache
        Entry Save(byte[] bytes, string? note, string result)
        {
            var newSha = Sha(bytes);
            try
            {
                SaveOriginal(origSha, orig);
                if (newSha != sha) Replace(path, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning($"couldn't save the fixed copy of {rel}, left as it is: {ex.Message}");
                return new Entry { Sha = sha, Orig = origSha, Result = Error, Note = OneLine("can't write: " + ex.Message), Retry = true };
            }
            return new Entry { Sha = newSha, Orig = origSha, Result = result, Note = note ?? "" };
        }

        if (Matches(rel, settings.Exclude)) return Done(Excluded);
        // Bknibb's port in place of an old library, or a hand-made patch for this exact file (Curated.cs)
        byte[] input;
        try { input = curated.Apply(path, orig, origSha); }
        catch (Exception ex)
        {
            log.LogWarning($"{rel}: {ex.Message}; fixing it without its curated patch");
            input = orig;
        }
        bool curatedOnly = input != orig;   // reference: Apply returns the same array when nothing applies
        ModuleDefinition module;
        var rp = new ReaderParameters { AssemblyResolver = game!.Resolver, ReadingMode = ReadingMode.Immediate };
        try
        {
            // the same DLLs the CLI picks from a folder (Program.Expand): ones that use the game or UnboundLib
            using (var peek = ModuleDefinition.ReadModule(new MemoryStream(input)))
            {
                // (a port from Curated isn't old; OldLibrary reads the MMHOOK on disk, which is still the old one)
                if (!curatedOnly && peek.Assembly != null && Game.OldLibrary(path, peek.Assembly.Name) is string old) return Done(Old, old);
                if (!peek.AssemblyReferences.Any(a => a.Name is "Assembly-CSharp" or "UnboundLib") || peek.Name.StartsWith("MMHOOK"))
                    return curatedOnly ? Save(input, null, Fixed) : Done(NotMod);
            }
            module = ModuleDefinition.ReadModule(new MemoryStream(input), rp);
        }
        catch (Exception ex)
        {
            log.LogWarning($"couldn't read {rel}, left as it is: {ex.GetType().Name}: {OneLine(ex.Message)}");
            return Done(Error, OneLine($"{ex.GetType().Name}: {ex.Message}"));
        }

        scanner!.Scan(module);
        var fixer = new Fixer(module, game, scanner);
        fixer.Run();
        if (!fixer.Changed) return curatedOnly ? Save(input, null, Fixed) : Done(Unchanged);
        var ms = new MemoryStream();
        module.Write(ms);
        var fixedBytes = ms.ToArray();
        var left = scanner.Scan(ModuleDefinition.ReadModule(new MemoryStream(fixedBytes), rp));
        var manual = left.Where(i => i.Fix == Fix.Manual).ToList();
        if (manual.Count > 0 && settings.LeaveManualMods && !Matches(rel, settings.FixAnyway))
        {
            var note = $"{manual.Count} MANUAL item{(manual.Count == 1 ? "" : "s")}: " + string.Join("; ", manual.Take(3).Select(i => i.What)) + (manual.Count > 3 ? "; ..." : "");
            log.LogWarning($"left {rel} as it is (LeaveManualMods): {note}");
            return Done(Manual, OneLine(note));
        }

        var saved = Save(fixedBytes, manual.Count > 0 ? $"{manual.Count} MANUAL left" : null, Fixed);
        if (saved.Result == Error) return saved;
        var review = left.Where(i => i.Fix == Fix.Review).Select(i => i.Kind).Distinct().ToList();
        var line = $"fixed {rel}: {fixer.Changes.Count()} kind{(fixer.Changes.Count() == 1 ? "" : "s")} of change" +
                   (review.Count > 0 ? $"; to check in game: {string.Join(", ", review)}" : "");
        if (manual.Count == 0) log.LogInfo(line);
        else log.LogWarning(line + $"; {manual.Count} MANUAL item{(manual.Count == 1 ? "" : "s")} left, which only its author can fix: " +
                            string.Join("; ", manual.Take(3).Select(i => i.What)) + (manual.Count > 3 ? "; ..." : ""));
        return saved;
    }

    // On Windows a virus scanner (or a mod manager still finishing an install) can hold a fresh DLL open for a moment,
    // and File.ReadAllBytes then fails with a sharing violation. Read with permissive sharing and retry briefly.
    static byte[] ReadShared(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var b = new byte[fs.Length]; int n = 0;
                    while (n < b.Length) { int r = fs.Read(b, n, b.Length - n); if (r <= 0) throw new EndOfStreamException(path); n += r; }
                    return b;
                }
            }
            catch (IOException) when (i < 10 && File.Exists(path)) { System.Threading.Thread.Sleep(300); }
        }
    }

    // Puts every fixed mod that is still our copy back to its original, and forgets the index.
    public void RestoreOriginals(bool off = true)
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
                Curated.Restored(path);
                n++;
            }
            catch (Exception ex) { log.LogWarning($"couldn't put back the original of {kv.Key}: {ex.Message}"); }
        }
        TryDelete(indexPath);
        log.LogInfo($"put back {n} original mod{(n == 1 ? "" : "s")}" + (off ? "; AutoFix is off now (Enabled in rounds-port.autofix.cfg)" : ": this is the old game build"));
    }

    // The file on disk is our fixed copy, not the original.
    static bool IsCopy(Entry e) => e.Orig.Length > 0 && e.Sha != e.Orig;

    // Every *.dll under plugins (paths relative to it, sorted). Also finishes a replace that was interrupted (see Replace).
    List<(string rel, FileInfo fi)> PluginFiles()
    {
        var files = new List<(string, FileInfo)>();
        if (!Directory.Exists(settings.Plugins)) return files;
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
            files.Add((f.Substring(settings.Plugins.Length).TrimStart('/', '\\'), new FileInfo(f)));
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return files;
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
