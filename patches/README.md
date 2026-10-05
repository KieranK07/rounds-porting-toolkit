# Hand-made patches

Fixes for specific releases of mods that `rounds-port fix` can't port by itself. Each one is a binary diff
(BSDIFF40) from the exact Thunderstore file to its fixed copy, so nothing here contains a mod.

[DuctTape](https://github.com/KieranK07/DuctTape)'s AutoFix applies them for any mod manager (its `scripts/curated.py`
copies them in), and Crosswind and the modpack take them from here too.

## patches.tsv

One line per file, tab-separated:

```
<package>-<version>/<path in the package>   <sha256 before>   <sha256 after>   <patch file>   [macos]
```

A patch is only applied to a file whose SHA-256 matches `before`, and the result has to match `after`. The optional
fifth column `macos` limits a patch to macOS (UnboundLib's Windows-only "hold Left Shift" check).

## What made each patch

| Patches | How | Changes |
|---|---|---|
| CardBarPatch, Classes Manager Reborn, Cosmic Rounds, GrowPatch, GunUnblockablePatch, ModsPlus, Performance Improvements, RarityLib, TemporaryStatsPatch | IL rewrites with `tools/compatfix` + `tools/compathelpers` | [PATCHLOG-simple.md](PATCHLOG-simple.md) |
| MapsExtended, Will's Wacky Map Objects | `tools/mapsextended-patcher` | [PATCHLOG-maps.md](PATCHLOG-maps.md) |
| ModdingUtils | rebuilt from source with `tools/moddingutils/moddingutils-0.4.8-compat.patch` | [PATCHLOG-moddingutils.md](PATCHLOG-moddingutils.md) |
| GunChargePatch, ProjectileChargePatch | `tools/guncharge-patcher` | [PATCHLOG-guncharge.md](PATCHLOG-guncharge.md) |
| UnboundLib 4.2.5 and 4.2.7 (macOS) | `tools/unboundlib-macfix` | [PATCHLOG-macfix.md](PATCHLOG-macfix.md) |
| UnboundLib 3.2.14, MMHook 1.0.0, RoundsWithFriends 2.2.2 | `bsdiff` to Bknibb's ports | [PATCHLOG-libraries.md](PATCHLOG-libraries.md) |

Licences of the patched mods and of these changes: [NOTICE.md](NOTICE.md).
