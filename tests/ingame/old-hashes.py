"""SHA-256 of every UnboundLib.dll, MMHOOK_Assembly-CSharp.dll and RoundsWithFriends.dll ever released in the old
Thunderstore packages (the Gale layer only replaces files it recognises from these)."""
import hashlib, io, json, os, urllib.request, zipfile
HERE = os.path.dirname(os.path.abspath(__file__))
UA = {"User-Agent": "rounds-ingame-test"}
PKGS = ["willis81808-UnboundLib", "olavim-UnboundLib", "willis81808-MMHook", "olavim-RoundsWithFriends",
        "KronosSolutions-UpdatedRoundsWithFriends"]
NAMES = {"unboundlib.dll", "mmhook_assembly-csharp.dll", "roundswithfriends.dll"}
pk = {p["full_name"]: p for p in json.load(open(os.path.join(HERE, "store/packages.json")))}
out = {}
for name in PKGS:
    for v in pk[name]["versions"]:
        path = os.path.join(HERE, "store/old", f"{name}-{v['version_number']}.zip")
        if not os.path.exists(path):
            with urllib.request.urlopen(urllib.request.Request(v["download_url"], headers=UA)) as r:
                open(path, "wb").write(r.read())
        with zipfile.ZipFile(path) as z:
            for info in z.infolist():
                fn = info.filename.replace("\\", "/").rsplit("/", 1)[-1]
                if fn.lower() in NAMES:
                    out.setdefault(fn, {})[hashlib.sha256(z.read(info)).hexdigest()] = f"{name} {v['version_number']}"
for fn, hs in sorted(out.items()):
    print(f"# {fn}: {len(hs)}")
    for h, src in sorted(hs.items(), key=lambda x: x[1]):
        print(f"{fn}\t{h}\t{src}")
