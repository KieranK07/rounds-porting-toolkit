# Regression test: the sweep

`rounds-port sweep` runs scan and fix on real mods: the 100 most-downloaded Thunderstore mods with DLLs, pinned to
the versions in `sweep-packages.tsv` (the old UnboundLib 3 and RoundsWithFriends 2 are left out, since Bknibb's ports
replace them). Mods are downloaded once into the rounds-port cache (`%LOCALAPPDATA%\rounds-port\sweep` on Windows,
`~/Library/Application Support/rounds-port/sweep` on macOS, `~/.local/share/rounds-port/sweep` on Linux). Only the list
and the results are committed here.

Before and after changing `src/rounds-port`:

```powershell
dotnet run --project src/rounds-port -c Release -- sweep tests/sweep-packages.tsv --compare tests/sweep-results.tsv
```

It prints a line per package (`clean`, `REVIEW`, `MANUAL`) and then what changed against `sweep-results.tsv`: a
package's grade, its leftover counts, or the bytes of its fixed DLLs. Exit code 0: nothing changed. 1: something did,
so check that every change is one you meant. When they all are, save the new results with
`--save tests/sweep-results.tsv` and commit them with your change.

- Fixed DLLs are byte-identical on Windows, macOS and Linux, so the saved hashes hold on any machine with the current
  game (v1.1.2).
- It reads only the game, BepInEx's core and the packages in the list (plus the libraries they use, from Thunderstore),
  never the mods you have installed, so the grades are the same on every machine too.
- `--top 100` makes a fresh list from Thunderstore's current top 100 (and their current versions).
- Passing the sweep says rounds-port handles these mods the way it did before, not that they work in game. Test
  changed rewrites in game too ([Hot Reload](../docs/HOTRELOAD.md) makes that quick).
