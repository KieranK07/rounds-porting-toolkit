"""The Gale fork's ROUNDS layer (gale-mac/src-tauri/src/rounds.rs, update_profile) in Python, for the Windows bench
where there's no Rust toolchain. Same data: gale-mac/src-tauri/resources/rounds.

    python layer.py <profile-dir>

ponytail: a copy of rounds.rs's logic; if rounds.rs changes, change this too (or build gale-mac on Windows).
"""
import bz2, hashlib, io, os, shutil, struct, sys, urllib.request

# a Gale fork checkout next to this repo (ROUNDS-modfix/gale-mac), or GALE_DIR
GALE = os.environ.get("GALE_DIR", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "gale-mac"))
RES = os.path.join(GALE, "src-tauri", "resources", "rounds")
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "store", "bknibb")

UNBOUNDLIB = ("UnboundLib.dll", "https://github.com/Bknibb/UnboundLib/releases/download/v4.2.5/UnboundLib.dll", "3411ae8451f7ad2bc7a4bf1b2e5fe21a4581afff44619f8a34b03430f3d4f408")
OCTOKIT = ("Octokit.dll", "https://github.com/Bknibb/UnboundLib/releases/download/v4.2.5/Octokit.dll", "6a48642d6ae464b43a6cb50292618af6c73ada8d726644e50d4fa44a1783f638")
MMHOOK = ("MMHOOK_Assembly-CSharp.dll", "https://github.com/Bknibb/UnboundLib/releases/download/v4.2.5/MMHOOK_Assembly-CSharp.dll", "926b53b329d94f6a8842e6d51ca17ff96f081df59695d7862845d5ccce9e5a62")
RWF = ("RoundsWithFriends.dll", "https://github.com/Bknibb/RoundsWithFriends/releases/download/v3.0.10/RoundsWithFriends.dll", "1bd4d5aa47de0e04661710a77bb0b5f1214dac4b3baabc9364b3418ecbc8ab61")
LIBRARIES = [(UNBOUNDLIB, [OCTOKIT]), (MMHOOK, []), (RWF, [])]
ODIN = "BepInEx/plugins/OdinSerializer"
PROFILE_FILES = [
    ("BepInEx/patchers/rounds-port-AutoFix", "rounds-port.AutoFix.dll", "rounds-port.AutoFix.dll"),
    ("BepInEx/plugins/rounds-port-Runtime", "rounds-port.Runtime.dll", "rounds-port.Runtime.dll"),
    (ODIN, "Sirenix.Serialization.dll", "odin/Sirenix.Serialization.dll"),
    (ODIN, "Sirenix.Serialization.Config.dll", "odin/Sirenix.Serialization.Config.dll"),
    (ODIN, "Sirenix.Utilities.dll", "odin/Sirenix.Utilities.dll"),
    (ODIN, "LICENSE.txt", "odin/Sirenix-OdinSerializer-LICENSE.txt"),
]


def sha(b): return hashlib.sha256(b).hexdigest()
def read(p): return open(p, "rb").read()


def replace(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".gale-tmp"
    open(tmp, "wb").write(data)
    os.replace(tmp, path)  # never write through a hard link


def fetch(dl):
    name, url, want = dl
    p = os.path.join(CACHE, want)
    if os.path.exists(p) and sha(read(p)) == want:
        return read(p)
    data = urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "rounds-bench"})).read()
    assert sha(data) == want, f"{url}: wrong sha"
    os.makedirs(CACHE, exist_ok=True)
    open(p, "wb").write(data)
    return data


def bspatch(old, patch):
    """BSDIFF40 (bzip2)."""
    assert patch[:8] == b"BSDIFF40"
    def off(b):
        x = struct.unpack("<Q", b)[0]
        return -(x & (1 << 63) - 1) if x >> 63 else x
    clen, dlen, nsize = off(patch[8:16]), off(patch[16:24]), off(patch[24:32])
    ctrl = io.BytesIO(bz2.decompress(patch[32:32 + clen]))
    diff = io.BytesIO(bz2.decompress(patch[32 + clen:32 + clen + dlen]))
    extra = io.BytesIO(bz2.decompress(patch[32 + clen + dlen:]))
    new = bytearray(); o = 0
    while len(new) < nsize:
        x, y, z = (off(ctrl.read(8)) for _ in range(3))
        d = diff.read(x)
        new += bytes((d[i] + (old[o + i] if 0 <= o + i < len(old) else 0)) & 255 for i in range(x))
        new += extra.read(y); o += x + z
    return bytes(new)


def patches():
    out = []
    for line in open(os.path.join(RES, "patches.tsv"), encoding="utf-8").read().splitlines():
        c = line.split("\t")
        if len(c) >= 5 and c[4] == "macos" and sys.platform != "darwin": continue   # macOS only
        if len(c) >= 4:
            out.append({"name": c[0].rsplit("/", 1)[-1], "before": c[1], "after": c[2], "patch": c[3]})
    return out


def files_under(d):
    return [os.path.join(r, f) for r, _, fs in os.walk(d) for f in fs]


def update(profile):
    plugins = os.path.join(profile, "BepInEx", "plugins")
    pts = patches()
    old = {l.split("\t")[1] for l in open(os.path.join(RES, "old-libraries.tsv"), encoding="utf-8").read().splitlines() if "\t" in l}
    changed = []
    files = files_under(plugins)
    for port, withs in LIBRARIES:
        ours = {port[2]} | {p["after"] for p in pts if p["before"] == port[2]}
        olds, own, others = [], [], []
        for f in files:
            if os.path.basename(f).lower() != port[0].lower(): continue
            h = sha(read(f))
            (olds if h in old else own if h in ours else others).append(f)
        replace_p, remove, port_dir = None, [], None
        if others: remove = olds + own
        elif own: port_dir, remove = os.path.dirname(own[0]), olds + own[1:]
        elif olds: replace_p, port_dir, remove = olds[0], os.path.dirname(olds[0]), olds[1:]
        for f in remove: os.remove(f); changed.append(f"removed {f}")
        if replace_p: replace(replace_p, fetch(port)); changed.append(f"{replace_p} -> Bknibb")
        if port_dir:
            for w in withs:
                p = os.path.join(port_dir, w[0])
                if not (os.path.exists(p) and sha(read(p)) == w[2]): replace(p, fetch(w)); changed.append(p)
    for d, name, src in PROFILE_FILES:
        own_dir = os.path.normpath(os.path.join(profile, d)); dest = os.path.join(own_dir, name)
        elsewhere = any(os.path.basename(f).lower() == name.lower() and not os.path.normpath(f).startswith(own_dir)
                        for sub in ("BepInEx/plugins", "BepInEx/patchers") for f in files_under(os.path.join(profile, sub)))
        if elsewhere:
            if os.path.exists(dest): os.remove(dest); changed.append(f"removed {dest}: a package provides it")
            continue
        data = read(os.path.join(RES, "files", src))
        cur = sha(read(dest)) if os.path.exists(dest) else None
        if cur not in ({sha(data)} | {p["after"] for p in pts if p["before"] == sha(data)}):
            replace(dest, data); changed.append(dest)
    for f in files_under(plugins):
        cands = [p for p in pts if p["name"] == os.path.basename(f)]
        if not cands: continue
        src = read(f); h = sha(src)
        for p in cands:
            if p["before"] == h:
                out = bspatch(src, read(os.path.join(RES, "patches", p["patch"])))
                assert sha(out) == p["after"], f"patch result wrong for {f}"
                replace(f, out); changed.append(f"patched {f}"); break
    cfg = os.path.join(profile, "BepInEx", "config", "BepInEx.cfg")
    text = open(cfg, encoding="utf-8").read() if os.path.exists(cfg) else ""
    lines = ["HideManagerGameObject = true" if l.strip().startswith("HideManagerGameObject") else l for l in text.splitlines()]
    if not any(l.strip().startswith("HideManagerGameObject") for l in text.splitlines()):
        lines += ["", "[Chainloader]", "HideManagerGameObject = true"]
    new = "\n".join(lines) + "\n"
    if new != text: replace(cfg, new.encode()); changed.append("HideManagerGameObject = true")
    return changed


if __name__ == "__main__":
    c = update(sys.argv[1])
    print(f"{len(c)} changes"); print("\n".join("  " + x for x in c))
