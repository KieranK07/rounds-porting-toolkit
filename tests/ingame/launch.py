"""launch.sh for Windows: start ROUNDS on a profile the way Gale/r2modman do (Doorstop 3 proxy winhttp.dll in the game
folder, target from the command line), wait for mods to load, keep the logs in <profile>/run, quit the game.
The proxy is removed afterwards so Steam launches stay vanilla.

    python launch.py <profile> [settle=30] [timeout=240]
"""
import os, shutil, subprocess, sys, time

# ROUNDS_GAME: another copy of the game (the bench keeps the old-rounds-for-mods beta in ROUNDS-modfix\gameold)
GAME = os.environ.get("ROUNDS_GAME", r"C:\Program Files (x86)\Steam\steamapps\common\ROUNDS")
STEAM = r"C:\Program Files (x86)\Steam\steam.exe"
PLAYER_LOG = os.path.expandvars(r"%USERPROFILE%\AppData\LocalLow\Landfall Games\ROUNDS\Player.log")
LOCK = os.path.join(os.environ["TEMP"], "rounds-bench.lock")


def running(name):
    out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {name}", "/NH"], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def run(prof, settle=30, limit=240, env_extra=None, args_extra=(), until_file=None, also=()):
    """until_file: wait for that file (the pilot's "done") instead of "Chainloader startup complete".
    also: [(profile, env_extra, args_extra, until_file)] more copies of the game, each on its own profile (two copies on one
    profile fight over its config files), started once the first has loaded its mods; the run ends when every
    until_file exists."""
    prof = os.path.abspath(prof)
    log = os.path.join(prof, "BepInEx", "LogOutput.log")
    profs = [prof, *(os.path.abspath(a[0]) for a in also)]
    while True:  # one game at a time
        try: os.mkdir(LOCK); break
        except FileExistsError: time.sleep(3)
    try:
        if running("ROUNDS.exe"):
            sys.exit("ROUNDS is already running")
        if not running("steam.exe"):
            subprocess.Popen([STEAM, "-silent"])
            time.sleep(45)  # ponytail: fixed wait for Steam login; poll steamwebhelper if this proves flaky
        for f in ("winhttp.dll", "doorstop_config.ini"):
            shutil.copyfile(os.path.join(prof, f), os.path.join(GAME, f))
        if not os.path.exists(os.path.join(GAME, "steam_appid.txt")):
            open(os.path.join(GAME, "steam_appid.txt"), "w").write("1557740")
        for p in profs:
            if os.path.exists(os.path.join(p, "BepInEx", "LogOutput.log")): os.remove(os.path.join(p, "BepInEx", "LogOutput.log"))
            os.makedirs(os.path.join(p, "run"), exist_ok=True)
        env = dict(os.environ, SteamAppId="1557740", SteamGameId="1557740")
        # stdout gets Mono's own output, such as the pilot's thread dump when the game freezes
        start_game = lambda p, env_x, args_x: subprocess.Popen(
            [os.path.join(GAME, "ROUNDS.exe"), "--doorstop-enable", "true", "--doorstop-target",
             os.path.join(p, "BepInEx", "core", "BepInEx.Preloader.dll"), *args_x],
            cwd=GAME, env=dict(env, **(env_x or {})), stdout=open(os.path.join(p, "run", "stdout.log"), "w"),
            stderr=subprocess.STDOUT)
        is_loaded = lambda: os.path.exists(log) and "Chainloader startup complete" in open(log, encoding="utf-8", errors="replace").read()
        procs, pending = [start_game(prof, env_extra, args_extra)], list(also)
        until = [f for f in [until_file, *(a[3] for a in also)] if f]
        start, state = time.time(), "timeout"
        while time.time() - start < limit:
            if pending and is_loaded():
                procs += [start_game(p, e, a) for p, e, a, _ in pending]; pending = []
            if until and all(os.path.exists(f) for f in until):
                state = "done"; break
            if not until and is_loaded():
                state = "loaded"; break
            if all(p.poll() is not None for p in procs):
                state = "exited"; break
            time.sleep(1)
        loaded = int(time.time() - start)
        if state == "loaded": time.sleep(settle)
        time.sleep(3 if until else 0)  # the pilot quits by itself just after writing "done"
        for p in procs:
            if p.poll() is None:
                subprocess.run(["taskkill", "/PID", str(p.pid), "/F"], capture_output=True)
                p.wait()
        time.sleep(1)
        for p in profs:
            src = os.path.join(p, "BepInEx", "LogOutput.log")
            if os.path.exists(src): shutil.copyfile(src, os.path.join(p, "run", "LogOutput.log"))
        if os.path.exists(PLAYER_LOG): shutil.copyfile(PLAYER_LOG, os.path.join(prof, "run", "Player.log"))
        print(f"{state} after {loaded}s (then {settle}s settle); logs in {prof}\\run")
    finally:
        for f in ("winhttp.dll", "doorstop_config.ini"):
            try: os.remove(os.path.join(GAME, f))
            except OSError: pass
        os.rmdir(LOCK)


if __name__ == "__main__":
    run(sys.argv[1], *(int(a) for a in sys.argv[2:4]))
