"""The same mods on both game builds: profiles/b<i> (2025 game + Rounds Port) against profiles/o<i> (the
old-rounds-for-mods beta, mods untouched). An error source only on 2025 is a port problem until explained.

    python oldnew.py [0 1 ...]
"""
import os, re, sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
P = lambda name, *rest: os.path.join(HERE, "profiles", name, *rest)


def rows(path):
    if not os.path.exists(path): return []
    return [l.rstrip("\r\n").split("\t") for l in open(path, encoding="utf-8", errors="replace")][1:]


def run(prof):
    cards = rows(P(prof, "run", "pilot", "cards.tsv"))
    picks = sum(1 for c in cards if len(c) > 1 and not c[1].startswith("(not offered"))
    errs = {}
    for e in rows(P(prof, "run", "pilot", "errors.tsv")):
        if len(e) == 2: errs.setdefault(e[0].replace(" (patched)", ""), e[1])
    log = P(prof, "run", "LogOutput.log")
    text = open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    loaded = len(re.findall(r"\] Loading \[", text))
    unloaded = re.findall(r"\[Error  :   BepInEx\] (Could not load \[[^\]]+\])", text)
    pickers = Counter(re.findall(r"pilot: picking .+? as player \d+: (active \w+, dead \w+)", text))
    return picks, errs, loaded, unloaded, pickers


def sites(prof):
    """error count per (first stack frame, message) in the whole log"""
    c = Counter()
    log = P(prof, "run", "LogOutput.log")
    if not os.path.exists(log): return c
    lines = open(log, encoding="utf-8", errors="replace").read().splitlines()
    for i, l in enumerate(lines):
        if not l.startswith("[Error  : Unity Log]") or "Exception" not in l: continue
        for f in lines[i + 1:i + 6]:
            f = f.strip()
            if f and not f.startswith(("Stack trace", "Rethrow", "System.", "Parameter", "UnityEngine.Debug", "HarmonyLib.")):
                c[re.sub(r" \(.*", "", f)[:90] + "  | " + l[21:70].strip()] += 1; break
    return c


def main():
    ids = sys.argv[1:] or [str(i) for i in range(10)]
    for i in ids:
        if not os.path.exists(P("o" + i, "run", "pilot", "cards.tsv")):
            print(f"b{i}: no old-game run yet"); continue
        np, ne, nl, nu, _ = run("b" + i)
        op, oe, ol, ou, pick = run("o" + i)
        print(f"== batch {i}: picks old {op} / 2025 {np}; plugins loaded old {ol} / 2025 {nl}")
        if set(ou) != set(nu):
            for u in sorted(set(nu) - set(ou)): print(f"   not loaded on 2025 only: {u}")
            for u in sorted(set(ou) - set(nu)): print(f"   not loaded on old only: {u}")
        for s in sorted(set(ne) - set(oe)): print(f"   2025 only  {s}: {ne[s][:150]}")
        for s in sorted(set(oe) - set(ne)): print(f"   old only   {s}: {oe[s][:150]}")
        print(f"   both: {', '.join(sorted(set(ne) & set(oe))) or '-'}")
        if pick: print(f"   old-game picker at pick time: {dict(pick)}")
        ns, os_ = sites("b" + i), sites("o" + i)
        more = [(k, os_[k], ns[k]) for k in set(ns) | set(os_) if ns[k] > 3 * os_[k] + 20 or os_[k] > 3 * ns[k] + 20]
        for k, o, n in sorted(more, key=lambda x: -(x[1] + x[2]))[:12]:
            print(f"   errors old {o:6} / 2025 {n:6}  {k}")


main()
