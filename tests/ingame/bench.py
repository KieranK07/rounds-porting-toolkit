"""batches.sh + isolate.sh for Windows. Each run: build the profile, launch, and if the log names undeclared
dependencies (deps.py), rebuild with them and launch once more.

    python bench.py batches [N=10] [first=0] [last=9]   the sweep's 98 mods in batches of N
    python bench.py isolate <Author-Name> ...           each package alone: its own problem, or a clash?
"""
import json, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable


def run(*a, quiet=False):
    r = subprocess.run([PY, *a], cwd=HERE, capture_output=True, text=True)
    if not quiet: print("   " + (r.stdout.strip().splitlines() or [""])[-1])
    return r.stdout


def once(prof, make_args):
    run("make-profile.py", prof, *make_args, quiet=True)
    run("launch.py", prof, "30", "240")
    extra = [l for l in run("deps.py", prof, quiet=True).splitlines() if l and not l.startswith("?")]
    if extra:
        print("   undeclared dependencies added: " + " ".join(extra))
        run("make-profile.py", prof, *make_args, *extra, quiet=True)
        run("launch.py", prof, "30", "240")
    run("analyze.py", prof, "--json", os.path.join(prof, "run", "result.json"), quiet=True)
    r = json.load(open(os.path.join(HERE, prof, "run", "result.json")))
    errs = [e for e in r["errors"] if "shader compiler" not in e["message"] and not e["message"].startswith("Command Line")]
    print(f"   {len(r['loaded'])} plugins, {len(errs)} distinct errors")
    for e in errs: print(f"     x{e['count']} [{e['source']}] {e['message'][:160]}")


def main():
    cmd, args = sys.argv[1], sys.argv[2:]
    if cmd == "batches":
        n, first, last = (int(x) for x in (args + ["10", "0", "9"][len(args):]))
        for i in range(first, last + 1):
            print(f"== batch {i} (mods {i*n+1}-{i*n+n})", flush=True)
            once(f"profiles/b{i}", ["--top", str(n), "--skip", str(i * n)])
    else:
        for pkg in args:
            print(f"== {pkg}", flush=True)
            once(f"profiles/iso/{pkg}", [pkg])


main()
