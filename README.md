# ROUNDS Porting Toolkit

Port ROUNDS mods to the current game, **v1.1.2** (Unity 2022.3). That update renamed and removed game code most mods
used (`playerID`, `teamID`, `maxHealth`, damage methods, card names...), so they stopped loading.

**On a Mac, or want it all in one app?** [Crosswind](https://github.com/KieranK07/crosswind), a mod manager based on Gale, runs on Mac and Windows
and does all of this for you: install mods as usual and press Launch.

**Playing, not making mods?** [DuctTape](https://github.com/KieranK07/DuctTape) is the mod that does this as the game
starts: it swaps in Bknibb's UnboundLib and RoundsWithFriends ports and fixes old mods, with any mod manager.

The rest of this page is for mod authors.

- **rounds-port**: finds what v1.1.2 broke in a mod's DLL and fixes the mechanical parts. Works on the compiled DLL,
  so you see the problems before touching your source. One file, nothing to install. Windows, macOS, Linux.
- **Hot Reload** (optional): swap a mod into the running game without restarting it.

## 1. Download

**Windows** (PowerShell, in the folder you want it in):

```powershell
curl.exe -fLo rounds-port.exe https://github.com/KieranK07/rounds-porting-toolkit/releases/latest/download/rounds-port-win-x64.exe
```

**macOS** (Terminal):

```sh
curl -fLo rounds-port https://github.com/KieranK07/rounds-porting-toolkit/releases/latest/download/rounds-port-osx-arm64 && chmod +x rounds-port
```

Intel Mac: `rounds-port-osx-x64`. Linux: `rounds-port-linux-x64`. Every file and its SHA-256:
[latest release](https://github.com/KieranK07/rounds-porting-toolkit/releases/latest).

Downloaded with a browser instead? Windows may show *Windows protected your PC*: **More info → Run anyway**. macOS may
refuse to open it: run `xattr -d com.apple.quarantine rounds-port` once.

## 2. Scan and fix a mod

**Windows:**

```powershell
.\rounds-port.exe scan "C:\path\to\MyMod.dll"    # what's broken
.\rounds-port.exe fix  "C:\path\to\MyMod.dll"    # fixed copy in ported\MyMod.dll, and what's left for you
```

**macOS:** the same with `./rounds-port scan MyMod.dll`.

Every problem is marked:

- **AUTO**: `fix` rewrites it.
- **REVIEW**: `fix` rewrites it or it's probably fine, but check it.
- **MANUAL**: change your source. The report says what the game has now.

It finds ROUNDS through Steam (or pass `--game <folder>`). It needs **the current game**, not the
`old-rounds-for-mods` beta: Steam → ROUNDS → Properties → Betas → **None**.

Mods are checked against BepInEx 5 and **Bknibb's UnboundLib 4**
([release](https://github.com/Bknibb/UnboundLib/releases/tag/v4.2.5)), the UnboundLib that works on v1.1.2. It uses
the game's copies; if the game doesn't have them, it downloads them once from their GitHub releases (checksum-checked)
to read mods with. Nothing is installed into the game. Other mods yours uses (ModdingUtils, RarityLib...) are read
from `BepInEx\plugins`, your r2modman / Thunderstore Mod Manager / Gale profiles, or `--ref <folder>`. Known libraries
that aren't installed anywhere are downloaded from Thunderstore (pinned versions, checksum-checked), only to read your
mod; anything still missing is listed as not checked.

The **[porting guide](docs/PORTING.md)** goes from here to a release build: testing in game, what to change in your
source, and what the tool can't see.

## 3. Hot Reload (optional)

Swap a mod into the running game: no restart between changes. Needs BepInEx in the game.

```powershell
.\rounds-port.exe install-hotreload                  # once: adds the plugin to BepInEx\plugins\HotReload
.\rounds-port.exe hot "bin\Debug\MyMod.dll" --watch  # ports it, swaps it in, and again on every rebuild
```

(macOS: `./rounds-port install-hotreload`, `./rounds-port hot bin/Debug/MyMod.dll --watch`.)

When a mod unloads, its patches, cards, menus and events go with it, so the new copy starts clean.
**[Hot Reload guide](docs/HOTRELOAD.md)**: setup on Windows and macOS, Visual Studio, what it undoes, limits.
`rounds-port uninstall-hotreload` takes it out again.

## What `fix` handles

| Old | Now | Rewrite |
|---|---|---|
| `Player.playerID`, `Player.teamID` fields | `PlayerID`, `TeamID` properties | reads use the property. Writes use `SetPlayerID`, or the private `m_teamID` (`AssignTeamID` also syncs Photon) |
| `CharacterData.maxHealth` field | `MaxHealth` property | reads use the property. Writes use `m_maxHealth` (the setter can unlock an achievement) |
| `CardInfo.cardName` reads | private, and empty for UnboundLib 4 cards | a helper that falls back to the localized key, `CardName`, then the GameObject name |
| `CardInfo.cardName` writes | private | writes the private field, so name lookups still find it (REVIEW: the title shown comes from localization) |
| The old game's own `Debug` class (`Log`, `LogError`, `LogWarning`, `DrawLine`) | removed | `UnityEngine.Debug` |
| `UIHandler.ShowJoinGameText`, `DisplayScreenText`, `DisplayScreenTextLoop` with a string | take a `LocalizedString` | a helper shows your text as is, untranslated (REVIEW) |
| `CallTakeDamage`, `TakeDamage`, `DoDamage`, `TakeDamageOverTime`, `DoDamageOverTime`, `RPCA_SendTakeDamage` | gained a trailing `HealthHandler.DamageSource` | passes `DamageSource.Player` |
| Harmony `argumentTypes` for those methods | no longer match | appends `typeof(HealthHandler.DamageSource)` |
| Photon RPCs to those methods (`RPCA_SendTakeDamage`) | one more argument | appends `DamageSource.Player` (PUN drops RPCs with the wrong argument count) |
| `PlayerManager.AddPlayerDiedAction(...)` | removed; `PlayerDiedAction` is a public field | adds the handler to the field |
| `Optionshandler.vol_Master` / `vol_Sfx` | removed | reads the options slider (REVIEW) |
| `Optionshandler.lockMouse` / `lockStick` | removed | reads the aim-in-8-directions options the game reads now |
| `CardBarButton.card` | `m_cardInfo` | uses it |
| Harmony patch on `CardBar.OnHover` with no `argumentTypes` | two overloads now: Harmony can't pick | adds `typeof(CardBarButton)`, the hover one (REVIEW) |
| Photon `Room.GetPlayer(id)`, `Room.PlayerCount`, `RoomOptions.MaxPlayers` | `GetPlayer(id, findMaster)`; byte → int | passes `false`; converts |
| `TMP_Text.ForceMeshUpdate()` | `ForceMeshUpdate(bool, bool)` | passes `(false, false)`, the old behaviour |
| `TMP_FontAsset.HasCharacter(c, searchFallbacks)` | gained `tryAddCharacter` | passes `false` |
| `ObjectsToSpawn.SpawnObject(...)` with its result dropped | returns pooled `PoolableWrapper[]` | calls the new one (MANUAL when the result is used) |
| Harmony patch on `CardBar.OnHover` taking `CardInfo card` | takes the `CardBarButton` now | the parameter becomes `cardButton`, reads use its `m_cardInfo` (REVIEW) |
| `DontDestroyOnLoad` in a plugin's `Awake` or constructor | BepInEx starts plugins before any scene is loaded; the first scene load destroys those objects anyway | also sets `hideFlags` `DontSave`, which keeps them |
| `UIVertex.uv0`…`uv3` | Vector2 → Vector4 (Unity 2022) | converts on read and write |
| `Steamworks.*` in Assembly-CSharp-firstpass | `com.rlabrecque.steamworks.net` | retargets the reference |
| `UnityEngine.Input` in CoreModule | `UnityEngine.InputLegacyModule` | retargets the reference, also in `[HarmonyPatch(typeof(Input))]` |
| `UnityEngine.TextCoreModule` types (`Glyph`...) | split into `TextCoreFontEngineModule` / `TextCoreTextEngineModule` | retargets the reference |
| `CardChoice.GetRanomCard` | `GetRandomCard` (typo fixed) | Harmony targets and strings |
| UnboundLib 3's `Unbound.RegisterMaps(...)` | removed in UnboundLib 4 | `LevelManager.RegisterMaps(..., "Modded")`, which is what it forwarded to |
| Harmony `___field` / reflection `"field"` that became `m_field` | renamed | renames it (REVIEW) |

Helpers are copied into your mod as an internal `__RoundsCompat` class (source: `src/compathelpers`), so the fixed DLL
has no new dependency.

It also flags (MANUAL/REVIEW): Harmony targets that are gone, renamed or now ambiguous; patch parameters that no longer
match; reflection by name that finds nothing; overrides broken by a changed base signature; and known behaviour
changes (object pooling, `TrickShot` setup moved to `Start`, Odin Serializer no longer shipped with the game).
The full old → new list, with IL detail: [docs/MAPPING.md](docs/MAPPING.md).

Asset bundles the mod ships are checked too: game scripts that are gone, saved fields the game dropped, cards and card
frames saved before localization, and shaders that draw pink on macOS.
[Porting guide: asset bundles](docs/PORTING.md#asset-bundles).

## How well it works

- On the 12 mods ported by hand for the first Mac modpack, `fix` reproduces 8
  byte for byte (Cosmic Rounds, Classes Manager Reborn, RarityLib, ModsPlus, Will's Wacky Map Objects, CardBarPatch,
  GunUnblockablePatch, TemporaryStatsPatch). For the other 4 it fixes the mechanical parts and flags the rest.
- The 100 most-downloaded Thunderstore mods (October 2026): no crashes, and after `fix` 74 of 98 have nothing left
  in the report and 9 more only REVIEW items. UnboundLib 3 and RoundsWithFriends 2 are left out: Bknibb's ports
  replace them. What's left for the other 15 is real work: object pooling, the player spotlight
  RoundsWithFriends 3 dropped, Odin Serializer, reflection into the Unity editor.
- Cards+, KeysCards and ZOMC from Thunderstore: fixed with no MANUAL items, load in game, and swap in and out live.
- It can't see behaviour changes that still compile (a pooled object reused while you hold it): those show in game.

## All commands

```
rounds-port scan  <mod.dll | folder>...   list everything that no longer matches the game
rounds-port fix   <mod.dll | folder>...   rewrite what it can, save fixed copies, list what's left
rounds-port hot   <mod.dll | folder>...   port it and swap it into the running game (needs Hot Reload)
rounds-port install-hotreload             add the Hot Reload plugin to the game
rounds-port uninstall-hotreload           remove it

--game <dir>     ROUNDS folder (found through Steam if you leave it out)
--ref <dir>      another folder of DLLs your mod uses, e.g. Bknibb's UnboundLib 4 (repeatable)
-o, --out <dir>  where fix saves mods (default: a "ported" folder next to each mod)
--pdb            fix also writes a .pdb (line numbers in error stack traces)
--watch          hot: keep watching the DLL and swap in every rebuild
```

Exit code: 0 nothing left to do, 1 only AUTO/REVIEW items, 2 MANUAL items remain, 3 error. Folders are searched for
mod DLLs (anything that references the game).

## Build from source

[.NET 8 SDK](https://dotnet.microsoft.com/download), then from this folder:

```powershell
dotnet run --project src/rounds-port -c Release -- scan "C:\path\to\MyMod.dll"
```

| Folder | What | Needs the game to build |
|---|---|---|
| `src/rounds-port` | the CLI (Mono.Cecil) | no: the two DLLs below are checked in, prebuilt |
| `src/compathelpers` | helpers `fix` copies into mods | yes |
| `src/HotReload` | the Hot Reload BepInEx plugin | yes, with BepInEx installed |
| `src/OdinStandIn` | the Odin Serializer stand-in MapsExtended loads (built copy in `odin/`) | yes |

Other folders: `patches/` hand-made patches for specific mod releases ([patches/README.md](patches/README.md)),
`data/old-libraries.tsv` every old UnboundLib, MMHook and RoundsWithFriends release by SHA-256, `tests/` the sweep and
the in-game bench ([tests/ingame](tests/ingame)). [DuctTape](https://github.com/KieranK07/DuctTape) (the mod, AutoFix and
the Runtime) builds from `src/rounds-port` and takes the patches and the Odin stand-in from here.

Game path: `C:\Program Files (x86)\Steam\steamapps\common\ROUNDS` or the macOS Steam folder by default; elsewhere,
add `-p:GameDir="<folder>"`. After changing `src/compathelpers` or `src/HotReload`, build it with
`-p:UpdateEmbedded=true` so rounds-port picks up the new copy. Builds are deterministic: the same source gives the same
bytes on Windows and macOS. Release binaries: `scripts/publish.sh`.

New rename or rule? The table of known changes is `src/rounds-port/Known.cs`, the rewrites are in `Fixer.cs`, the
checks in `Scanner.cs`. Check a change against 98 real mods with the sweep ([tests/README.md](tests/README.md)).
Issues and pull requests welcome.

## Credits

Built with [Mono.Cecil](https://github.com/jbevain/cecil) (MIT) and
[AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) (MIT, reads asset bundles). Hot Reload is modelled on BepInEx's
[ScriptEngine](https://github.com/BepInEx/BepInEx.Debug). Ported mods depend on [Bknibb](https://github.com/Bknibb)'s
UnboundLib and RoundsWithFriends updates for v1.1.2. MIT license ([LICENSE](LICENSE)). ROUNDS is © Landfall Games;
not affiliated with Landfall.
