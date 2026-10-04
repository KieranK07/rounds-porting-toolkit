"""Copies the curated patch table (the Gale fork's resources/rounds, from rounds-mac-modpack) into src/AutoFix/curated,
so AutoFix applies the same hand-made fixes for players on other mod managers.

    python scripts/curated.py [<gale-mac>/src-tauri/resources/rounds]

.NET Framework has no bzip2, so each BSDIFF40 patch is rewritten as BSDIFFDF: the same layout with raw deflate blocks
(Curated.Bspatch reads it). Only .dll patches: the .pdb/.mdb ones only add line numbers to stack traces.
"""
import bz2, hashlib, os, struct, sys, zlib

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "..", "gale-mac", "src-tauri", "resources", "rounds")
OUT = os.path.join(HERE, "..", "src", "AutoFix", "curated")


def off(b):
    x = struct.unpack("<q", b)[0]
    return -(x & (1 << 63) - 1) if x < 0 else x


def blocks(patch):
    assert patch[:8] == b"BSDIFF40"
    clen, dlen = off(patch[8:16]), off(patch[16:24])
    return off(patch[24:32]), [patch[32:32 + clen], patch[32 + clen:32 + clen + dlen], patch[32 + clen + dlen:]]


def deflate(b):
    c = zlib.compressobj(9, zlib.DEFLATED, -15)
    return c.compress(b) + c.flush()


def bspatch(old, size, ctrl, diff, extra):
    """the reference algorithm, to check the rewritten patch gives the same file"""
    new, o, n, ci, di, ei = bytearray(size), 0, 0, 0, 0, 0
    while n < size:
        add, copy, seek = (off(ctrl[ci + 8 * k:ci + 8 * k + 8]) for k in range(3)); ci += 24
        for i in range(add):
            new[n + i] = (diff[di + i] + (old[o + i] if 0 <= o + i < len(old) else 0)) & 255
        di += add; n += add; o += add
        new[n:n + copy] = extra[ei:ei + copy]; ei += copy; n += copy; o += seek
    return bytes(new)


def main():
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT): os.remove(os.path.join(OUT, f))
    rows, done = [], {}
    for line in open(os.path.join(SRC, "patches.tsv"), encoding="utf-8"):
        path, before, after, name, *only = line.rstrip("\n").split("\t")
        if not path.lower().endswith(".dll"): continue
        out = name.replace(".bsdiff", ".bsdf")
        if out not in done:
            size, raw = blocks(open(os.path.join(SRC, "patches", name), "rb").read())
            raw = [bz2.decompress(b) for b in raw]
            packed = [deflate(b) for b in raw]
            data = b"BSDIFFDF" + struct.pack("<qqq", len(packed[0]), len(packed[1]), size) + b"".join(packed)
            open(os.path.join(OUT, out), "wb").write(data)
            done[out] = (size, raw)
        rows.append("\t".join((path.rsplit("/", 1)[-1], before, after, out, *only)))
    open(os.path.join(OUT, "patches.tsv"), "w", encoding="utf-8", newline="\n").write("\n".join(rows) + "\n")

    # check each rewritten patch on the file it's for, where the bench has a copy (store/ or a profile)
    store = os.path.expanduser("~/ROUNDS-modfix/port-tests/ingame")
    have = {}
    for root, _, files in os.walk(store):
        for f in files:
            if f.lower().endswith(".dll") and any(f == r.split("\t")[0] for r in rows):
                p = os.path.join(root, f)
                have.setdefault(hashlib.sha256(open(p, "rb").read()).hexdigest(), p)
    checked = 0
    for r in rows:
        name, before, after, out = r.split("\t")[:4]
        if before in have:
            size, raw = done[out]
            assert hashlib.sha256(bspatch(open(have[before], "rb").read(), size, *raw)).hexdigest() == after, name
            checked += 1
    print(f"{len(rows)} patches -> {OUT} ({sum(os.path.getsize(os.path.join(OUT, f)) for f in os.listdir(OUT))} bytes); "
          f"{checked} checked against a copy of their file")


main()
