"""The same card on both game builds, side by side: each offer screenshot (the pilot selects the card first, so it is
face up) from profiles/o<i> (old-rounds-for-mods beta, mods untouched) next to profiles/b<i> (2025 + DuctTape).

    python compare-shots.py <out.html> 7 8 0:run-gallery

Writes one page with the pairs as JPEG data URIs.
"""
import base64, glob, io, os, re, sys
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))


def shots(prof, run="run"):
    out = {}
    for p in sorted(glob.glob(os.path.join(HERE, "profiles", prof, run, "pilot", "shots", "*_offer_*.png"))):
        out.setdefault(re.sub(r"^\d+_offer_", "", os.path.basename(p))[:-4], p)   # first offer of each card
    return out


def jpeg(path, w=800):
    im = Image.open(path).convert("RGB")
    im = im.resize((w, w * im.height // im.width))
    b = io.BytesIO(); im.save(b, "JPEG", quality=74)
    return "data:image/jpeg;base64," + base64.b64encode(b.getvalue()).decode()


def main():
    out, batches = sys.argv[1], sys.argv[2:]
    rows = []
    for arg in batches:
        b, _, run = arg.partition(":")   # "0:run-gallery": the 2025 side from that run folder
        old, new = shots("o" + b), shots("b" + b, run or "run")
        for name in [n for n in old if n in new]:
            rows.append((b, name.strip("_").replace("__", " ").replace("-", " "), jpeg(old[name]), jpeg(new[name])))
    print(f"{len(rows)} cards on both builds")
    figs = "\n".join(f'<section><h2>{n} <small>batch {b}</small></h2><div class="pair"><figure><img src="{o}" alt="{n} on the old beta">'
                     f'<figcaption>Old beta</figcaption></figure><figure><img src="{w}" alt="{n} on 2025 with DuctTape">'
                     f'<figcaption>2025 + DuctTape</figcaption></figure></div></section>' for b, n, o, w in rows)
    open(out, "w", encoding="utf-8").write(open(os.path.join(HERE, "compare-template.html"), encoding="utf-8").read()
                                           .replace("{{COUNT}}", str(len(rows))).replace("{{ROWS}}", figs))


main()
