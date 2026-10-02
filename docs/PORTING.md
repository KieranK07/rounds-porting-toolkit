# Porting a mod to ROUNDS v1.1.2

From "my mod doesn't load anymore" to a release that works on the current game. Commands are Windows PowerShell;
on macOS use `./rounds-port` and `/` paths.

## 1. Get the current game

rounds-port reads the game's own DLLs, so it needs v1.1.2, not the `old-rounds-for-mods` beta:
Steam → ROUNDS → Properties → Betas → **None**, and let it update. (Mods built for the beta still work on the beta;
you're porting for everyone on the current game, which includes every Mac player: the beta has no macOS build.)

## 2. Get a modded game to test in

You need BepInEx plus the libraries most mods depend on, already ported to v1.1.2:

- **Everything at once** (Windows and macOS): the [rounds-mac-modpack](https://github.com/KieranK07/rounds-mac-modpack)
  installer sets up BepInEx and 31 working mods, including UnboundLib 4, ModdingUtils, RarityLib, Classes Manager
  Reborn and RoundsWithFriends. One command; it backs up whatever mods you had.
- **Only the basics**: [BepInEx 5.4.23](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) (`win_x64` zip,
  extracted into the game folder), with `HideManagerGameObject = true` in `BepInEx\config\BepInEx.cfg` (start the game
  once to create it): without that, v1.1.2 destroys BepInEx's manager object and no mod runs. Then Bknibb's
  [UnboundLib 4](https://github.com/Bknibb/UnboundLib/releases/tag/v4.2.5) and
  [RoundsWithFriends 3](https://github.com/Bknibb/RoundsWithFriends/releases/tag/v3.0.10) in `BepInEx\plugins`.
  The UnboundLib 3 and RoundsWithFriends 2 on Thunderstore don't work on v1.1.2.

## 3. Scan

```powershell
.\rounds-port.exe scan "C:\path\to\MyMod.dll"
```

The header says which game and which UnboundLib it checked against. If the game has no BepInEx or UnboundLib 4, it
downloads them once (only to read your mod). If your mod uses other mods that aren't installed, it says
`not checked: what it uses from ModdingUtils...`: put them in `BepInEx\plugins` or add `--ref <folder>` to check those
parts too.

Each line is **AUTO** (`fix` handles it), **REVIEW** (handled or probably fine, look at it) or **MANUAL** (yours).

## 4. Fix and test the compiled DLL

```powershell
.\rounds-port.exe fix "C:\path\to\MyMod.dll"
```

That writes `ported\MyMod.dll` next to the original and prints every rewrite. Test it:

- copy it into `BepInEx\plugins` (replacing the old one) and start the game, or
- with [Hot Reload](HOTRELOAD.md): `.\rounds-port.exe hot "C:\path\to\MyMod.dll"` while the game runs.

Check the BepInEx console or `BepInEx\LogOutput.log` for errors, and play a round with your cards. If MANUAL items are
left, the mod may still load but those parts won't work until you change the source.

## 5. Update your source

The fixed DLL is for testing. For a release, make the same changes in your source so you keep building it normally:

1. **References.** Point your project at the v1.1.2 game DLLs (`ROUNDS_Data\Managed`) and UnboundLib 4. If you used
   `Assembly-CSharp-firstpass` for Steamworks, reference `com.rlabrecque.steamworks.net.dll` instead. If you used
   `UnityEngine.Input`, add `UnityEngine.InputLegacyModule.dll`.
2. **Compile errors** are mostly the renames `fix` listed: `player.playerID` → `player.PlayerID`,
   `teamID` → `TeamID`, `data.maxHealth` → `data.MaxHealth`, damage calls take a `HealthHandler.DamageSource`
   (`DamageSource.Player` keeps the old behaviour).
3. **Strings the compiler can't check**: Harmony patch targets, `___field` parameters, `AccessTools.Field(..., "name")`
   and `Traverse` names. The scan report lists each one that no longer matches, with what the game has now.
4. **MANUAL items** from the report. [MAPPING.md](MAPPING.md) has the old → new detail for each.
5. Build, then `scan` your new DLL: `no problems found` means it matches the game.

Need the mod on the old beta too? A DLL compiled against one build breaks on the other (the field and the property
never exist together), so keep a branch per game version, or reach the changed members through reflection.

## 6. Release

- Bump the version, so players and mod managers can tell it apart from the pre-v1.1.2 build.
- Say in the description that it's for v1.1.2 and needs Bknibb's UnboundLib 4 (and RoundsWithFriends 3 if you use it):
  the UnboundLib on Thunderstore is still 3.x.
- Keep your `BepInDependency` on `com.willis.rounds.unbound`: UnboundLib 4 kept the same GUID.

## Asset bundles

`scan` also reads the Unity asset bundles a mod ships (embedded in the DLL, or a bundle file next to it that the mod
loads). Components in a bundle are saved against the game's scripts by name, so it checks every component that uses a
game or library script:

- **Scripts the game no longer has** (MANUAL): the component comes up missing when the bundle loads. Scripts the old
  game didn't have either (Stick Fight leftovers, PUN's `PhotonView` saved as if it were in Assembly-CSharp) are only
  a note: they were already missing.
- **Saved fields the current script no longer has**, by name and type (MANUAL): the value is lost on load. In the top
  100 Thunderstore mods there are none; the update kept the fields bundles use.
- **Cards and card frames saved before localization** (REVIEW). The game now shows card text from localization.
  Cards registered through UnboundLib 4's `CustomCard.BuildUnityCard` get theirs filled in; others may show no text.
  A custom card frame (`CardInfoDisplayer`) without `m_localizedNameText` throws when a card is drawn with it: rebuild
  it with `UILocalizedString` text, or use the game's frame.
- **Shaders without a Metal version** (a note): they draw pink on macOS. Build the bundle for macOS too if you can.

Unity's own components (TextMeshPro, UI) are left to Unity, which upgrades their saved data itself.

## What rounds-port can't see

- Behaviour changes that still compile and resolve. Known ones it warns about: object pooling
  (`ObjectsToSpawn.SpawnObject` returns `PoolableWrapper[]`, don't `Destroy()` pooled objects), `TrickShot` setup moved
  to `Start`, `ChangeColor` is an empty marker, Odin Serializer isn't shipped with the game anymore.
- Clashes between mods at runtime. Example: UnboundLib 4's card-pick stats panel calls `ResetStats` on components
  that aren't on any GameObject; a `ResetStats` prefix that calls `GetComponent` throws there. Guard patches that may
  run on such objects (`if (__instance == null) return;` is true for them in Unity).
- Whether a bundle's contents look right in game: the [asset bundle check](#asset-bundles) reads what's saved, not
  how it renders.
