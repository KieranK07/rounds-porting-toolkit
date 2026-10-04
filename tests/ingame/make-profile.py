"""Builds a ROUNDS mod profile from Thunderstore the way Gale/r2modman lay one out, then applies the Gale fork's
ROUNDS layer and the macOS BepInEx files, ready for launch.sh.

    python3 make-profile.py <profile-dir> <Author-Name>[ ...]      packages at their latest versions, with dependencies
    python3 make-profile.py <profile-dir> --top N [--skip K] [more]  the N most-downloaded mods with DLLs (from sweep)
    ... --package <zip>     no Gale layer: the Thunderstore package (dist/) installed as r2modman would
    ... --plain             nothing added: the mods as they are, for the old game build (old-rounds-for-mods)

Every file is a hard link into ./store, like Gale's cache.
"""
import json, os, shutil, subprocess, sys, time, urllib.request, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
STORE = os.path.join(HERE, "store")
GALE = os.environ.get("GALE_DIR", os.path.join(HERE, "..", "..", "..", "gale-mac"))   # a Gale fork checkout
SWEEP_LIST = os.path.join(HERE, "..", "sweep-packages.tsv")
UA = {"User-Agent": "rounds-ingame-test"}


def index():
    path = os.path.join(STORE, "packages.json")
    if not os.path.exists(path) or time.time() - os.path.getmtime(path) > 6 * 3600:
        os.makedirs(STORE, exist_ok=True)
        req = urllib.request.Request("https://thunderstore.io/c/rounds/api/v1/package/", headers=UA)
        with urllib.request.urlopen(req) as r, open(path, "wb") as f:
            f.write(r.read())
    return {p["full_name"]: p for p in json.load(open(path, encoding="utf-8"))}


def resolve(names, pkgs):
    """Package -> version, latest of each, dependencies included (what Gale installs)."""
    out = {}
    todo = list(names)
    while todo:
        name = todo.pop()
        if name in out:
            continue
        p = pkgs.get(name)
        if p is None:
            print(f"  not on Thunderstore: {name}")
            continue
        v = p["versions"][0]
        out[name] = v["version_number"]
        for dep in v["dependencies"]:
            todo.append(dep.rsplit("-", 1)[0])
    return out


def fetch(name, version):
    author, pkg = name.split("-", 1)
    path = os.path.join(STORE, f"{name}-{version}.zip")
    if not os.path.exists(path):
        url = f"https://thunderstore.io/package/download/{author}/{pkg}/{version}/"
        with urllib.request.urlopen(urllib.request.Request(url, headers=UA)) as r, open(path + ".part", "wb") as f:
            f.write(r.read())
        os.rename(path + ".part", path)
    return path


def target(name, path):
    """Where a file from a package zip goes in the profile."""
    parts = [p for p in path.replace("\\", "/").split("/") if p]
    if name.startswith("BepInEx-BepInExPack"):
        return "/".join(parts[1:]) if len(parts) > 1 else None
    low = [p.lower() for p in parts]
    if low[:1] == ["bepinex"]:
        parts, low = parts[1:], low[1:]
    if low[:1] == ["config"]:
        return "BepInEx/config/" + "/".join(parts[1:])
    if low[:1] == ["patchers"]:
        return f"BepInEx/patchers/{name}/" + "/".join(parts[1:])
    if low[:1] == ["plugins"]:
        parts = parts[1:]
    return f"BepInEx/plugins/{name}/" + "/".join(parts)


def build(dest, versions):
    shutil.rmtree(dest, ignore_errors=True)
    os.makedirs(dest)
    for name, version in sorted(versions.items()):
        zpath = fetch(name, version)
        unpacked = os.path.join(STORE, f"{name}-{version}")
        with zipfile.ZipFile(zpath) as z:
            for info in z.infolist():
                if info.filename.endswith(("/", "\\")):
                    continue
                rel = target(name, info.filename)
                if not rel:
                    continue
                src = os.path.join(unpacked, info.filename.replace("\\", "/"))
                if not os.path.exists(src):
                    os.makedirs(os.path.dirname(src), exist_ok=True)
                    with open(src, "wb") as f:
                        f.write(z.read(info))
                out = os.path.join(dest, rel)
                os.makedirs(os.path.dirname(out), exist_ok=True)
                if rel.startswith("BepInEx/config/"):
                    # configs are written by the game (BepInEx saves new keys): each profile gets its own copy
                    open(out, "wb").write(z.read(info))
                elif not os.path.exists(out):
                    os.link(src, out)
    with open(os.path.join(dest, "packages.txt"), "w") as f:
        f.writelines(f"{n}\t{v}\n" for n, v in sorted(versions.items()))


def prepare(dest):
    if os.name == "nt":
        # no Rust here: layer.py is rounds.rs in Python; Windows uses the Thunderstore BepInExPack as is
        sys.path.insert(0, HERE)
        import layer
        print(f"layer: {len(layer.update(dest))} changes")
        return
    # the ROUNDS layer (rounds.rs), then the macOS BepInEx core and Doorstop (macos.rs)
    env = dict(os.environ, ROUNDS_PREPARE_DIR=os.path.abspath(dest))
    r = subprocess.run(["cargo", "test", "-q", "--lib", "prepare_dir", "--", "--ignored", "--nocapture"],
                       cwd=os.path.join(GALE, "src-tauri"), env=env, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(r.stdout[-3000:] + r.stderr[-3000:])
    mac = os.path.join(GALE, "src-tauri/resources/macos")
    for root, _, files in os.walk(mac):
        for fn in files:
            src = os.path.join(root, fn)
            out = os.path.join(dest, os.path.relpath(src, mac))
            os.makedirs(os.path.dirname(out), exist_ok=True)
            if os.path.exists(out):
                os.remove(out)
            shutil.copyfile(src, out)


def install(dest, zpath):
    """a local package zip, as r2modman installs one"""
    with zipfile.ZipFile(zpath) as z:
        name = "local-" + json.loads(z.read("manifest.json"))["name"]
        for info in z.infolist():
            if info.filename.endswith("/"): continue
            out = os.path.join(dest, target(name, info.filename))
            os.makedirs(os.path.dirname(out), exist_ok=True)
            open(out, "wb").write(z.read(info))
    print(f"installed {zpath} as {name}")


def main():
    dest, args = sys.argv[1], sys.argv[2:]
    pkgs = index()
    n = skip = 0
    package, plain = None, False
    names = []
    while args:
        a = args.pop(0)
        if a == "--top": n = int(args.pop(0))
        elif a == "--skip": skip = int(args.pop(0))
        elif a == "--package": package = args.pop(0)
        elif a == "--plain": plain = True
        else: names.append(a)
    if n:
        top = [l.split("\t")[0] for l in open(SWEEP_LIST) if l.strip() and not l.startswith("#")]
        names = top[skip:skip + n] + names
    versions = resolve(names + ["BepInEx-BepInExPack_ROUNDS"], pkgs)  # every real profile has the loader
    print(f"{len(names)} asked, {len(versions)} with dependencies")
    build(dest, versions)
    if package: install(dest, package)
    elif not plain: prepare(dest)
    print(f"profile ready: {dest}")


main()
