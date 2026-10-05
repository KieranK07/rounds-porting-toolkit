# Third-party notices

No mod files are stored here. Each patch is applied on the player's machine to the original file, downloaded from
Thunderstore or the author's release and checked by SHA-256 first. If you're an author and want a patch changed or
removed, open an issue.

| Patched | Licence of the original | These changes |
|---|---|---|
| ModdingUtils 0.4.8 (Pykess, [pdcook/ModdingUtils](https://github.com/pdcook/ModdingUtils)) | GPL-3.0 | Every source change is in `tools/moddingutils/moddingutils-0.4.8-compat.patch`, GPL-3.0 |
| CardBarPatch 2.1.1 (BossSloth), Performance Improvements 0.2.0 (RoundsModding) | GPL-3.0 | IL rewrites by `tools/compatfix` + `tools/compathelpers` (source here, GPL-3.0 for these changes), listed in `PATCHLOG-simple.md` |
| UnboundLib 4.2.5 and 4.2.7 ([Bknibb/UnboundLib](https://github.com/Bknibb/UnboundLib)) | none stated | One call to a Windows-only API replaced (`tools/unboundlib-macfix`, `PATCHLOG-macfix.md`) |
| UnboundLib 3.2.14, MMHook 1.0.0 (willis81808), RoundsWithFriends 2.2.2 (olavim) | UnboundLib: none stated; RoundsWithFriends: GPL-3.0 | MMHook and RoundsWithFriends become Bknibb's ports, used with his OK: [Bknibb/UnboundLib](https://github.com/Bknibb/UnboundLib) 4.2.7's MMHOOK, [Bknibb/RoundsWithFriends](https://github.com/Bknibb/RoundsWithFriends) 3.0.10 (GPL-3.0, source there). UnboundLib becomes our fork of Bknibb's 4.2.7 ([KieranK07/UnboundLib](https://github.com/KieranK07/UnboundLib/tree/ducttape), source there); `PATCHLOG-libraries.md` |
| MapsExtended 1.4.2 (olavim) | MIT, plus BSD-3-Clause for its bundled NetTopologySuite | `tools/mapsextended-patcher`, `PATCHLOG-maps.md` |
| Classes Manager Reborn, Cosmic Rounds, GrowPatch, GunChargePatch, GunUnblockablePatch, ModsPlus, ProjectileChargePatch, RarityLib, TemporaryStatsPatch, Will's Wacky Map Objects | each author's own | Listed in the matching `PATCHLOG-*.md` |

The Odin Serializer stand-in (`../src/OdinStandIn`, built into `../odin`) is built from
[TeamSirenix/odin-serializer](https://github.com/TeamSirenix/odin-serializer) at ba19025 (Apache-2.0, © Sirenix IVS),
modified: namespaces renamed to `Sirenix.Serialization` / `Sirenix.Utilities` and split into the three assemblies
MapsExtended expects. Licence: `../src/OdinStandIn/LICENSE`.

ROUNDS is © Landfall Games. Not affiliated with or endorsed by Landfall Games or any mod author.
