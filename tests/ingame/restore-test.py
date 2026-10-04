"""AutoFix on a profile laid out as r2modman does (make-profile.py --package): a second start reads nothing, then
RestoreOriginals puts back the exact Thunderstore files (old libraries included), then turning it on again re-fixes.

    python restore-test.py profiles/r2m
"""
import hashlib, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import launch

prof = os.path.abspath(sys.argv[1])
plugins = os.path.join(prof, "BepInEx", "plugins")
cfg = os.path.join(prof, "BepInEx", "config", "rounds-port.autofix.cfg")
store = os.path.join(HERE, "store")
sha = lambda p: hashlib.sha256(open(p, "rb").read()).hexdigest()


def originals():
    """plugin DLL -> sha of the same file in the package zip's unpacked copy (store/<pkg>-<version>/...)"""
    out = {}
    versions = dict(l.rstrip("\n").split("\t") for l in open(os.path.join(prof, "packages.txt")))
    for pkg in os.listdir(plugins):
        src = os.path.join(store, f"{pkg}-{versions.get(pkg, '')}")
        if not os.path.isdir(src): continue
        for root, _, files in os.walk(os.path.join(plugins, pkg)):
            for f in files:
                if not f.endswith(".dll"): continue
                rel = os.path.relpath(os.path.join(root, f), os.path.join(plugins, pkg))
                cands = [os.path.join(r, f) for r, _, fs in os.walk(src) if f in fs]
                if cands: out[os.path.join(root, f)] = sha(cands[0])
    return out


def start(label):
    launch.run(prof, 5, 240)
    log = open(os.path.join(prof, "run", "LogOutput.log"), encoding="utf-8", errors="replace").read()
    lines = [l for l in log.splitlines() if ":rounds-port]" in l]
    print(f"--- {label}"); [print("  " + l[:200]) for l in lines[:8]]
    return log


def setcfg(key, value):
    text = open(cfg, encoding="utf-8").read()
    open(cfg, "w", encoding="utf-8").write(re.sub(rf"^{key} = .*$", f"{key} = {value}", text, flags=re.M))


orig = originals()
changed = [p for p, h in orig.items() if sha(p) != h]
print(f"{len(orig)} DLLs from packages, {len(changed)} differ from their package (fixed, patched or swapped)")
assert any(p.endswith("UnboundLib.dll") for p in changed), "UnboundLib wasn't swapped"
assert any(p.endswith("MMHOOK_Assembly-CSharp.dll") for p in changed), "MMHook wasn't swapped"

log = start("second start")
# only Octokit.dll, which the first start put next to the new UnboundLib, is new
assert re.search(r"checking (\d+)", log) is None or re.search(r"checking 1 new", log), "a second start re-checked DLLs"

setcfg("RestoreOriginals", "true")
start("RestoreOriginals")
still = [os.path.relpath(p, plugins) for p, h in orig.items() if sha(p) != h]
assert not still, f"not restored: {still}"
assert not os.path.exists(os.path.join(plugins, "willis81808-UnboundLib", "Octokit.dll")), "Octokit left behind"
print("every package DLL is the package's own again")

setcfg("Enabled", "true")
start("on again")
assert sum(sha(p) != h for p, h in orig.items()) == len(changed), "re-fix gave a different set of changed files"
print(f"re-fixed the same {len(changed)} files")
