#!/bin/bash
# Each package alone (with only its dependencies): is an error the mod's own, or a clash with other mods?
#   ./isolate.sh <Author-Name> [...]
cd "$(dirname "$0")"
for pkg in "$@"; do
  P="profiles/iso/$pkg"
  python3 make-profile.py "$P" "$pkg" >/dev/null 2>&1 || { echo "== $pkg: profile failed"; continue; }
  if pgrep -x ROUNDS >/dev/null; then echo "ROUNDS is running (someone's playing): stopping"; exit 1; fi
  ./launch.sh "$P" 25 240 >/dev/null 2>&1
  # once more with dependencies the mod uses but doesn't declare
  EXTRA=$(python3 deps.py "$P" | grep -v '^?' | tr '\n' ' ')
  if [ -n "$EXTRA" ]; then
    echo "   $pkg: undeclared dependencies added: $EXTRA"
    python3 make-profile.py "$P" "$pkg" $EXTRA >/dev/null 2>&1
    ./launch.sh "$P" 25 240 >/dev/null 2>&1
  fi
  python3 analyze.py "$P" --json "$P/run/result.json" > "$P/run/summary.txt" 2>&1
  python3 - "$P" "$pkg" <<'PY'
import json, sys
r = json.load(open(sys.argv[1] + "/run/result.json"))
errs = [e for e in r["errors"] if "shader compiler" not in e["message"] and not e["message"].startswith("Command Line")]
print(f"== {sys.argv[2]}: {len(r['loaded'])} plugins, {len(errs)} distinct errors")
for e in errs: print(f"   x{e['count']} {e['message'][:170]}")
PY
done
