"""Gameplay test: the pilot plugin (pilot/) drives the game in a profile and reports per card.

    python pilot.py match <profile> [Author-Name ...]     build the profile from the packages (or reuse it), then a real
                                                          local match between two AIs, every card offered and picked
    python pilot.py online <profile> [Author-Name ...]    the same as an online game: two copies of the game in one
                                                          RoundsWithFriends private room, then the copies are compared

Results: <profile>/run/pilot[-host|-join]/cards.tsv, errors.tsv, state.tsv, shots/*.png, plus the usual logs.
ROUNDS_PILOT_MINUTES sets the match length (default 20).
"""
import os, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import launch

# the old game build still has Odin in Managed (2025 removed it): it gets the pilot built against it (OLDGAME, bin/old)
OLD = os.path.exists(os.path.join(launch.GAME, "ROUNDS_Data", "Managed", "Sirenix.Serialization.dll"))
PILOT_DLL = os.path.join(HERE, "pilot", "bin", *(("old",) if OLD else ("Release", "net472")), "rounds-bench.pilot.dll")
WINDOW = ("-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720")


def rows(path):
    """tsv rows without the header; [] if the file is missing"""
    if not os.path.exists(path): return []
    return [l.rstrip("\r\n").split("\t") for l in open(path, encoding="utf-8", newline="\n")][1:]


def summary(out, label=""):
    cards = rows(os.path.join(out, "cards.tsv"))
    if not os.path.exists(os.path.join(out, "cards.tsv")):
        print(f"{label}no cards.tsv: the pilot didn't finish; see run/LogOutput.log*"); return
    skipped = [r for r in cards if len(r) > 1 and r[1].startswith("(not offered")]
    errs = [r for r in rows(os.path.join(out, "errors.tsv")) if len(r) == 2]
    print(f"{label}{len(cards) - len(skipped)} cards picked, {len(skipped)} not offered (the game wouldn't allow them); "
          f"{len(os.listdir(os.path.join(out, 'shots')))} screenshots")
    for src in sorted({e[0] for e in errs}):
        print(f"  {src}:")
        for e in errs:
            if e[0] == src: print(f"    {e[1][:200]}")


def compare(host, join):
    """battles where the two copies disagree about a player's cards or stats: a desync"""
    a = {(r[0], r[1]): r[2:] for r in rows(os.path.join(host, "state.tsv")) if len(r) == 5}
    b = {(r[0], r[1]): r[2:] for r in rows(os.path.join(join, "state.tsv")) if len(r) == 5}
    both = sorted(set(a) & set(b), key=lambda k: (int(k[0]), k[1]))
    bad = [k for k in both if a[k] != b[k]]
    print(f"desync check: {len(both)} player snapshots in both copies, {len(bad)} differ")
    for k in bad[:20]:
        print(f"  battle {k[0]} player {k[1]}:\n    host: {a[k]}\n    join: {b[k]}")


def matches(profs):
    """a local match on each profile at once, each its own copy of the game (the sweep: one batch per copy)"""
    profs = [os.path.abspath(p) for p in profs]
    copies, dests = [], []
    for p in profs:
        dest = os.path.join(p, "BepInEx", "plugins", "rounds-bench-pilot")
        os.makedirs(dest, exist_ok=True)
        shutil.copyfile(PILOT_DLL, os.path.join(dest, "rounds-bench.pilot.dll"))
        dests.append(dest)
        out = os.path.join(p, "run", "pilot")
        shutil.rmtree(out, ignore_errors=True)
        os.makedirs(out)
        copies.append((p, {"ROUNDS_PILOT": "match", "ROUNDS_PILOT_OUT": out},
                       (*WINDOW, "-logFile", os.path.join(p, "run", "Player.log")), os.path.join(out, "done")))
    try:
        _, env, args, done = copies[0]
        launch.run(profs[0], 0, 2400, env_extra=env, args_extra=args, until_file=done, also=copies[1:])
        for p, (_, _, _, done) in zip(profs, copies):
            summary(os.path.dirname(done), f"[{os.path.basename(p)}] ")
    finally:
        for d in dests: shutil.rmtree(d, ignore_errors=True)


def main():
    if sys.argv[1] == "matches": return matches(sys.argv[2:])
    mode, prof, pkgs = sys.argv[1], sys.argv[2], sys.argv[3:]
    if pkgs:
        subprocess.run([sys.executable, os.path.join(HERE, "make-profile.py"), prof, *pkgs], check=True)
    prof = os.path.abspath(prof)
    dest = os.path.join(prof, "BepInEx", "plugins", "rounds-bench-pilot")
    os.makedirs(dest, exist_ok=True)
    shutil.copyfile(PILOT_DLL, os.path.join(dest, "rounds-bench.pilot.dll"))
    try:
        if mode == "online":
            # the second player gets its own copy of the profile, as a second PC would have
            profs = {"host": prof, "join": prof + "-join"}
            shutil.rmtree(profs["join"], ignore_errors=True)
            shutil.copytree(prof, profs["join"], ignore=shutil.ignore_patterns("run"))
            room = os.path.join(prof, "run", "pilot-room.txt")
            os.makedirs(os.path.dirname(room), exist_ok=True)
            if os.path.exists(room): os.remove(room)
            outs = {}
            for side in ("host", "join"):
                outs[side] = os.path.join(profs[side], "run", "pilot-" + side)
                shutil.rmtree(outs[side], ignore_errors=True)
                os.makedirs(outs[side])
            copy = lambda side: (profs[side], {"ROUNDS_PILOT": side, "ROUNDS_PILOT_OUT": outs[side], "ROUNDS_PILOT_ROOM": room},
                                 (*WINDOW, "-logFile", os.path.join(profs[side], "run", "Player.log")),
                                 os.path.join(outs[side], "done"))
            _, env, args, done = copy("host")
            launch.run(prof, 0, 2400, env_extra=env, args_extra=args, until_file=done, also=[copy("join")])
            for side in ("host", "join"): summary(outs[side], f"[{side}] ")
            compare(outs["host"], outs["join"])
        else:
            out = os.path.join(prof, "run", "pilot")
            shutil.rmtree(out, ignore_errors=True)
            os.makedirs(out)
            launch.run(prof, 0, 1800, env_extra={"ROUNDS_PILOT": mode, "ROUNDS_PILOT_OUT": out}, args_extra=WINDOW,
                       until_file=os.path.join(out, "done"))
            summary(out)
    finally:
        shutil.rmtree(dest)  # the profile goes back to what players have


main()
