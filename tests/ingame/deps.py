"""Dependencies a mod needs but doesn't declare on Thunderstore, from one run's log.

    python3 deps.py <profile>        prints the packages to add, one per line ("?" lines: not found)

Looks at "missing dependencies: <plugin GUID>" (BepInEx) and "Could not load file or assembly '<Name>'"
(Mono). A package provides a GUID when one of its DLLs contains the GUID string; it provides an assembly
when it ships <Name>.dll. Candidates are packages whose name looks like the GUID or assembly, most
downloaded first; each is downloaded and checked.
"""
import json, os, re, sys, urllib.request, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
STORE = os.path.join(HERE, "store")
UA = {"User-Agent": "rounds-ingame-test"}


def wanted(profile):
    log = open(os.path.join(profile, "run", "LogOutput.log"), encoding="utf-8", errors="replace").read()
    guids = set()
    for m in re.finditer(r"missing dependencies: (.+)", log):
        guids |= {g.strip() for g in m.group(1).split(",") if g.strip()}
    asms = set(re.findall(r"Could not load file or assembly '([^,']+)", log)) - {"UnityEditor"}
    return guids, asms


def words(s):
    return [w for w in re.split(r"[^a-z0-9]+", s.lower()) if len(w) > 2 and w not in {"rounds", "plugins", "plugin", "com", "mod", "mods"}]


def candidates(key, pkgs):
    ws = words(key)
    scored = []
    for p in pkgs:
        if p["is_deprecated"]:
            continue
        name = p["name"].lower().replace("_", "")
        hit = sum(1 for w in ws if w in name or name in w)
        if hit:
            scored.append((hit, sum(v["downloads"] for v in p["versions"]), p))
    scored.sort(key=lambda x: (-x[0], -x[1]))
    return [p for _, _, p in scored[:6]]


def provides(p, guid=None, asm=None):
    v = p["versions"][0]
    path = os.path.join(STORE, f"{p['full_name']}-{v['version_number']}.zip")
    if not os.path.exists(path):
        with urllib.request.urlopen(urllib.request.Request(v["download_url"], headers=UA)) as r:
            open(path, "wb").write(r.read())
    with zipfile.ZipFile(path) as z:
        for info in z.infolist():
            fn = info.filename.replace("\\", "/").rsplit("/", 1)[-1]
            if not fn.lower().endswith(".dll"):
                continue
            if asm and fn[:-4].lower() == asm.lower():
                return True
            if guid and guid.encode("utf-16-le") in z.read(info):
                return True
    return False


def main():
    profile = sys.argv[1]
    pkgs = json.load(open(os.path.join(STORE, "packages.json")))
    have = {l.split("\t")[0] for l in open(os.path.join(profile, "packages.txt"))}
    guids, asms = wanted(profile)
    for key, kind in [(g, "guid") for g in sorted(guids)] + [(a, "asm") for a in sorted(asms)]:
        found = None
        for p in candidates(key, pkgs):
            if p["full_name"] in have:
                continue
            if provides(p, guid=key if kind == "guid" else None, asm=key if kind == "asm" else None):
                found = p["full_name"]
                break
        print(found if found else f"? {key}")


main()
