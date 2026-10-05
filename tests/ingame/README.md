# In-game bench

Plays real matches with mods installed and records what happens: which cards were offered and picked, every error
with the card that was in play, screenshots of each card. The sweep (`../README.md`) checks what `rounds-port` writes;
this checks what players see.

Runs on Windows (the macOS scripts `launch.sh`, `batches.sh` and `isolate.sh` are the older Mac versions). Needs
Python 3 with Pillow, a copy of ROUNDS with BepInEx in it for building the pilot, and a checkout of the
[DuctTape](https://github.com/KieranK07/DuctTape) next to this repo, its package built (`dist/`), and on a Mac [Crosswind](https://github.com/KieranK07/crosswind) (the Gale fork) too (or `GALE_DIR`) for its BepInEx files.
Profiles, downloads (`store/`) and run output stay here and are not committed.

| Command | Does |
|---|---|
| `python make-profile.py profiles/x Author-Name ...` | a profile laid out like Gale's, from Thunderstore, with DuctTape (and on a Mac, Crosswind's BepInEx); `--package <zip>` adds only that DuctTape package; `--plain` leaves the mods untouched |
| `python pilot.py matches profiles/b0 profiles/b1 ...` | a match between two AI players per profile; `match` / `online` for one |
| `python oldnew.py 0 1 ...` | the same profiles on the 2025 game (`b<i>`) and the `old-rounds-for-mods` beta (`o<i>`) |
| `python compare-shots.py out.html 7 8 0` | each card captured on both builds, side by side |
| `python report.py <out> label=<profile>/run/pilot` | one HTML page per run, screenshots included |
| `python compat.py > ../../docs/COMPATIBILITY.md` | the compatibility list from the `b0`..`b9` runs |

Settings: `ROUNDS_GAME` (a copy of the game, such as the old beta; default the Steam folder), `ROUNDS_PILOT_MINUTES`
(match length in minutes, default 20), `ROUNDS_PILOT_PROBE` (camera and object experiments, see `pilot/Pilot.cs`). The pilot is a
BepInEx plugin, built once per game build:

```powershell
dotnet build pilot -c Release -p:GameDir=<copy with BepInEx> -p:Managed=<game>\ROUNDS_Data\Managed
dotnet build pilot -c Release -p:GameDir=<copy with BepInEx> -p:Managed=<old beta>\ROUNDS_Data\Managed -p:DefineConstants=OLDGAME -p:OutputPath=bin\old\ -p:IntermediateOutputPath=obj\old\
```
