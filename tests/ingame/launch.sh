#!/bin/bash
# Starts ROUNDS on a profile the way the Gale fork does (Doorstop through DYLD_INSERT_LIBRARIES, Steam running),
# waits until mods have loaded (or a timeout), keeps both logs in <profile>/run/, and quits the game.
#   ./launch.sh <profile> [settle-seconds=25] [timeout=180]
set -u
P="$(cd "$1" && pwd)"; SETTLE="${2:-25}"; LIMIT="${3:-180}"
G="$HOME/Library/Application Support/Steam/steamapps/common/ROUNDS"
PLAYER_LOG="$HOME/Library/Logs/Landfall Games/ROUNDS/Player.log"
LOG="$P/BepInEx/LogOutput.log"

# one game at a time: several agents may share this bench
LOCK=/tmp/rounds-bench.lock
until mkdir "$LOCK" 2>/dev/null; do sleep 3; done
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

pgrep -x steam_osx >/dev/null || { echo "Steam isn't running"; exit 2; }
pgrep -x ROUNDS >/dev/null && { echo "ROUNDS is already running"; exit 2; }
[ -f "$G/steam_appid.txt" ] || echo 1557740 > "$G/steam_appid.txt"
rm -f "$LOG"; mkdir -p "$P/run"

cd "$G"
SteamAppId=1557740 SteamGameId=1557740 \
DYLD_INSERT_LIBRARIES="$P/libdoorstop.dylib" DOORSTOP_ENABLED=1 \
DOORSTOP_TARGET_ASSEMBLY="$P/BepInEx/core/BepInEx.Preloader.dll" DOORSTOP_IGNORE_DISABLED_ENV=0 \
DOORSTOP_BOOT_CONFIG_OVERRIDE= DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE= DOORSTOP_MONO_DEBUG_ENABLED=0 \
DOORSTOP_MONO_DEBUG_ADDRESS=127.0.0.1:10000 DOORSTOP_MONO_DEBUG_SUSPEND=0 \
  "$G/ROUNDS.app/Contents/MacOS/ROUNDS" >/dev/null 2>&1 &
PID=$!
START=$(date +%s); STATE="timeout"
while kill -0 $PID 2>/dev/null; do
  if grep -q "Chainloader startup complete" "$LOG" 2>/dev/null; then STATE="loaded"; break; fi
  [ $(( $(date +%s) - START )) -ge "$LIMIT" ] && break
  sleep 1
done
kill -0 $PID 2>/dev/null || STATE="exited"
LOADED=$(( $(date +%s) - START ))
[ "$STATE" = loaded ] && sleep "$SETTLE"
kill -0 $PID 2>/dev/null && { kill $PID; sleep 2; kill -9 $PID 2>/dev/null; }
cp "$LOG" "$P/run/LogOutput.log" 2>/dev/null; cp "$PLAYER_LOG" "$P/run/Player.log" 2>/dev/null
echo "$STATE after ${LOADED}s (then ${SETTLE}s settle); logs in $P/run"
