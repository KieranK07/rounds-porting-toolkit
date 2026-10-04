using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
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
            log.LogInfo($"{Short(path)}: Bknibb's port in place of the old release");
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
            log.LogInfo($"{Short(path)}: curated patch ({patch.Resource.Substring("curated/".Length)})");
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

    // <package folder>/<file>, as AutoFix's other lines name plugins
    static string Short(string path) => Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path);

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
        // Mono has no TLS this early in the game's start (the engine brings it), so .NET's own HTTPS usually fails here.
        // WinHTTP is Windows' own (and Wine's, under Proton); curl is macOS's and Linux's.
        byte[]? data = null;
        var tried = new List<string>();
        foreach (var (how, get) in new (string, Func<byte[]>)[] { (".NET", () => DotNet(p.Url)), ("WinHTTP", () => WinHttp.Get(p.Url)), ("curl", () => Curl(p.Url, dest + ".rptmp")) })
        {
            try
            {
                data = get();
                if (Sha(data) == p.Sha) { log.LogInfo($"downloaded Bknibb's {p.File} ({how})"); break; }
                tried.Add($"{how}: not the file it should be");
            }
            catch (Exception e) { tried.Add($"{how}: {e.Message}"); }
            data = null;
        }
        if (data == null) throw new IOException(string.Join("; ", tried));
        File.WriteAllBytes(dest + ".rptmp", data);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(dest + ".rptmp", dest);
        return dest;
    }

    static byte[] DotNet(string url)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "rounds-port";
        req.Timeout = req.ReadWriteTimeout = 20000;
        using var res = req.GetResponse();
        using var s = res.GetResponseStream()!;
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static byte[] Curl(string url, string tmp)
    {
        try
        {
            using var curl = Process.Start(new ProcessStartInfo("curl", $"-fsSL --connect-timeout 10 --max-time 40 -o \"{tmp}\" \"{url}\"")
                { UseShellExecute = false, CreateNoWindow = true })!;
            if (!curl.WaitForExit(45000)) { try { curl.Kill(); } catch { } throw new IOException("timed out"); }
            if (curl.ExitCode != 0) throw new IOException($"exit code {curl.ExitCode}");
            return File.ReadAllBytes(tmp);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // WinHTTP from P/Invoke: HTTPS through the OS, follows GitHub's redirect to its download host.
    static class WinHttp
    {
        [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr WinHttpOpen(string agent, uint access, string? proxy, string? bypass, uint flags);
        [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr WinHttpConnect(IntPtr session, string host, ushort port, uint reserved);
        [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr WinHttpOpenRequest(IntPtr connect, string verb, string path, string? version, string? referrer, IntPtr accept, uint flags);
        [DllImport("winhttp.dll", SetLastError = true)] static extern bool WinHttpSetTimeouts(IntPtr h, int resolve, int connect, int send, int receive);
        [DllImport("winhttp.dll", SetLastError = true)] static extern bool WinHttpSendRequest(IntPtr request, IntPtr headers, uint headersLength, IntPtr optional, uint optionalLength, uint totalLength, UIntPtr context);
        [DllImport("winhttp.dll", SetLastError = true)] static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);
        [DllImport("winhttp.dll", SetLastError = true)] static extern bool WinHttpQueryHeaders(IntPtr request, uint info, IntPtr name, ref uint buffer, ref uint length, IntPtr index);
        [DllImport("winhttp.dll", SetLastError = true)] static extern bool WinHttpQueryDataAvailable(IntPtr request, out uint available);
        [DllImport("winhttp.dll", SetLastError = true)] static extern bool WinHttpReadData(IntPtr request, byte[] buffer, uint toRead, out uint read);
        [DllImport("winhttp.dll")] static extern bool WinHttpCloseHandle(IntPtr h);

        public static byte[] Get(string url)
        {
            var u = new Uri(url);
            IntPtr s = IntPtr.Zero, c = IntPtr.Zero, r = IntPtr.Zero;
            try
            {
                s = WinHttpOpen("rounds-port", 0, null, null, 0);   // 0: the system's proxy settings
                if (s == IntPtr.Zero) throw new IOException($"WinHttpOpen: error {Marshal.GetLastWin32Error()}");
                WinHttpSetTimeouts(s, 10000, 10000, 20000, 20000);
                c = WinHttpConnect(s, u.Host, (ushort)u.Port, 0);
                if (c != IntPtr.Zero) r = WinHttpOpenRequest(c, "GET", u.PathAndQuery, null, null, IntPtr.Zero, 0x00800000);   // WINHTTP_FLAG_SECURE
                if (r == IntPtr.Zero || !WinHttpSendRequest(r, IntPtr.Zero, 0, IntPtr.Zero, 0, 0, UIntPtr.Zero) || !WinHttpReceiveResponse(r, IntPtr.Zero))
                    throw new IOException($"error {Marshal.GetLastWin32Error()}");
                uint status = 0, len = 4;
                WinHttpQueryHeaders(r, 19 | 0x20000000, IntPtr.Zero, ref status, ref len, IntPtr.Zero);   // STATUS_CODE | FLAG_NUMBER
                if (status != 200) throw new IOException($"HTTP {status}");
                var ms = new MemoryStream();
                var buf = new byte[65536];
                while (WinHttpQueryDataAvailable(r, out var n) && n > 0)
                {
                    if (!WinHttpReadData(r, buf, Math.Min(n, (uint)buf.Length), out var read) || read == 0) break;
                    ms.Write(buf, 0, (int)read);
                }
                return ms.ToArray();
            }
            finally { foreach (var h in new[] { r, c, s }) if (h != IntPtr.Zero) WinHttpCloseHandle(h); }
        }
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
