# Old libraries to Bknibb's ports

The newest old releases of the three libraries most mods depend on, turned into [Bknibb](https://github.com/Bknibb)'s
ports for the current game. With these, DuctTape players keep the packages their mods already depend on.

| Patch | From (Thunderstore) | To |
|---|---|---|
| `willis81808-UnboundLib-3.2.14~plugins~UnboundLib.dll` | willis81808-UnboundLib 3.2.14 | `UnboundLib.dll` of [UnboundLib 4.2.5](https://github.com/Bknibb/UnboundLib/releases/tag/v4.2.5) |
| `willis81808-MMHook-1.0.0~plugins~MMHOOK_Assembly-CSharp.dll` | willis81808-MMHook 1.0.0 | `MMHOOK_Assembly-CSharp.dll` of the same release |
| `olavim-RoundsWithFriends-2.2.2~plugins~RoundsWithFriends.dll` | olavim-RoundsWithFriends 2.2.2 | `RoundsWithFriends.dll` of [RoundsWithFriends 3.0.10](https://github.com/Bknibb/RoundsWithFriends/releases/tag/v3.0.10) |

Made with `bsdiff <old> <new>` from the exact files; each result is byte for byte Bknibb's release file. Bknibb agreed
to his ports being built into DuctTape with credit ([UnboundLib#1](https://github.com/Bknibb/UnboundLib/issues/1)).
UnboundLib 4 also needs `Octokit.dll` from the same release (MIT), which DuctTape ships as it is.

On macOS the UnboundLib result then gets `Bknibb-UnboundLib-4.2.5~UnboundLib.dll` as well ([PATCHLOG-macfix.md](PATCHLOG-macfix.md)).
Older releases of the three libraries aren't patched: DuctTape asks the player to update them.
