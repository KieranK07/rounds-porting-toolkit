# Rounds Port

Play mods made before the December 2025 update on the current game, without waiting for new versions of them.
Install it next to your mods. Nothing to set up.

## What it does when the game starts

- **Libraries.** The old UnboundLib 3, MMHook and RoundsWithFriends 2 that most mods depend on don't work on the
  current game. They're swapped for [Bknibb](https://github.com/Bknibb)'s ports (UnboundLib 4.2.5,
  RoundsWithFriends 3.0.10), downloaded once from Bknibb's GitHub releases and checked by SHA-256.
- **Mods.** Code the update renamed or removed (`playerID`, `maxHealth`, damage methods, card names and about 30
  more) is rewritten in each old mod's DLL, the way a mod author would port it. This is the `fix` from
  [rounds-port](https://github.com/KieranK07/rounds-porting-toolkit). Each mod is checked once; the first start takes a
  few seconds longer (about 4 seconds for 70 mods), later ones don't.
- **Hand-made fixes** for exact versions that needed more: Cosmic Rounds 2.7.0, MapsExtended 1.4.2, ModdingUtils 0.4.8,
  Classes Manager Reborn 1.5.5, RarityLib 1.3.0, ModsPlus 1.6.2, Will's Wacky Map Objects 1.2.4, CardBarPatch 2.1.1,
  GunChargePatch 0.0.4, Performance Improvements 0.2.0, and a few small patches.
- **In game:** fixes for problems that only show while playing. Card names showing as missing translations, empty
  card bar buttons, the first card pick not showing, Pick Phase Improvements and Pick N Cards stuck in the pick phase,
  MapsExtended maps without their physics objects for clients, modded cards out of sync between players online,
  Cosmic Rounds throwing thousands of errors a round.

Which of the 98 most-downloaded mods were tested and how they did:
[COMPATIBILITY.md](https://github.com/KieranK07/rounds-porting-toolkit/blob/main/docs/COMPATIBILITY.md).
- Adds Odin Serializer, which the game no longer ships (MapsExtended, Will's Wacky Cards and others use it).
- Turns on `HideManagerGameObject` in `BepInEx.cfg`. Without it the current game destroys mods' objects.

Mods behave as they did on the old game. Nothing is added to menus or credits.

## Updated packages win

If a package brings its own newer UnboundLib or RoundsWithFriends, that one is used and the old files are left alone.
Mods that already have a version for the current game load as they are.

## Turning it off

- **Old game build** (Steam beta `old-rounds-for-mods`): it notices, puts every original file back and does nothing
  else.
- `BepInEx/config/rounds-port.autofix.cfg`:
  - `Exclude`: mods never to touch (`MapsExtended.dll` or `olavim-MapsExtended`).
  - `RestoreOriginals = true`: puts every original back on the next start and turns this off.
- Originals are kept in `BepInEx/cache/rounds-port`.

## Not fixed

- Mods that need their author: some flag "MANUAL" problems in `BepInEx/LogOutput.log` (the line says which). Most
  still load; the part that uses the removed code doesn't work.
- Bugs mods already had on the old game stay.

Problems: open an issue on [GitHub](https://github.com/KieranK07/rounds-porting-toolkit/issues) with your
`BepInEx/LogOutput.log`.

## Credits

Bknibb's [UnboundLib](https://github.com/Bknibb/UnboundLib) and
[RoundsWithFriends](https://github.com/Bknibb/RoundsWithFriends) ports. Hand-made fixes from rounds-mac-modpack.
[Odin Serializer](https://github.com/TeamSirenix/odin-serializer) (Apache 2.0, license included).
Built with [Mono.Cecil](https://github.com/jbevain/cecil) and [Harmony](https://github.com/pardeike/Harmony).
ROUNDS is © Landfall Games; not affiliated with Landfall.
