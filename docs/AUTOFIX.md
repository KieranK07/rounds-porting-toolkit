# AutoFix (load-time patcher)

A BepInEx preloader patcher that runs `rounds-port fix` on old mods every time ROUNDS starts, before BepInEx loads
them. Players keep the mods from Thunderstore as they are; nobody needs a new release from the mod's author for the
parts `fix` handles.

Players get it in the Thunderstore package (`thunderstore/`, built by `scripts/package.py`) with the Runtime and Odin
Serializer. The Gale fork does the library and patch steps itself before launch; AutoFix then finds nothing left to do
for them.

## Install

Put `rounds-port.AutoFix.dll` in `BepInEx/patchers/` (under r2modman, Thunderstore Mod Manager or Gale: the
profile's `BepInEx/patchers/`). It needs BepInEx 5.4.23 (Windows release or the macOS v5-lts build) and works on the
current game only; on the `old-rounds-for-mods` beta it puts every original back and does nothing else.

## What it does

At each start, before any plugin loads:

- `HideManagerGameObject` is turned on in `BepInEx.cfg` (the current game destroys plugins' objects otherwise). The
  Chainloader reads it after the patchers, so it counts from the first start.
- **Libraries:** a file from an old UnboundLib 3, MMHook or RoundsWithFriends 2 release gets Bknibb's port in its place
  (UnboundLib 4.2.5 with Octokit, its MMHOOK, RoundsWithFriends 3.0.10), downloaded from his GitHub releases into
  `BepInEx/cache/rounds-port/downloads` and checked by SHA-256. .NET's own HTTPS first, then `curl` (Windows 10+ and macOS
  have it). If any other copy of the library isn't an old release (a package brings a newer port), the old files are
  left alone and BepInEx loads the newer one. Offline: the old file stays and the next start tries again.
- **Curated patches:** exact mod versions that needed hand-made fixes (Cosmic Rounds 2.7.0, MapsExtended 1.4.2,
  ModdingUtils 0.4.8, ...) get the rounds-mac-modpack's binary patch, found by the file's SHA-256 (`src/AutoFix/curated`,
  made by `scripts/curated.py` from the Gale fork's copy), then `fix` as usual.
- Every DLL in `BepInEx/plugins` that uses the game or UnboundLib is scanned and fixed, like `rounds-port fix`.
- A mod `fix` changes is **replaced in place** by the fixed copy. The original goes to
  `BepInEx/cache/rounds-port/originals/<sha256>.dll`.
- A mod with MANUAL items (problems only its author can fix) still gets everything else fixed, and the log names
  what's left. Left alone, it would fail on the fixable problems too (see `LeaveManualMods`).
- The results go into `BepInEx/cache/rounds-port/index.tsv`. A start with no new or updated mods reads none of them
  (about 50 ms for 100 mods). The first start after installing 100 mods takes about 4 s.
- A new version of AutoFix, a game update or a settings change re-checks every mod, starting from its original.
- When a mod manager updates or reinstalls a mod, the new file is fixed again.
- Files are swapped by renaming, never written into. Gale hard-links mods to its download cache, and that copy stays
  the original.
- Old builds of UnboundLib (3.x) and RoundsWithFriends (2.x), and the MMHOOK made for the old game, are recognised by
  what's in the file, not the folder name, so Bknibb's files installed into the old packages' folders are used.
  Replaced ones count as fixed: `RestoreOriginals` and the old game build put them back.

One line per mod in `BepInEx/LogOutput.log`, from source `rounds-port`:

```
[Info   :rounds-port] fixed Root-Classes_Manager_Reborn/ClassesManagerReborn.dll: 3 kinds of change
[Warning:rounds-port] left RS_Mind-RSClasses/RSClasses.dll as it is: 1 MANUAL item: RSClasses.ShieldBash::OnBlock RPC("RPCA_AddSlow") with 1 argument. ...
[Info   :rounds-port] 106 mods: 47 fixed, 45 need nothing, 13 left as they are (problems only their authors can fix) (3954 ms)
```

## Settings

`BepInEx/config/rounds-port.autofix.cfg`, created at the first start:

| Setting | Default | |
|---|---|---|
| `Enabled` | true | |
| `Exclude` | | DLL or folder names never to touch, comma-separated. A mod fixed earlier gets its original back. |
| `RestoreOriginals` | false | Puts every original back at the next start, then sets `Enabled = false`. |
| `LeaveManualMods` | false | Leave mods with MANUAL items exactly as they are instead. |
| `FixAnyway` | | With `LeaveManualMods` on: mods to fix anyway. |

To undo everything: `RestoreOriginals = true`, start the game once. Deleting `BepInEx/cache` loses the originals; the
fixed mods then stay fixed until the mod manager reinstalls them.

## Multiplayer

Everyone running AutoFix gets the same files. Players with mods ported by `rounds-port fix` (or the
rounds-mac-modpack) run the same code: AutoFix's output has the same meaning as the CLI's, though not the same bytes
(it uses BepInEx's Mono.Cecil 0.10.4; the CLI uses 0.11.6). UnboundLib's mod check compares IDs and versions only, and
`fix` never changes them.

## Testing

`src/AutoFix` builds from the same scan/fix source as the CLI (`rounds-port.csproj`), for the game's Mono. Run on the
game's own Mono outside the game against a profile with the 98 sweep packages (plus their libraries, Bknibb's
UnboundLib and RWF), hard-linked like Gale's:

- All 57 DLLs the CLI sweep rewrites come out with the same meaning (references, every type, member and IL
  instruction, attributes, resources). 3 more are libraries the sweep only downloads.
- Second start: no file read or touched, 53 ms.
- Re-check after a settings change: unchanged output isn't rewritten.
- Exclude, RestoreOriginals, an interrupted swap, a mod update, a corrupt DLL, a read-only folder.
- None of the hard-linked originals changed.
- Same results with the Windows release's `BepInEx.dll` and the macOS v5-lts build.

In game (Windows, `port-tests/ingame`): a profile laid out as r2modman does (Thunderstore originals plus the package, no
Gale layer) starts, swaps the libraries, applies the curated patches and plays a full AI match.
