# AutoFix (load-time patcher, in testing)

A BepInEx preloader patcher that runs `rounds-port fix` on old mods every time ROUNDS starts, before BepInEx loads
them. Players keep the mods from Thunderstore as they are; nobody needs a new release from the mod's author for the
parts `fix` handles.

Not released yet: it has been tested outside the game only (see Testing).

## Install

Put `rounds-port.AutoFix.dll` in `BepInEx/patchers/` (under r2modman, Thunderstore Mod Manager or Gale: the
profile's `BepInEx/patchers/`). It needs BepInEx 5.4.23 (Windows release or the macOS v5-lts build) and works on the
current game only; on the `old-rounds-for-mods` beta it does nothing.

## What it does

At each start, before any plugin loads:

- Every DLL in `BepInEx/plugins` that uses the game or UnboundLib is scanned and fixed, like `rounds-port fix`.
- A mod `fix` changes is **replaced in place** by the fixed copy. The original goes to
  `BepInEx/cache/rounds-port/originals/<sha256>.dll`.
- A mod that still has MANUAL items after fixing is **left as it is** (see `FixWhenManualLeft`).
- The results go into `BepInEx/cache/rounds-port/index.tsv`. A start with no new or updated mods reads none of them
  (about 50 ms for 100 mods). The first start after installing 100 mods takes about 4 s.
- A new version of AutoFix, a game update or a settings change re-checks every mod, starting from its original.
- When a mod manager updates or reinstalls a mod, the new file is fixed again.
- Files are swapped by renaming, never written into. Gale hard-links mods to its download cache, and that copy stays
  the original.
- Old builds of UnboundLib (3.x) and RoundsWithFriends (2.x), and the MMHOOK made for the old game, are skipped with
  a warning: use Bknibb's ports. They're recognised by what's in the file, not the folder name, so Bknibb's files
  installed into the old packages' folders are used.

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
| `FixWhenManualLeft` | false | Also rewrite mods with MANUAL items left. They load, but those parts may still fail. |
| `FixAnyway` | | Like `FixWhenManualLeft`, for the mods listed. |

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

Still to test in game: the patcher loads and runs before the Chainloader, under an r2modman profile launch too; a
modpack of Thunderstore originals plays a round; multiplayer against the ported modpack; startup time on Windows.
