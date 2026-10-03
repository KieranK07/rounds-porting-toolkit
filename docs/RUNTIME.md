# rounds-port Runtime (in testing)

A BepInEx plugin for problems `fix` can't solve in a mod's DLL: old mods that load on the current game but then throw,
or draw wrong, while running together. Each patch puts back what the old game did. It adds nothing to the menus,
registers no credits, and writes to the log as `rounds-port`.

Install: `rounds-port.Runtime.dll` in `BepInEx/plugins/`. Needs BepInEx 5.4.23 and the current game. If the
rounds-mac-modpack's Mac Compat Fixes is installed, BepInEx skips this plugin: that one has the same fixes.

| Fix | What goes wrong without it |
|---|---|
| Shaders (Metal only) | Mod asset bundles only have D3D11 shaders, so on macOS their cards and effects draw pink. Materials are pointed at the game's own Metal shaders, or rebuilt on `Particles/Standard Unlit` |
| Letterbox | On 16:10 screens nothing clears the bars around the 16:9 picture, so old frames stay there |
| Card names | UnboundLib's cards have no entry in the game's string table: titles show as the missing-translation text |
| `GetSourceCard` | Picked cards don't always carry the `(Clone)` name UnboundLib looks for: empty card bar buttons |
| Card bar hover | A button whose card was destroyed throws on hover |
| Stats panel | UnboundLib 4.2.5's stats panel calls `ResetStats` on detached components; mods patching `ResetStats` throw, and the first card pick never shows |
| Card visuals | RarityLib throws on destroyed rarity markers; the new card prefab's particles cover cards in the toggle-cards menu; menu card art doesn't animate on hover; selected menu buttons turn white |
| MapsExtended | Its object manager is destroyed at startup, so clients load custom maps without their physics objects |
| Cosmic Rounds 2.7.0 | Bullet effects look for the owner on the bullet, card templates run `Start` with no bullet, ice trails outlive their object, hit effects explode with no owner: thousands of errors a round |
| `CardBar.Update` patches | The game's `CardBar` has no `Update` now. AutoFix disables patches on it (they'd stop the mod's `PatchAll`); these call them every frame for each active card bar, as `Update` did (LocalZoom keeps the hovered card's zoom in step with the camera) |
| Cards Plus | Its Cyberpunk cards' effect also lands on the card prefab UnboundLib builds, which has no visual, and throws there at startup; skipped on the prefab only |

Source: `src/Runtime`. Patches whose target mod isn't installed are skipped. Safe to swap with Hot Reload: every load
patches under its own Harmony id and undoes everything when it unloads.
