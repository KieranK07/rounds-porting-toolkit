# Old libraries to the current-game ports

The newest old releases of the three libraries most mods depend on, turned into ports for the current game:
[Bknibb](https://github.com/Bknibb)'s, and for UnboundLib our fork of his. With these, DuctTape players keep the packages their mods already depend on.

| Patch | From (Thunderstore) | To |
|---|---|---|
| `willis81808-UnboundLib-3.2.14~plugins~UnboundLib.dll` | willis81808-UnboundLib 3.2.14 | `UnboundLib.dll` built from [KieranK07/UnboundLib](https://github.com/KieranK07/UnboundLib/tree/ducttape) `ducttape` (Bknibb's 4.2.7 plus fixes, below) |
| `willis81808-MMHook-1.0.0~plugins~MMHOOK_Assembly-CSharp.dll` | willis81808-MMHook 1.0.0 | `MMHOOK_Assembly-CSharp.dll` of the same release |
| `olavim-RoundsWithFriends-2.2.2~plugins~RoundsWithFriends.dll` | olavim-RoundsWithFriends 2.2.2 | `RoundsWithFriends.dll` of [RoundsWithFriends 3.0.10](https://github.com/Bknibb/RoundsWithFriends/releases/tag/v3.0.10) |

Made with `bsdiff <old> <new>` from the exact files; MMHook and RoundsWithFriends give Bknibb's release files byte for byte. Bknibb agreed
to his ports being built into DuctTape with credit ([UnboundLib#1](https://github.com/Bknibb/UnboundLib/issues/1)).
UnboundLib 4 also needs `Octokit.dll` from the same release (MIT), which DuctTape ships as it is.

2026-10-05: the UnboundLib patch goes to 4.2.7 instead of 4.2.5 (4.2.6 added a card bar toggle, 4.2.7 is the same
code built as Release). Its `MMHOOK_Assembly-CSharp.dll` and `Octokit.dll` are the same files as 4.2.5's.

2026-10-05, later: UnboundLib comes from our fork of Bknibb's 4.2.7 (`ducttape` branch, commit c023cdd, built with
`dotnet build -c Release` against the current game: the same sha256 every build). Its changes:
- Escape in the escape menu's MODS pages goes back a page again (it did nothing on the current game).
- Toggle Cards / Toggle Levels open in front: the map showed through them in sandbox, and opened from the escape menu
  they were drawn behind it.
- The hold-Left-Shift check only runs on Windows, so the macOS fix ([PATCHLOG-macfix.md](PATCHLOG-macfix.md)) isn't
  needed for this build.
- No "update available" line for Bknibb's releases: players update DuctTape, not UnboundLib.
- A rematch no longer stops when a player's card list holds a card that's already destroyed (it threw in the
  `Player.FullReset` postfix and left the lobby stuck).
- Sandbox waits for its map before adding a player: the current game lets players join before the first map has
  loaded, so with a custom map the player often never spawned.

Bknibb's own 4.2.5 and 4.2.7, when installed as they are, still get the macOS fix on a Mac.
Older releases of the three libraries aren't patched: DuctTape asks the player to update them.
