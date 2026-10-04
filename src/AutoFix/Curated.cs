using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using Mono.Cecil;

// What the Gale fork does to a ROUNDS profile before launch (gale-mac/src-tauri/src/rounds.rs), done here so players
// on any mod manager get it: old UnboundLib 3 / MMHook / RoundsWithFriends 2 files become Bknibb's ports (downloaded
// from GitHub, pinned by SHA-256), and exact mod versions that needed hand-made fixes get their patch from
// rounds-mac-modpack (curated/, made by scripts/curated.py). AutoFix's own fix runs on the result.
// Whatever a package provides wins: an old library is left alone when any other copy of it isn't an old release.
sealed class Curated(string cache, ManualLogSource log)
{
    sealed record Port(string File, string Url, string Sha, string? With);

    const string Release = "https://github.com/Bknibb/";
    static readonly Port[] Ports =
    {
        new("UnboundLib.dll", Release + "UnboundLib/releases/download/v4.2.5/UnboundLib.dll", "3411ae8451f7ad2bc7a4bf1b2e5fe21a4581afff44619f8a34b03430f3d4f408", "Octokit.dll"),
        new("MMHOOK_Assembly-CSharp.dll", Release + "UnboundLib/releases/download/v4.2.5/MMHOOK_Assembly-CSharp.dll", "926b53b329d94f6a8842e6d51ca17ff96f081df59695d7862845d5ccce9e5a62", null),
        new("RoundsWithFriends.dll", Release + "RoundsWithFriends/releases/download/v3.0.10/RoundsWithFriends.dll", "1bd4d5aa47de0e04661710a77bb0b5f1214dac4b3baabc9364b3418ecbc8ab61", null),
    };
    static readonly Port Octokit = new("Octokit.dll", Release + "UnboundLib/releases/download/v4.2.5/Octokit.dll", "6a48642d6ae464b43a6cb50292618af6c73ada8d726644e50d4fa44a1783f638", null);

    sealed record Patch(string Name, string Before, string After, string Resource);
    static readonly Dictionary<string, Patch> Patches = ReadPatches();

    // ports this start puts in: (port, path of the downloaded file)
    readonly Dictionary<string, string> needed = new(StringComparer.OrdinalIgnoreCase);

    // Which old libraries get their port: an old release is installed and nothing else provides the library.
    // Downloads them now, before anything is changed; a failed download leaves that library as it is.
    public IEnumerable<string> Plan(IEnumerable<string> pluginFiles)
    {
        foreach (var g in pluginFiles.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var port = Ports.FirstOrDefault(p => p.File.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (port == null) continue;
            var old = g.Select(f => IsOld(f, File.ReadAllBytes(f))).ToList();
            if (!old.Contains(true) || old.Contains(false)) continue;
            try { needed[port.File] = Download(port); }
            catch (Exception e) { log.LogWarning($"couldn't download Bknibb's {port.File} ({e.Message}): the old one stays, and mods that need the new one won't load"); }
        }
        return needed.Values;
    }

    // The bytes to start from instead of a plugin's own (the same array when nothing applies).
    public byte[] Apply(string path, byte[] bytes, string sha)
    {
        var file = Path.GetFileName(path);
        var port = Ports.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (port != null && needed.TryGetValue(port.File, out var dl) && IsOld(path, bytes))
        {
            log.LogInfo($"{path}: Bknibb's port in place of the old release");
            bytes = File.ReadAllBytes(dl);
            sha = port.Sha;
            if (port.With != null)
            {
                var with = Path.Combine(Path.GetDirectoryName(path)!, port.With);
                if (!File.Exists(with) || Sha(File.ReadAllBytes(with)) != Octokit.Sha) File.Copy(Download(Octokit), with, true);
            }
        }
        if (Patches.TryGetValue(file + "\t" + sha, out var patch))
        {
            using var s = typeof(Curated).Assembly.GetManifestResourceStream(patch.Resource)!;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            var result = Bspatch(bytes, ms.ToArray());
            if (Sha(result) != patch.After) throw new InvalidDataException($"the curated patch for {file} gave the wrong file");
            log.LogInfo($"{path}: curated patch ({patch.Resource})");
            bytes = result;
        }
        return bytes;
    }

    // After an old library got its original back: the file put next to its port goes too.
    public static void Restored(string path)
    {
        var port = Ports.FirstOrDefault(p => p.File.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        if (port?.With == null) return;
        var with = Path.Combine(Path.GetDirectoryName(path)!, port.With);
        try { if (File.Exists(with) && Sha(File.ReadAllBytes(with)) == Octokit.Sha) File.Delete(with); } catch { }
    }

    static bool IsOld(string path, byte[] bytes)
    {
        try
        {
            using var m = ModuleDefinition.ReadModule(new MemoryStream(bytes));
            return m.Assembly != null && Game.OldLibrary(path, m.Assembly.Name) != null;
        }
        catch { return false; }
    }

    // <cache>/downloads/<sha256>: .NET first, then curl (Windows 10+ and macOS have it), for a Mono without TLS 1.2
    string Download(Port p)
    {
        var dest = Path.Combine(cache, "downloads", p.Sha + ".dll");
        if (File.Exists(dest) && Sha(File.ReadAllBytes(dest)) == p.Sha) return dest;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        byte[]? data = null;
        string why = "";
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var req = (HttpWebRequest)WebRequest.Create(p.Url);
            req.UserAgent = "rounds-port";
            req.Timeout = req.ReadWriteTimeout = 30000;
            using var res = req.GetResponse();
            using var s = res.GetResponseStream()!;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            data = ms.ToArray();
        }
        catch (Exception e) { why = e.Message; }
        if (data == null || Sha(data) != p.Sha)
        {
            var tmp = dest + ".rptmp";
            try
            {
                using var curl = Process.Start(new ProcessStartInfo("curl", $"-fsSL --max-time 60 -o \"{tmp}\" \"{p.Url}\"")
                    { UseShellExecute = false, CreateNoWindow = true })!;
                curl.WaitForExit(70000);
                data = File.Exists(tmp) ? File.ReadAllBytes(tmp) : null;
            }
            catch (Exception e) { why += "; curl: " + e.Message; }
            finally { try { File.Delete(tmp); } catch { } }
        }
        if (data == null || Sha(data) != p.Sha) throw new IOException(data == null ? why : "it doesn't match its SHA-256");
        File.WriteAllBytes(dest + ".rptmp", data);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(dest + ".rptmp", dest);
        return dest;
    }

    static Dictionary<string, Patch> ReadPatches()
    {
        var d = new Dictionary<string, Patch>(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(Curated).Assembly.GetManifestResourceStream("curated/patches.tsv");
        if (s == null) return d;
        foreach (var line in new StreamReader(s).ReadToEnd().Split('\n'))
        {
            var c = line.Split('\t');
            if (c.Length == 4) d[c[0] + "\t" + c[1]] = new Patch(c[0], c[1], c[2], "curated/" + c[3]);   // two mods ship the same file
        }
        return d;
    }

    // BSDIFF40 with raw deflate blocks in place of bzip2 ("BSDIFFDF", see scripts/curated.py)
    static byte[] Bspatch(byte[] old, byte[] patch)
    {
        if (Encoding.ASCII.GetString(patch, 0, 8) != "BSDIFFDF") throw new InvalidDataException("not a BSDIFFDF patch");
        long clen = Off(patch, 8), dlen = Off(patch, 16), size = Off(patch, 24);
        using var ctrl = Block(patch, 32, clen);
        using var diff = Block(patch, 32 + clen, dlen);
        using var extra = Block(patch, 32 + clen + dlen, patch.Length - 32 - clen - dlen);
        var result = new byte[size];
        var c = new byte[24];
        long o = 0, n = 0;
        while (n < size)
        {
            Fill(ctrl, c, 0, 24);
            long add = Off(c, 0), copy = Off(c, 8), seek = Off(c, 16);
            Fill(diff, result, n, add);
            for (long i = 0; i < add; i++)
                if (o + i >= 0 && o + i < old.Length) result[n + i] += old[o + i];
            n += add; o += add;
            Fill(extra, result, n, copy);
            n += copy; o += seek;
        }
        return result;
    }

    static DeflateStream Block(byte[] b, long at, long len) => new(new MemoryStream(b, (int)at, (int)len), CompressionMode.Decompress);

    static void Fill(Stream s, byte[] into, long at, long len)
    {
        while (len > 0)
        {
            int r = s.Read(into, (int)at, (int)Math.Min(len, int.MaxValue));
            if (r <= 0) throw new InvalidDataException("the patch is cut short");
            at += r; len -= r;
        }
    }

    static long Off(byte[] b, long at)
    {
        long x = BitConverter.ToInt64(b, (int)at);
        return x < 0 ? -(x & long.MaxValue) : x;
    }

    static string Sha(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
