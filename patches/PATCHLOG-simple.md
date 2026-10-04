# PATCHLOG: simple mods (CR, ModsPlus, GunUnblockablePatch, TemporaryStatsPatch, ClassesManagerReborn, RarityLib, CardBarPatch, GrowPatch, PerformanceImprovements)

**Tool:** `compat/compatfix` (.NET 8 + Mono.Cecil 0.11.6). It reads the originals from the game's `BepInEx/plugins` (read-only)
and writes to `staging/patched/<pkg>/<same rel path>`. Each package's other files (manifest, icon, README, LICENSE) are copied
alongside, so every folder is a drop-in replacement. To rebuild, run
`dotnet compat/compatfix/bin/Release/net8.0/compatfix.dll [pkg-substring...]`. The machine log is at `staging/patched/compatfix.log`.

**Helper code:** `compat/compathelpers/Helpers.cs` is compiled for net472 against the game's Managed/ folder. Its methods are
**cloned at the IL level** into each mod that needs them, as `internal static class __RoundsCompat` in the global namespace.
Only the methods each mod uses are cloned, and the result has no new assembly dependencies apart from `Unity.Localization`
(which is already in Managed/).

**Identity:** assembly name and version, `BepInPlugin` GUID/name/version, and all public types are unchanged. This was verified
by diffing the decompiled attributes.

## Private-member access decision

These rewrites never emit direct `ldfld`/`stfld` on private game members. The evidence on whether Unity's Mono skips
JIT access checks is inconclusive. Old RWF 2.2.2 does reference two game fields that are private now
(`ListMenuPage.firstSelected`, `CharacterSelectionInstance.isReady`), but I could not prove they were private in the old build. The
Bknibb UnboundLib 4 fork reaches privates only through reflection. Reflection is safe either way, so:

- Private **writes** (`m_maxHealth`, `m_teamID`) go through `__RoundsCompat.SetMaxHealthRaw` / `SetTeamIDRaw`, which use a
  cached `FieldInfo.SetValue`. These calls are rare (stat changes), so the cost does not matter.
- Private **reads** with a public equivalent go through the public getter (`PlayerID`, `TeamID`, `MaxHealth`).
- `accessaudit` (compat/accessaudit) lists every TypeRef/MemberRef in the patched DLLs that resolves to a non-public
  definition. Only legitimate `protected` base constructors and getters called from subclasses remain.
  ILVerify 8.0 (which also checks accessibility) reports **"All Classes and Methods Verified"** for all 9 patched DLLs. The
  originals had 1 to 160 errors each. Outputs are in `compat/ilverify/`.

## Shared generic rewrites (all 9 mods)

| Old | New |
|---|---|
| `ldfld Player::playerID` | `callvirt Player::get_PlayerID()` |
| `stfld Player::playerID` | `callvirt Player::SetPlayerID(int)` (none occur in these mods) |
| `ldfld Player::teamID` | `callvirt Player::get_TeamID()` |
| `stfld Player::teamID` | `call __RoundsCompat.SetTeamIDRaw` (reflection on `m_teamID`; none occur in these mods) |
| `ldfld CharacterData::maxHealth` | `callvirt CharacterData::get_MaxHealth()` |
| `stfld CharacterData::maxHealth` | `call __RoundsCompat.SetMaxHealthRaw` (reflection on `m_maxHealth`; avoids the Titan achievement) |
| `ldfld CardInfo::cardName` (**now private**; UnboundLib 4 never fills it for mod cards; not in MAPPING) | `call __RoundsCompat.CardName(card)`. Uses the same order as ModdingUtils' `CompatShims.GetCardName`, so cross-mod name comparisons agree: legacy field if non-empty → for an UnboundLib `CustomCard`, the `LocalizedCardName` entry key (= `GetTitle()`) → `CardName` → `name`. A null card still throws NRE, like the old `ldfld` |
| Damage calls missing `DamageSource` | The call instruction is turned into `ldc.i4.0` in place, and the call to the new overload is inserted after it, so branch targets stay valid |
| `ldsfld Optionshandler::vol_Master/vol_Sfx` | `ldstr "OPTION_VOLUME_MASTER"/"OPTION_VOLUME_SFX"; call __RoundsCompat.GetVolume`. This walks `Optionshandler.instance.OptionsData.SettingsList` (no `GetSettingsData`, so it never LogErrors before init) and returns `Clamp01(CurrentValueSliderNormalized)`, i.e. the old linear 0..1 slider value. If the options are not reachable it returns 1f |
| TypeRef scope `[Assembly-CSharp-firstpass] Steamworks.*` | `[com.rlabrecque.steamworks.net]` |
| TypeRef scope `[UnityEngine.CoreModule] UnityEngine.Input` | `[UnityEngine.InputLegacyModule]` |

No `ldflda` sites occurred, so no spills were needed (the tool supports them and logs each one).

## Per mod

### XAngelMoonX-CR-2.7.0 / CosmicRounds.dll: clean
- 65 + 65 `vol_Master`/`vol_Sfx` reads → `GetVolume(...)`.
- 17 `maxHealth` writes → `SetMaxHealthRaw`; 41 reads → `get_MaxHealth`.
- 52 `teamID` reads, 12 `playerID` reads, 91 `cardName` reads → helper or getter.
- Damage calls gained a `DamageSource.Player` argument: `Damagable.CallTakeDamage` ×2, `Damagable.TakeDamage` ×8, `HealthHandler.DoDamage` ×14,
  `HealthHandler.TakeDamageOverTime` ×2, `DamageOverTime.TakeDamageOverTime` ×9.

### willis81808-ModsPlus-1.6.2 / plugins/ModsPlus.dll: clean
- `cardName` ×3 → helper; `maxHealth` read ×1; `teamID` read ×4.

### Pykess-GunUnblockablePatch-0.0.0: clean
- `playerID` read ×1.
- **`HealthHandlerPatchCallTakeDamage.Postfix`:** `RPC("RPCA_SendTakeDamage", ...)` argument array changed from 4 to 5 elements, with
  `(HealthHandler.DamageSource)0` appended. `RPCA_SendTakeDamage` gained a 5th parameter, and PUN 2's `ExecuteRpc` matches
  on exact parameter count (checked in decompiled PhotonNetwork), so the 4-argument RPC would have failed with "wrong
  parameters" and unblockable guns would have stopped dealing damage through block. This was not in MAPPING; it was found manually.

### Pykess-TemporaryStatsPatch-0.0.2: clean
- `maxHealth`: 14 writes → `SetMaxHealthRaw`, 33 reads → `get_MaxHealth`. All Traverse targets (`data`, `isOn`,
  `sinceDealtDamage`) still exist on `StatsAfterDealingDamage`/`StatsWhenFullHP`/`ToggleStats`/`CharacterStatModifiers`.

### Root-Classes_Manager_Reborn-1.5.5: clean
- `playerID` read ×3; `cardName` ×8 → helper.

### Root-RarityLib-1.3.0: clean
- The Steamworks `CSteamID`/`SteamUser` TypeRefs are retargeted to `com.rlabrecque.steamworks.net`, and an AssemblyRef was added.

### BossSloth-CardBarPatch-2.1.1: clean
- The `UnityEngine.Input` TypeRef is retargeted to `UnityEngine.InputLegacyModule`.

### RoundsModding-Grow_Patch-0.0.0: clean
- `[HarmonyPatch(typeof(TrickShot), "Awake")]` → `"Start"`.
- Prefix: `__instance.enabled = false;` inserted before `Destroy(__instance)`. `Destroy` is deferred, and from Start the
  vanilla `TrickShot.Update` would otherwise run once with null `move`/`projectileHit` and throw an NRE.
- `FixedTrickShot.trail` field type changed from `ScaleTrailFromDamage` to `FriendlyFoe.IScaleTrailFromDamage`.
  `Awake` now uses `GetComponentInChildren<IScaleTrailFromDamage>()`. In `FixedUpdate`, `(bool)trail` became
  `trail = __RoundsCompat.FindTrail(this, trail); if (trail != null)`, and `Rescale()` is now called through the interface. The lazy
  lookup is needed because the pooled `ScaleTrailFromDamagePooled` trail is parented in `BulletPoolInstancer.Start`, whose
  order relative to TrickShot is undefined.

### RoundsModding-Performance_Improvements-0.2.0: clean (two features neutralized)
- **`ChangeColorPatchStart` is neutralized:** its HarmonyPatch attributes were removed, so `PatchAll` skips it. `ChangeColor` is now an
  empty marker with no `Start`. "Disable BulletHitSurface particles" still works through the `PlayBulletHit` prefix.
- **`ObjectsToSpawnPatchSpawnObject` is neutralized** (this was not in MAPPING). Its postfix takes `ref GameObject[] __result`, but
  the method now returns `PoolableWrapper[]`. Its only effect was to add a `RemoveAfterPoint` marker. The cleanup hook
  destroys only that marker component, never the object, so the patch was functionally a no-op.
- **`DynamicParticlesPatchPlayBulletHit.Prefix`:**
  - The SpawnObject MemberRef now returns `PoolableWrapper[]`, and the locals were retyped.
  - The tint loop changed from `array[k].transform` to `array[k].Instance.transform`. The game does the same and
    has the same lack of null checks.
  - **The `FixBulletHitParticleEffects` branch was removed** (55 instructions). It added a non-pooled
    `RemoveAfterSeconds(2s)` to every spawned hit effect. That component `Destroy()`s the GameObject, which would kill
    pooled instances and leave dead references in `PrefabPool`. The game already expires these through `RemoveAfterSecondsPooled`.
  - The rate limits and the `DisableBulletHitSurface` early-out are kept.
  - The "Fix BulletHit persistence" toggle still applies to `ProjectileCollisionPatchDie` sparks, which are still `Instantiate`d and not pooled, so the behaviour there is correct.

## Verification
- **compatscan:** run against the target set with the 9 patched DLLs substituted (`compat/SCAN_patched_simple.txt`). All 9 show `0 0 0`
  (unresolved types / members / Harmony+reflection).
- **ILVerify 8.0.0:** run with Managed + core + staged UnboundLib/RWF + plugins as references. All 9 patched DLLs verify with 0 errors.
- **accessaudit:** found no private or internal cross-assembly references.
- **ilspycmd:** I decompiled the touched methods and read through them (PlayBulletHit prefix, FixedTrickShot, TrickShot prefix, the GunUnblockable
  RPC, CR volume/maxHealth/damage sites, `__RoundsCompat`).

## Residual risks
1. CR volume: CR sound intensity is multiplied by master×SFX, and the SFX mixer also attenuates. That double attenuation is
   the same as on the old game. If the slider asset range is not 0..1, `Normalized` still maps it to 0..1.
2. `CardName` for **vanilla** cards depends on whether the serialized legacy `cardName` survived in the assets. If it is empty,
   the localized `CardName` is used, and English-string comparisons could fail in non-English locales. ModdingUtils has the same caveat.
3. PerformanceImprovements `RayHitBulletSound`/`ScreenEdgeBounce` finalizers `Destroy(gameObject)` on an NRE. If one of those
   ever fires on a pooled object, the pool gets a dead entry. This is unchanged mod behaviour and not triggered by the port.
4. Behaviour was not tested in-game, because the game was not launched (per instructions).

## CosmicRounds runtime NRE fixes (2026-10-01), as Harmony patches, not IL

**Where:** `src/MacCompatFixes/CosmicRoundsFixes.cs`, inside the MacCompatFixes plugin so it can be hot-reloaded. `CosmicRounds.dll` is
**unchanged**: both the staged and installed copies are byte-identical to the 2026-09-30 compat build. CR types are resolved by name
at patch time. Each patch class has a `Prepare()` that returns false when its targets are missing, and the patches keep no static state.

**The pooling theory was wrong for bullets.** Bullets are still `PhotonNetwork.Instantiate`d and destroyed by
`ProjectileHit.DestroyMe`. `PrefabPool` only handles `ObjectsToSpawn.effect` hit effects and the trail/collision/spark children made
by `BulletPoolInstancer`. CR uses only two pooled effects. SunCard (an Explosive-bullet clone) and MitosisBlock (a Demonic-pact clone)
never call `RemoveAfterSecondsPooled`, so the pool instantiates fresh copies, the same as the old `Instantiate` path. All 4 errors below
are latent CR 2.7.0 bugs that also existed on the old build.

| Error (Player.log run of 10:55, saved at `scratchpad/cr-runtime/Player-run1.log`) | Root cause | Patch |
|---|---|---|
| `FireballMono.Update` IL_00a5 ×~47k | `Awake` does `player = GetComponent<Player>()` on the **bullet**, which is always null, so the card loop dereferences null every frame for every bullet. `move` is only set when crSpecialVFX is on | `CR_BulletVfxOwner_Fix`, a prefix on `FireballMono/HolyFireMono/BeeSpriteMono.Update`. It refills `player` from `ProjectileHit.ownPlayer`, falling back to `SpawnedAttack.spawner`, and `move` from `GetComponent<MoveTransform>`. If those are unresolved it skips the frame, and it disables the component if it is not on a bullet. HolyFire (Halo) and BeeSprite (Hive) had the identical bug |
| `DarkMono.Start` IL_0006, `MeteorMono.Start` IL_0012 (×1 per pick) | Cards create the AddToProjectile template as a live scene object (`new GameObject("A_Dark", typeof(DarkMono))`). Unity runs `Start` on it with no bullet parent | `CR_ProjectileTemplateStart_Fix`, a prefix on `Start` of the 17 AddToProjectile monos whose Start dereferences a parent component. It skips when `GetComponentInParent<ProjectileHit>() == null`. Each of those Starts threw before assigning anything, and their Updates already check `transform.parent`. The template is **not** disabled, because `Instantiate` copies `enabled` |
| `Explosion.DoExplosionEffects` IL_0377 (×16, mostly at round end) | `BurnMono.Hit` (Flamethrower), `HolyMono` and `FrostMono` `Object.Instantiate` Demonic pact's `A_DemonicExplosion` (Explosion dmg 2, range 4, force 2000, auto, ignoreTeam; prefab inspected with UnityPy) as a visual with **no SpawnedAttack**. `spawned.IsMine()` then throws on null whenever the visual overlaps a player | `CR_OwnerlessExplosion_Fix`, a finalizer on vanilla `Explosion.DoExplosionEffects`. It swallows only a `NullReferenceException` raised while `spawned == null`, and is applied only when CR is loaded. Behaviour up to the throw is unchanged. Players still take nothing, as before, and `Explode` now continues to the remaining colliders instead of aborting |
| (not in log yet) `IceTrailMono.Update` | Its trail has `RemoveAfterSeconds(0.5)`. Afterwards `trail.transform` throws every frame, on bullets older than 0.5 s and forever on the in-scene template | `CR_IceTrailUpdate_Fix`, a prefix that skips Update when `trail == null` |

**Verified offline:** `dotnet build -c Release` is clean. A net8 harness (`scratchpad/cr-runtime/harness`) loads CR plus the
plugin and runs every `Prepare`/`TargetMethods`. All 22 targets resolve, and every `___field` injection matches the field type.
It also generates Harmony's replacement IL through a dry-run MethodPatcher, which showed the correct ldflda/ldfld injection, skip
branches and finalizer try/catch. It could not JIT or detour, because MonoMod.Core has no macOS backend on .NET 8 and CoreCLR
rejects Unity ECalls. **Not tested in-game.**

**Load-order note:** the plugin needs `[BepInDependency("com.XAngelMoonX.rounds.CosmicRounds", SoftDependency)]`. Otherwise, on a
cold start, MacCompatFixes can patch before CosmicRounds.dll is loaded, and the CR patches would silently skip. Hot reload is unaffected.
