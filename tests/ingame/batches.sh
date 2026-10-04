#!/bin/bash
# The sweep's 98 mods in batches of N (with their dependencies): build, launch, analyze each.
#   ./batches.sh [N=10] [first-batch=0] [last-batch=9]
cd "$(dirname "$0")"
N="${1:-10}"; FIRST="${2:-0}"; LAST="${3:-9}"
for i in $(seq "$FIRST" "$LAST"); do
  P="profiles/b$i"
  echo "== batch $i (mods $((i*N+1))-$((i*N+N)))"
  python3 make-profile.py "$P" --top "$N" --skip $((i*N)) 2>&1 | tail -2
  if pgrep -x ROUNDS >/dev/null; then echo "ROUNDS is running (someone's playing): stopping"; exit 1; fi
  ./launch.sh "$P" 30 240 2>/dev/null
  # once more with dependencies the mods use but don't declare
  EXTRA=$(python3 deps.py "$P" | grep -v '^?' | tr '\n' ' ')
  if [ -n "$EXTRA" ]; then
    echo "   undeclared dependencies added: $EXTRA"
    python3 make-profile.py "$P" --top "$N" --skip $((i*N)) $EXTRA 2>&1 | tail -1
    ./launch.sh "$P" 30 240 2>/dev/null
  fi
  python3 analyze.py "$P" --json "$P/run/result.json" > "$P/run/summary.txt" 2>&1
  grep -E '^plugins loaded|^distinct errors|^  Info: [0-9]+ mods' "$P/run/summary.txt"
done
