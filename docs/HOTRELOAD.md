# Hot Reload (optional)

A BepInEx plugin that swaps mods while ROUNDS is running: rebuild your mod and it's live a second later, no restart.
You don't need it to port a mod; `rounds-port scan` and `fix` work without it.

## Set up

You need BepInEx in the game (see [PORTING.md, step 2](PORTING.md#2-get-a-modded-game-to-test-in)). Then, once:

**Windows** (PowerShell, where `rounds-port.exe` is):

```powershell
.\rounds-port.exe install-hotreload
```

**macOS** (Terminal):

```sh
./rounds-port install-hotreload
```

That copies the plugin to `BepInEx/plugins/HotReload/` and creates `BepInEx/scripts/`. If BepInEx's ScriptEngine is
installed, it's moved aside: both load the same folder. Restart ROUNDS if it was running.

Manual install instead: download `HotReload.dll` from the
[latest release](https://github.com/KieranK07/rounds-porting-toolkit/releases/latest) into
`BepInEx\plugins\HotReload\`.

Remove it: `rounds-port uninstall-hotreload` (mods left in `BepInEx/scripts` then stop loading; move them to
`BepInEx/plugins` to keep them).

## Use

Mods in **`BepInEx/scripts`** (not `plugins`) are hot: they load at startup like any plugin, and reload about a
second after their DLL changes. **F6** in game reloads all of them (on a Mac keyboard **fn+F6**: F6 alone is a
system key there). Removing a DLL from the folder unloads it.

| | Windows | macOS |
|---|---|---|
| Folder | `C:\Program Files (x86)\Steam\steamapps\common\ROUNDS\BepInEx\scripts` | `~/Library/Application Support/Steam/steamapps/common/ROUNDS/BepInEx/scripts` |
| Port + swap in, again on every rebuild | `.\rounds-port.exe hot "bin\Debug\MyMod.dll" --watch` | `./rounds-port hot bin/Debug/MyMod.dll --watch` |
| Already ported (your own build) | copy the DLL into the folder | the same |

`hot` ports the mod for v1.1.2 if it needs it, then writes it into `BepInEx/scripts`. A copy of the same mod in
`BepInEx/plugins` would load twice, so `hot` moves it to `BepInEx/plugins-parked-by-rounds-port`. On Windows the game
locks DLLs it loaded from `plugins`: close ROUNDS once for that move. Hot mods are loaded from memory, so files in
`scripts` are never locked; rebuild over them freely.

**Visual Studio / Rider**: either run `rounds-port hot <your bin\Debug\MyMod.dll> --watch` in a terminal while you
work, or add a post-build step that copies the DLL into the folder:

```xml
<Target Name="CopyToRounds" AfterTargets="Build">
  <Copy SourceFiles="$(TargetPath)" DestinationFolder="C:\Program Files (x86)\Steam\steamapps\common\ROUNDS\BepInEx\scripts" />
</Target>
```

Config (`BepInEx/config/kieran.rounds.hotreload.cfg`): `Folder` (default `scripts`), `LoadOnStart`, `ReloadKey`
(default F6), `Watch`, `Delay` (seconds a file must be quiet before it reloads).

Compared with BepInEx's ScriptEngine, which it replaces:

| | ScriptEngine | Hot Reload |
|---|---|---|
| What reloads | every mod in the folder | the DLL that changed, plus mods that depend on it |
| Needs a `.pdb` | yes (without one the whole reload stops) | no (used for line numbers if it matches) |
| One mod fails | the rest of the reload stops | only that mod is skipped |
| Old copy's patches, cards, menus, events | left in place | removed (below) |
| Assembly name | always renamed | kept on the first load, so other mods' references and RPCs resolve |

## Loading

- At startup, mods in the folder load after every plugin in `BepInEx/plugins` and before RarityLib locks its
  rarities, so they register exactly like normal plugins.
- Each DLL is read with Mono.Cecil, sorted by its `BepInDependency` attributes, and loaded with `Assembly.Load`. A
  plugin whose hard dependency is missing, or whose GUID is already loaded, is skipped with an error.
- Plugins get their own host GameObject, a `PluginInfo` in `Chainloader.PluginInfos`, and their `Info`/`Location`.
- Mono can't unload an assembly, so a reload loads a second copy named `<Name>-hot<n>`. The old copy stays in
  memory with nothing pointing at it.
- `Assembly.LoadFile`/`LoadFrom` on a hot mod's path returns the live copy (Classes Manager Reborn reopens plugin
  DLLs from disk to find class handlers).

## What an unload removes

Every step runs on its own, so one failing doesn't stop the rest. The log line lists the counts.

| What | Where it lives | How |
|---|---|---|
| Harmony patches | any Harmony ID | every patch whose method is in the old assembly |
| MonoMod `On.X.Y +=` | `HookEndpointManager` | `RemoveAllOwnedBy` |
| `Hook`, `ILHook`, `Detour` | the mod's own objects | tracked when applied, disposed |
| Asset bundles | Unity | tracked when opened, `Unload(false)` so the new copy can open them |
| Plugin objects | host GameObject, `PluginInfos`, log sources | destroyed / removed |
| Components | scene and DontDestroyOnLoad objects | components of the old types destroyed, the objects kept |
| Cards | UnboundLib `CardManager.cards`, `activeCards`, `inactiveCards`; `CardChoice.instance.cards`; Photon `DefaultPool.ResourceCache` | removed, card object destroyed |
| Card registries | ModdingUtils `hiddenCards`; RarityLib `CardRarities*`; Classes Manager `Registry`, `ClassInfos`; ModsPlus `customAbbreviations` | removed |
| Toggle-cards menu | `ToggleCardsMenuHandler` | card buttons parked (`cardObjs` and `defaultCardActions` are index-aligned); an empty category's button, page and lists removed |
| Game-mode hooks and handlers | `GameModeManager.hooks`, `onceHooks`, `handlers` | entries whose callback is the mod's |
| Networking | `NetworkingManager.events`, handshake actions, RPC cache | the mod's events and its `ModLoader_<guid>` handshake |
| MODS menu, GUI | `ModOptions.modMenus`, `GUIListeners` | entry and its button |
| Credits, update check, client-side flag | `Credits`, `UpdateChecker`, `SyncModClients` | the mod's registration |
| ModdingUtils callbacks | `Cards.instance.removalCallbacks`, `cardValidationFunctions`; `InterfaceGameModeHooksManager` | the mod's entries |
| Network prefabs | Photon pool | the mod's non-card prefabs (registering a name that exists keeps the old prefab) |
| Everything else shared | static fields of the game, Unity's core module and every plugin, and singletons they hold | delegates from the old assembly stripped; list, dictionary, array and set entries that are its objects, its callbacks, or objects destroyed above removed |

Library state is reached by type and field name through reflection, so a library that isn't installed is skipped.
The names come from the decompiled libraries: UnboundLib 4.2.5, ModdingUtils 0.4.8, RarityLib, Classes Manager
Reborn, ModsPlus.

## After a live load

What normally happens once at boot is redone for the new copy, once its cards stop appearing (`BuildCard` finishes
a few frames late):

- its cards go into the toggle-cards menu with their saved on/off state; a new category gets a button copied from
  a sibling, and the menu is re-sorted
- `RarityAdder`/`ThemeAdder` components, credits page, MODS button, `FirstStartCallbacks`
- Classes Manager: other mods' class definitions are pointed at the new cards; `ClassHandler.Init`/`PostInit` run
- RarityLib: `AddRarity` for a rarity that already exists returns it instead of throwing

## Limits

- Mods in `BepInEx/plugins` load through BepInEx and aren't swappable. Move one to `BepInEx/scripts` and restart once.
- A mod loaded for the first time mid-game can't add a new rarity (RarityLib locks at startup). After one restart
  with it in the folder, every reload works.
- Players keep the cards they already hold until the next round.
- Online: a reloaded copy has a different assembly name. Restart before playing with others.
- State a mod keeps somewhere not listed above (another mod's private field, a file) isn't undone.

## Adding a library

Unload goes in `Libraries.Unload` (`Libraries.cs`), redo-at-boot in `Libraries.AfterLiveLoad`. Use the `T`/`S`/`I`
reflection helpers so the plugin still runs without that library, and `Owned(delegate, assembly)` to match callbacks,
including ones wrapped in a library's compiler-generated closure.
