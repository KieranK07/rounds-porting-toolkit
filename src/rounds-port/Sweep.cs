using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

// rounds-port sweep: a regression test on real mods, for working on rounds-port itself. Downloads a pinned list of
// Thunderstore packages (cached), runs scan and fix on every mod DLL in them, and prints one line per package plus a
// total. --save writes the results; --compare lists what changed against an earlier --save. Fixed DLLs come out
// byte-identical on Windows, macOS and Linux, so results saved on one machine compare on another.
static class Sweep
{
    const string Api = "https://thunderstore.io/c/rounds/api/v1/package/";
    // Not mods: mod managers and the BepInEx pack.
    static readonly string[] NotMods = { "ebkr-r2modman", "Kesomannen-GaleModManager", "BepInEx-BepInExPack_ROUNDS" };
    static string Root => Path.Combine(Deps.Cache, "sweep");

    sealed record Result(string Name, string Version, int[] Before, int[] Left, int Grade, string Hashes);

    public static int Run(string? gameDir, string list, int top, string? save, string? compare)
    {
        if (top > 0) MakeList(list, top);
        if (!File.Exists(list)) throw new UserError($"{list}: not found (make one with --top <n>)");
        var pkgs = File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t')).Select(p => (name: p[0], version: p[1])).ToList();
        Out.Line($"{pkgs.Count} packages from {list}");

        var dirs = new List<(string name, string version, string dir)>();
        foreach (var (name, version) in pkgs)
        {
            if (Fetch(name, version) is string dir) dirs.Add((name, version, dir));
            else Out.Warn($"{name} {version}: no DLLs, skipped");
        }
        // Every package's DLLs are references for every other (cards use ModdingUtils, RarityLib...), except the old
        // UnboundLib 3 and RoundsWithFriends 2: mods are checked against Bknibb's ports, as players run them.
        var refs = dirs.Where(d => !Game.OldPackages.Contains(d.name)).Select(d => d.dir).ToList();
        var game = new Game(gameDir, refs, Array.Empty<string>());
        game.EnsureRoundsWithFriends3();
        Out.Line($"game: {game.Dir}");
        Out.Line($"UnboundLib: {game.UnboundLib ?? "not found"}");
        Out.Line("");

        var scanner = new Scanner(game);
        var results = new List<Result>();
        foreach (var (name, version, dir) in dirs)
        {
            var outDir = Path.Combine(Root, "out", name);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            int[] before = new int[3], left = new int[3];
            int grade = 0;
            var hashes = new List<string>();
            foreach (var dll in Program.Expand(new() { dir }))
            {
                Ported p;
                try { p = Program.Port(dll, game, scanner, fix: true, outDir, pdb: false); }
                catch (Exception e) { Out.Error($"{name}/{Path.GetFileName(dll)}: {e.Message}"); grade = 3; continue; }
                foreach (var i in p.Before) before[(int)i.Fix]++;
                foreach (var i in p.Left) left[(int)i.Fix]++;
                grade = Math.Max(grade, Program.Grade(p.Left));
                if (p.Dest != null) hashes.Add(Path.GetFileName(dll) + "=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Dest)))[..16].ToLowerInvariant());
            }
            var r = new Result(name, version, before, left, grade, hashes.Count == 0 ? "-" : string.Join(";", hashes.OrderBy(h => h, StringComparer.Ordinal)));
            results.Add(r);
            Out.Line($"  {Label(grade)} {name} {version}: {Counts(before)} -> {(left.Sum() == 0 ? "nothing left" : Counts(left))}");
        }

        int Count(int g) => results.Count(r => r.Grade == g);
        Out.Line("");
        Out.Line($"{Count(0)} of {results.Count} with nothing left after fix; {Count(1)} only REVIEW, {Count(2)} MANUAL, {Count(3)} errors");

        if (save != null)
        {
            File.WriteAllLines(save, new[] { "# package\tversion\tbefore auto/review/manual\tleft auto/review/manual\tgrade\tfixed DLLs (sha256, 16 chars)" }
                .Concat(results.Select(r => string.Join('\t', r.Name, r.Version, string.Join('/', r.Before), string.Join('/', r.Left), r.Grade, r.Hashes))));
            Out.Line($"saved {save}");
        }
        if (compare == null) return results.Any(r => r.Grade == 3) ? 3 : 0;

        if (!File.Exists(compare)) throw new UserError($"{compare}: not found");
        var old = File.ReadAllLines(compare).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')).ToDictionary(p => p[0]);
        int changed = 0;
        Out.Line($"compared with {compare}:");
        foreach (var r in results)
        {
            if (!old.TryGetValue(r.Name, out var o)) { Out.Line($"  new      {r.Name} {r.Version}"); changed++; continue; }
            var what = new List<string>();
            if (o[1] != r.Version) what.Add($"version {o[1]} -> {r.Version}");
            if (o[4] != r.Grade.ToString()) what.Add($"{Label(int.Parse(o[4])).Trim()} -> {Label(r.Grade).Trim()}");
            if (o[3] != string.Join('/', r.Left)) what.Add($"left {o[3]} -> {string.Join('/', r.Left)}");
            if (o[5] != r.Hashes) what.Add("fixed DLL bytes changed");
            if (what.Count > 0) { Out.Line($"  changed  {r.Name}: {string.Join(", ", what)}"); changed++; }
        }
        foreach (var gone in old.Keys.Except(results.Select(r => r.Name))) { Out.Line($"  missing  {gone}"); changed++; }
        Out.Line(changed == 0 ? "  no changes" : $"  {changed} package{(changed == 1 ? "" : "s")} changed");
        return results.Any(r => r.Grade == 3) ? 3 : changed > 0 ? 1 : 0;
    }

    static string Label(int grade) => grade switch { 0 => "clean ", 1 => "REVIEW", 2 => "MANUAL", _ => "ERROR " };
    static string Counts(int[] c) => $"{c[0]} auto, {c[1]} review, {c[2]} manual";

    // The package's DLLs (flattened) in the cache, downloading it once. Null when it has no DLLs (modpacks, maps).
    static string? Fetch(string name, string version)
    {
        var dir = Path.Combine(Root, "mods", $"{name}-{version}");
        if (File.Exists(Path.Combine(dir, ".nodll"))) return null;
        if (File.Exists(Path.Combine(dir, ".complete"))) return dir;
        var dash = name.IndexOf('-');
        if (dash < 1) throw new UserError($"{name}: not a Thunderstore package name (Namespace-Name)");
        Out.Note($"downloading {name} {version}");
        var zip = Deps.Download($"https://thunderstore.io/package/download/{name[..dash]}/{name[(dash + 1)..]}/{version}/", null);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        using var z = new ZipArchive(new MemoryStream(zip));
        var dlls = z.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var e in dlls) e.ExtractToFile(Path.Combine(dir, Path.GetFileName(e.FullName.Replace('\\', '/'))), true);
        File.WriteAllText(Path.Combine(dir, dlls.Count == 0 ? ".nodll" : ".complete"), "");
        return dlls.Count == 0 ? null : dir;
    }

    // The n most-downloaded ROUNDS packages that contain DLLs, at their current versions.
    static void MakeList(string list, int n)
    {
        Out.Note("reading the Thunderstore package list");
        using var doc = JsonDocument.Parse(Deps.Download(Api, null));
        var pkgs = doc.RootElement.EnumerateArray()
            .Where(p => !p.GetProperty("is_deprecated").GetBoolean())
            .Select(p => (name: p.GetProperty("full_name").GetString()!, version: p.GetProperty("versions")[0].GetProperty("version_number").GetString()!,
                          downloads: p.GetProperty("versions").EnumerateArray().Sum(v => v.GetProperty("downloads").GetInt64())))
            .Where(p => !NotMods.Contains(p.name) && !Game.OldPackages.Contains(p.name))
            .OrderByDescending(p => p.downloads);
        var chosen = new List<string>();
        foreach (var p in pkgs)
        {
            if (chosen.Count == n) break;
            if (Fetch(p.name, p.version) != null) chosen.Add($"{p.name}\t{p.version}");
        }
        File.WriteAllLines(list, new[] { $"# rounds-port sweep list: the {n} most-downloaded Thunderstore mods with DLLs, {DateTime.UtcNow:yyyy-MM-dd}",
            "# package\tversion" }.Concat(chosen));
        Out.Line($"wrote {list}");
    }
}
