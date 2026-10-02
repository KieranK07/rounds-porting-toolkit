using System.IO.Compression;
using System.Security.Cryptography;

// Reference DLLs rounds-port needs to read mods, fetched once when the game doesn't have them: BepInEx's core (every
// mod references BepInEx and Harmony) and Bknibb's UnboundLib 4. Only used for reading; nothing is installed into the
// game. Downloaded from the official GitHub releases, checked against fixed SHA-256s, cached per user.
static class Deps
{
    const string BepInExZip = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";
    const string BepInExZipSha = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4";
    static readonly (string name, string url, string sha)[] UnboundLibFiles =
    {
        ("UnboundLib.dll", "https://github.com/Bknibb/UnboundLib/releases/download/v4.2.5/UnboundLib.dll",
            "3411ae8451f7ad2bc7a4bf1b2e5fe21a4581afff44619f8a34b03430f3d4f408"),
        ("MMHOOK_Assembly-CSharp.dll", "https://github.com/Bknibb/UnboundLib/releases/download/v4.2.5/MMHOOK_Assembly-CSharp.dll",
            "926b53b329d94f6a8842e6d51ca17ff96f081df59695d7862845d5ccce9e5a62"),
    };

    public static string Cache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rounds-port");

    // BepInEx/core of BepInEx 5.4.23.5, or null (with a message) if it can't be fetched.
    public static string? BepInExCore()
    {
        var dir = Path.Combine(Cache, "BepInEx-5.4.23.5", "core");
        if (File.Exists(Path.Combine(dir, "BepInEx.dll")) && File.Exists(Path.Combine(dir, "0Harmony.dll"))) return dir;
        Out.Note($"BepInEx isn't installed in the game; downloading its core DLLs to read mods with (once, to {dir})");
        try
        {
            var bytes = Download(BepInExZip, BepInExZipSha);
            Directory.CreateDirectory(dir);
            using var zip = new ZipArchive(new MemoryStream(bytes));
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (name.StartsWith("BepInEx/core/") && name.EndsWith(".dll")) e.ExtractToFile(Path.Combine(dir, e.Name), true);
            }
            return dir;
        }
        catch (Exception e)
        {
            Out.Warn($"couldn't get BepInEx ({e.Message}). Install BepInEx 5 in the game, or pass --ref <a BepInEx/core folder>.");
            return null;
        }
    }

    static readonly (string name, string url, string sha)[] RwfFiles =
    {
        ("RoundsWithFriends.dll", "https://github.com/Bknibb/RoundsWithFriends/releases/download/v3.0.10/RoundsWithFriends.dll",
            "1bd4d5aa47de0e04661710a77bb0b5f1214dac4b3baabc9364b3418ecbc8ab61"),
    };

    // Bknibb's UnboundLib 4.2.5 (UnboundLib.dll + MMHOOK_Assembly-CSharp.dll), or null (with a message).
    public static string? UnboundLib4() => Files("UnboundLib-4.2.5", UnboundLibFiles, "UnboundLib 4", "Bknibb's 4.2.5", "Bknibb's UnboundLib 4");

    // Bknibb's RoundsWithFriends 3.0.10, or null (with a message). Only `sweep` asks for it: many mods use RWF.
    public static string? RoundsWithFriends3() => Files("RoundsWithFriends-3.0.10", RwfFiles, "RoundsWithFriends 3", "Bknibb's 3.0.10", "Bknibb's RoundsWithFriends 3");

    static string? Files(string folder, (string name, string url, string sha)[] files, string what, string which, string refHint)
    {
        var dir = Path.Combine(Cache, folder);
        if (files.All(f => File.Exists(Path.Combine(dir, f.name)))) return dir;
        Out.Note($"{what} isn't in the game; downloading {which} to check mods against (once, to {dir})");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in files) File.WriteAllBytes(Path.Combine(dir, f.name), Download(f.url, f.sha));
            return dir;
        }
        catch (Exception e)
        {
            Out.Warn($"couldn't get {what} ({e.Message}). Pass --ref <folder with {refHint}>.");
            return null;
        }
    }

    // A Thunderstore package's DLLs (flattened) in the cache, downloaded once; null when it has none (modpacks, maps).
    // With a sha, the zip is checked against it.
    public static string? ThunderstorePackage(string name, string version, string? sha = null)
    {
        var dir = Path.Combine(Cache, "thunderstore", $"{name}-{version}");
        if (File.Exists(Path.Combine(dir, ".nodll"))) return null;
        if (File.Exists(Path.Combine(dir, ".complete"))) return dir;
        var dash = name.IndexOf('-');
        if (dash < 1) throw new UserError($"{name}: not a Thunderstore package name (Namespace-Name)");
        Out.Note($"downloading {name} {version} from Thunderstore (once, to {dir})");
        var zip = Download($"https://thunderstore.io/package/download/{name[..dash]}/{name[(dash + 1)..]}/{version}/", sha);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        using var z = new ZipArchive(new MemoryStream(zip));
        var dlls = z.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var e in dlls) e.ExtractToFile(Path.Combine(dir, Path.GetFileName(e.FullName.Replace('\\', '/'))), true);
        File.WriteAllText(Path.Combine(dir, dlls.Count == 0 ? ".nodll" : ".complete"), "");
        return dlls.Count == 0 ? null : dir;
    }

    // Libraries mods use, by assembly name -> (Thunderstore package, version, zip sha256). Generated from the 100
    // most-downloaded mods and the packages other mods depend on (thunderstore-libs.tsv).
    public static readonly Dictionary<string, (string package, string version, string sha)> Libraries = LoadLibraries();

    static Dictionary<string, (string, string, string)> LoadLibraries()
    {
        using var s = typeof(Deps).Assembly.GetManifestResourceStream("thunderstore-libs.tsv")!;
        using var r = new StreamReader(s);
        var map = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        for (string? l; (l = r.ReadLine()) != null;)
            if (l.Length > 0 && !l.StartsWith('#') && l.Split('\t') is { Length: 4 } p) map[p[0]] = (p[1], p[2], p[3]);
        return map;
    }

    public static byte[] Download(string url, string? sha)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("rounds-port");
        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        if (sha != null && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != sha)
            throw new Exception($"checksum mismatch for {url}");
        return bytes;
    }
}
