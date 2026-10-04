"""All batches' errors in one list, most widespread first.   python3 aggregate.py [profiles/b*]"""
import glob, json, re, sys
from collections import defaultdict
dirs = sys.argv[1:] or sorted(glob.glob("profiles/b*"))
errs = defaultdict(lambda: {"batches": [], "count": 0, "frames": []})
autofix_left = []
for d in dirs:
    try: r = json.load(open(f"{d}/run/result.json"))
    except FileNotFoundError: continue
    for e in r["errors"]:
        if "shader compiler platform" in e["message"] or e["message"].startswith("Command Line:"): continue
        key = re.sub(r"token [0-9a-f]+", "token X", re.sub(r"\d+", "N", e["message"][:160]))
        x = errs[(e["source"], key)]
        x["batches"].append(d.split("/")[-1]); x["count"] += e["count"]; x["frames"] = x["frames"] or e["frames"]; x["message"] = e["message"]
    autofix_left += [a for a in r["autofix"] if a.startswith("Warning")]
for (src, _), x in sorted(errs.items(), key=lambda kv: (-len(kv[1]["batches"]), -kv[1]["count"])):
    print(f"[{src}] {','.join(x['batches'])} x{x['count']}: {x['message'][:300]}")
    for f in x["frames"][:3]: print(f"      {f}")
print(f"\nAutoFix warnings ({len(autofix_left)}):")
for a in autofix_left: print("  " + a[:260])
