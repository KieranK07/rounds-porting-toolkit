# UnboundLib 4.2.5 (Bknibb) — macOS fix
UnboundLib's Unbound() constructor P/Invokes user32.dll!GetAsyncKeyState (Windows-only; "hold Left Shift to skip loading").
On macOS this throws DllNotFoundException and aborts the constructor, so none of UnboundLib's hooks
(menus, CardChoice.Start card registration, etc.) get installed. Symptoms: dev-console card spawns fail
("DefaultPool failed to load ..."), EscapeMenuHandlerPath.Update NREs, ExecuteAfterSeconds NREs.
Fix: the call is replaced by `pop; ldc.i4.0` (key never held) and the P/Invoke + user32 ModuleRef are removed.
Original DLL kept as notes/UnboundLib.original-bknibb-4.2.5.dll. Safe on Windows too.

# BepInEx.cfg: HideManagerGameObject = true
The current game destroys BepInEx's manager GameObject early on, so every plugin's instance
becomes Unity-null (NREs in StartCoroutine/IsObjectMonoBehaviour, RWFMod.GetSoundEnabled, GameModeManager.SetupUI),
leaving no player spawn and no cards. Copy ROUNDS-modfix/BepInEx.cfg to <game>/BepInEx/config/.

# MacCompatFixes plugin (src in ROUNDS-modfix/src/MacCompatFixes)
- GetSourceCard fallback (postfix, last): UnboundLib requires name == "<card>(Clone)"; cards spawned now
  don't always match, so sourceCard was null -> card bar buttons with no card -> hover NRE in AddCardVisual.
- CardInfo.CardName: UnboundLib never adds its titles to StringTableCards, so CardName returned the
  missing-translation text ('Title' in quotes). Returns the title instead; fixes card-bar letters and
  dev-console name matching for mod cards.
- 1.1.0 (rev): only swaps shaders that are unsupported on this GPU; falls back to closest built-in
  (TMP / particles additive / particles alpha / UI / sprites). Every swap is logged in LogOutput.log.
- 1.2.0: mod bundles only contain D3D11 shader programs. Materials are re-pointed at the game's own
  Metal shader objects (TextMeshPro/*, Particles/Standard Unlit) — looked up by object + isSupported,
  since bundle copies share the names. CosmicRounds-only shaders rebuilt on Particles/Standard Unlit:
  Legacy Particles/Additive (SrcAlpha One, 2x tint), Custom/Add (One One), Custom/Opacity2 (opaque),
  Standard (keeps material's own _SrcBlend/_DstBlend/_ZWrite). Sweep runs every 0.5s for runtime materials.
- 1.3.0: shader.isSupported is unreliable for bundle shaders (true until first failed draw), so shaders
  from mod bundles are tracked as "foreign" and always replaced with the game's own copy (sharedassets0)
  or rebuilt. Fix runs on every AssetBundle.LoadFrom* and on new Material(...) — no first-frame pink.
  Card bar: remembers each button's card and re-resolves a destroyed CardInfo on hover (skips instead of throwing).
  Each Harmony patch class is applied in isolation so one failure can't disable the plugin.
- 1.8.2: first card pick of a game didn't show (any platform). UnboundLib 4.2.5's stats panel calls ResetStats on
  components made with `new`; other mods' ResetStats patches call GetComponent there and throw, which escaped the
  panel's static constructor and RoundsWithFriends' DoStartGame. ResetStats failures on such detached components are
  now ignored (finalizer), and any panel error is logged instead of stopping the pick (StatsViewerFixes.cs).
- 1.8.3: UnboundLib's Discord and Thunderstore links on the main menu are shown again (no longer hidden).
- 1.8.4: online custom maps: players who aren't the host now get the maps' physics objects (boxes, ropes, balls,
  saws). MapsExtended's map-object manager gets destroyed on the current build, and a client's map load stopped at the
  first networked object; its sync coroutine now runs on Mac Compat Fixes' helper object. Not Mac-specific.
