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

    static string Cache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rounds-port");

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

    // Bknibb's UnboundLib 4.2.5 (UnboundLib.dll + MMHOOK_Assembly-CSharp.dll), or null (with a message).
    public static string? UnboundLib4()
    {
        var dir = Path.Combine(Cache, "UnboundLib-4.2.5");
        if (UnboundLibFiles.All(f => File.Exists(Path.Combine(dir, f.name)))) return dir;
        Out.Note($"UnboundLib 4 isn't in the game; downloading Bknibb's 4.2.5 to check mods against (once, to {dir})");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in UnboundLibFiles) File.WriteAllBytes(Path.Combine(dir, f.name), Download(f.url, f.sha));
            return dir;
        }
        catch (Exception e)
        {
            Out.Warn($"couldn't get UnboundLib 4 ({e.Message}). Pass --ref <folder with Bknibb's UnboundLib 4>.");
            return null;
        }
    }

    static byte[] Download(string url, string sha)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("rounds-port");
        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != sha)
            throw new Exception($"checksum mismatch for {url}");
        return bytes;
    }
}
