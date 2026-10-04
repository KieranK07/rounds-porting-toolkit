"""Summarises one launch.sh run: which plugins loaded, what AutoFix did, and every distinct error.

    python3 analyze.py <profile> [--json out.json]
"""
import json, os, re, sys
from collections import Counter, OrderedDict

LINE = re.compile(r"^\[(\w+)\s*:\s*([^\]]+)\] ?(.*)$")


def entries(path):
    """BepInEx log entries: (level, source, text with continuation lines)."""
    cur = None
    for raw in open(path, encoding="utf-8", errors="replace"):
        raw = raw.rstrip("\n")
        m = LINE.match(raw)
        if m:
            if cur:
                yield cur
            cur = [m.group(1), m.group(2).strip(), m.group(3)]
        elif cur is not None:
            cur[2] += "\n" + raw
    if cur:
        yield cur


def frames(text, n=3):
    out = []
    for l in text.splitlines()[1:]:
        l = l.strip()
        if l.startswith("at ") or re.match(r"^[\w.`<>]+[:.][\w<>`]+ ?\(", l):
            out.append(l[:160])
        if len(out) == n:
            break
    return out


def main():
    prof = sys.argv[1]
    log = os.path.join(prof, "run", "LogOutput.log")
    if not os.path.exists(log):
        sys.exit(f"no log at {log}")
    loading, failed, autofix = [], [], []
    errors = OrderedDict()
    for level, source, text in entries(log):
        first = text.splitlines()[0] if text else ""
        if source == "BepInEx" and first.startswith("Loading ["):
            loading.append(first[len("Loading ["):-1])
        if source == "BepInEx" and level in ("Error", "Warning", "Fatal") and ("Could not load" in first or "skipping" in first.lower() or "incompatib" in first.lower() or "missing dependenc" in first.lower()):
            failed.append(first)
        if source == "rounds-port" and (re.match(r"(checking|fixed|left|\d+ mods:|this is the old)", first) or level != "Info"):
            autofix.append(f"{level}: {first}")
        if level in ("Error", "Fatal"):
            key = (source, first[:200])
            if key not in errors:
                errors[key] = {"source": source, "level": level, "message": first[:400], "frames": frames(text), "count": 0}
            errors[key]["count"] += 1

    print(f"plugins loaded: {len(loading)}")
    if failed:
        print(f"\nplugins not loaded ({len(failed)}):")
        for f in failed:
            print("  " + f)
    print(f"\nAutoFix ({len(autofix)} lines):")
    for a in autofix:
        if not a.startswith("Info: fixed"):
            print("  " + a[:300])
    print(f"  ({sum(a.startswith('Info: fixed') for a in autofix)} 'fixed' lines)")
    print(f"\ndistinct errors: {len(errors)} ({sum(e['count'] for e in errors.values())} total)")
    by_source = Counter(e["source"] for e in errors.values())
    for (source, _), e in errors.items():
        print(f"\n  [{e['source']}] x{e['count']} {e['message']}")
        for fr in e["frames"]:
            print(f"      {fr}")
    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
        json.dump({"loaded": loading, "failed": failed, "autofix": autofix, "errors": list(errors.values())}, open(out, "w"), indent=1)


main()
