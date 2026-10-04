"""The Thunderstore package: thunderstore/ (manifest, README, icon) + AutoFix + Runtime + Odin Serializer -> dist/.

    python scripts/package.py [version]

Build AutoFix and the Runtime first (Release). Layout as r2modman/Gale install it: patchers/ -> BepInEx/patchers/<pkg>,
plugins/ -> BepInEx/plugins/<pkg>.
"""
import json, os, sys, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
TS = os.path.join(ROOT, "thunderstore")
ODIN = os.path.join(ROOT, "odin")
BIN = lambda p, f: os.path.join(ROOT, "src", p, "bin", "Release", "net472", f)

FILES = {
    "patchers/rounds-port.AutoFix.dll": BIN("AutoFix", "rounds-port.AutoFix.dll"),
    "plugins/rounds-port.Runtime.dll": BIN("Runtime", "rounds-port.Runtime.dll"),
    "plugins/OdinSerializer/Sirenix.Serialization.dll": os.path.join(ODIN, "Sirenix.Serialization.dll"),
    "plugins/OdinSerializer/Sirenix.Serialization.Config.dll": os.path.join(ODIN, "Sirenix.Serialization.Config.dll"),
    "plugins/OdinSerializer/Sirenix.Utilities.dll": os.path.join(ODIN, "Sirenix.Utilities.dll"),
    "plugins/OdinSerializer/LICENSE.txt": os.path.join(ODIN, "Sirenix-OdinSerializer-LICENSE.txt"),
    "README.md": os.path.join(TS, "README.md"),
    "CHANGELOG.md": os.path.join(TS, "CHANGELOG.md"),
    "icon.png": os.path.join(TS, "icon.png"),
}


def main():
    manifest = json.load(open(os.path.join(TS, "manifest.json"), encoding="utf-8"))
    if len(sys.argv) > 1: manifest["version_number"] = sys.argv[1]
    assert len(manifest["description"]) <= 250, "Thunderstore: description is 250 characters at most"
    out = os.path.join(ROOT, "dist", f"{manifest['name']}-{manifest['version_number']}.zip")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", json.dumps(manifest, indent=2) + "\n")
        for arc, src in FILES.items():
            z.write(src, arc)
    print(f"{out} ({os.path.getsize(out)} bytes)")


main()
