# ROUNDS v1.1.2: old → new game API (detailed)

What the v1.1.2 update changed for mods, member by member, with the IL rewrite `rounds-port fix` uses and how sure we
are of it. Worked out while porting the 31 mods of [rounds-mac-modpack](https://github.com/KieranK07/rounds-mac-modpack)
against the current game, Bknibb's UnboundLib 4.2.5 and RoundsWithFriends 3.0.10. For the short version, see the
table in the [README](../README.md#what-fix-handles) and [PORTING.md](PORTING.md).

UnboundLib 3 → 4 itself breaks nothing between mods: every reference from the ported mods into UnboundLib and MMHOOK
resolves against 4.2.5, and the GUID `com.willis.rounds.unbound` is unchanged, so `BepInDependency` still matches.

Conventions: `→` is the IL rewrite. Confidence: H = verified in the new code with the same semantics; M = works but
semantics differ or depend on asset data; L = guess.

---

## 1. Field → property renames (Assembly-CSharp)

| Old ref | New member | IL rewrite | Writers? | Conf |
|---|---|---|---|---|
| `int32 Player::playerID` (field) | `private int m_playerID`; `public int PlayerID {get;}` (no setter); `public void SetPlayerID(int)` (plain assign); `public void AssignPlayerID(int)` (assign + SetColors + writes **local** Photon custom props) | read: `ldfld Player::playerID` → `callvirt instance int32 Player::get_PlayerID()`. Write: `stfld` → `callvirt Player::SetPlayerID(int32)` (stack shape is the same: obj, value) | ModdingUtils `PlayerPatchAssignPlayerID.Prefix` (`__instance.playerID = ID`). Use `SetPlayerID` there, **not** AssignPlayerID, because it is inside a prefix on AssignPlayerID | H |
| `int32 Player::teamID` (field) | `private int m_teamID`; `public int TeamID {get;}`; `AssignTeamID(int)` (assign + local Photon props). **No plain public setter** | read: `ldfld Player::teamID` → `callvirt instance int32 Player::get_TeamID()`. Write: `stfld Player::teamID` → `stfld int32 Player::m_teamID` (private, but Mono does not enforce field access, same as publicized refs). If that is unacceptable, emit `AccessTools.FieldRefAccess<Player,int>("m_teamID")` | ModdingUtils `PlayerPatchAssignTeamID.Prefix` only. **Must not** become `AssignTeamID`: that recurses into its own prefix, and it also writes the LOCAL player's Photon props for a remote/AI player | H (read) / M (write: private field) |
| `float32 CharacterData::maxHealth` (field) | `[FormerlySerializedAs("maxHealth")] private float m_maxHealth`; `public float MaxHealth {get; set;}`. The setter does `if (player.IsLocal && value >= 1000f) PlatformManager.UnlockAchievement(Titan)` and then assigns | read: `ldfld` → `callvirt instance float32 CharacterData::get_MaxHealth()`. Write: `stfld` → `callvirt instance void CharacterData::set_MaxHealth(float32)`. Compound `x.maxHealth *= k` compiles to `dup; ldfld; …; stfld`, and swapping both instructions keeps the stack valid | TemporaryStatsPatch (+=/-=), CosmicRounds (`*=` ×20), ModdingUtils (`+=`, `-=`, AI setup) | H. Side effects: (a) the setter can unlock the Titan achievement; (b) it dereferences `player`, which is set in `CharacterData.Awake`. All mod call sites run after Awake. To avoid the achievement, use `stfld m_maxHealth` instead (M) |
| ModdingUtils transpiler `AccessTools.Field(typeof(Player),"playerID")` (Gun_PatchSendPlayerID, patches `Gun.<FireBurst>d__.MoveNext`) | same as above | String rename alone is **not enough**. The emitted `new CodeInstruction(OpCodes.Ldfld, playerID)` must become `new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Player),"PlayerID"))`. In IL: change the `ldstr "playerID"` + `AccessTools::Field` call to `ldstr "PlayerID"` + `AccessTools::PropertyGetter` (returns MethodInfo), and change `ldsfld OpCodes::Ldfld` to `OpCodes::Callvirt` for that 2nd instruction. FireBurst still has the matched pattern (`GetComponent<PhotonView>()` → `ldstr "RPCA_Init*"` → `holdable.holder.view.OwnerActorNr`), so the anchor still matches. Without the fix, `AccessTools.Field` returns null, the CodeInstruction gets a null operand, and the patch throws at apply time | – | M |

## 2. Signature changes (added trailing `HealthHandler.DamageSource damageSource = Player`)

`HealthHandler.DamageSource` is a nested enum `{ Player = 0, OutOfBounds = 1 }`. For every row, insert `ldc.i4.0`
immediately before the `call`/`callvirt` and retarget the MemberRef to the new signature. Behaviour matches the old
default.

| Old MemberRef | New | Used by | Conf |
|---|---|---|---|
| `void Damagable::CallTakeDamage(V2,V2,GameObject,Player,bool)` | `…,bool, HealthHandler/DamageSource)` (abstract; HealthHandler/DamagableEvent/Destructible override) | WillsWackyMapObjects, CosmicRounds | H |
| `void Damagable::TakeDamage(V2,V2,GameObject,Player,bool,bool)` | `…,bool,bool, DamageSource)` | CosmicRounds | H |
| `void HealthHandler::DoDamage(V2,V2,Color,GameObject,Player,bool,bool,bool)` | `…,bool,bool,bool, DamageSource)` | CosmicRounds | H |
| `void HealthHandler::TakeDamageOverTime(V2,V2,float,float,Color,GameObject,Player,bool)` | `…,bool, DamageSource)` | ModdingUtils, WillsWackyMapObjects, CosmicRounds | H |
| `void DamageOverTime::TakeDamageOverTime(V2,V2,float,float,Color,SoundEvent,GameObject,Player,bool)` | `…,bool, DamageSource)` | CosmicRounds | H |

Harmony patches that target these methods by name (GunUnblockablePatch on `HealthHandler.CallTakeDamage`/`DoDamage`, and
others) still bind, because their parameter names are a prefix subset of the new list. The scanner verified the
injected-parameter names.

## 3. Return-type change: object pooling

| Old | New | Rewrite | Conf |
|---|---|---|---|
| `GameObject[] ObjectsToSpawn::SpawnObject(Transform,HitInfo,ObjectsToSpawn,HealthHandler,PlayerSkin,float,SpawnedAttack,bool)` | `FriendlyFoe.PoolableWrapper[] SpawnObject(...same params...)`. `PoolableWrapper { int PoolId; GameObject Instance; IPrefabPoolable[] Poolables; }`. Entries can be **null** (skipped spawns). Instances come from `PrefabPool` and are reused | **ModdingUtils** (`ProjectileHitPatchRPCA_DoHit.Postfix`, return value `pop`ped): retarget the MemberRef return type only. **PerformanceImprovements** (`DynamicParticlesPatchPlayBulletHit.Prefix` iterates the result and adds `RemoveAfterSeconds`/`RemoveAfterPoint`, which **Destroy** the object): change the local to `PoolableWrapper[]`, use `.Instance`, and null-check. Better: drop the "FixBulletHitParticleEffects" branch entirely. The game now manages hit-effect lifetime through pooling (`RemoveAfterSecondsPooled`), and destroying a pooled instance leaves a dead reference in `PrefabPool` | H (ModdingUtils) / M (PerfImprovements semantics) |

## 4. Removed / renamed methods targeted by Harmony

| Patch | Old target | Now | Action | Conf |
|---|---|---|---|---|
| ModdingUtils `CardChoicePatchGetRanomCard` | `CardChoice::GetRanomCard()` | `private GameObject CardChoice::GetRandomCard()`. Same weighting body (Common 10 / Uncommon 4 / Rare 1); only the typo was fixed. `SpawnUniqueCard` calls it | Change the attribute string `"GetRanomCard"` → `"GetRandomCard"` | H |
| GrowPatch `TrickShotPatchAwake` (prefix replaces TrickShot with FixedTrickShot) | `TrickShot::Awake` | No Awake. Trail lookup moved to `Start()`, and `trail` is now `IScaleTrailFromDamage` (implemented by `ScaleTrailFromDamage` and the new `ScaleTrailFromDamagePooled`). Game still uses `Update` + `deltaTime` (the bug the mod fixes) | Retarget to `"Start"` (private void, no args). Also make `FixedTrickShot.trail` use `GetComponentInChildren<IScaleTrailFromDamage>()`. Otherwise pooled bullets (ScaleTrailFromDamagePooled) are not rescaled | H (retarget) / M (trail type) |
| PerformanceImprovements `ChangeColorPatchStart` | `ChangeColor::Start` | `ChangeColor` is now an **empty** marker MonoBehaviour. `DynamicParticles.PlayBulletHit` uses it only to find a renderer to tint | **No equivalent.** Remove the patch class. Its purpose (destroying or limiting hit particles) is covered by the existing `DynamicParticles.PlayBulletHit` prefix plus game pooling | H (no equivalent) |
| MapsExtended `CardBarPatch_OnHover_Patch` | `CardBar::OnHover` (one overload, field `currentCard`) | Two overloads: `bool OnHover(int)` and `void OnHover(CardBarButton)`, the first calling the second. The field is `m_currentCard` | Add `argumentTypes: new[]{typeof(CardBarButton)}`, rename the param `___currentCard` → `___m_currentCard`. Without args, Harmony throws AmbiguousMatchException (no parameterless fallback). This is what the Bknibb UnboundLib fork does | H |
| ModdingUtils `CardBarUtils.RPCA_ShowCard` direct call | `void CardBar::OnHover(CardInfo, Vector3)` | Gone. Hover now needs a `CardBarButton` (`m_cardInfo` + `transform`). Body: destroy `m_currentCard`; `m_currentCard = CardChoice.instance.AddCardVisual(info, cardPos.position)`; disable colliders; sortingLayer "MostFront"; SetScaleToZero off and scale 1.15; `CardBarHandler.instance.SetSelection(button.position)`; `DoesHover = true` | **No direct equivalent.** Replace with a helper that does the same steps with a CardInfo, minus `SetSelection` (it sets `m_currentCard` through reflection). Then `Traverse.Field("currentCard")` → `"m_currentCard"` | M |
| ModdingUtils `SilentAddToCardBar` (two copies) via Traverse | CardBar fields `ci`, `source`; CardBarButton field `card` | `ci` **removed** (no per-bar card cache); `source` → `m_source`; CardBarButton `card` → public `m_cardInfo`. New `AddCard` also inserts the button into `m_cards` (a private `List<CardBarButton>`) for D-pad hover. The label is now `UILocalizedString.Text` | Drop the `ci` line (Traverse on a missing field silently no-ops). `"source"` → `"m_source"` (otherwise GetValue returns null, then NRE). `"card"` → `"m_cardInfo"`. Optionally insert into `m_cards`. The scanner **missed** these because the names exist on other game types; they were found by hand | H (renames) / M (m_cards) |

## 5. Removed static fields

| Old | New | Rewrite | Conf |
|---|---|---|---|
| `static float32 Optionshandler::vol_Master`, `vol_Sfx` (CosmicRounds, about 60 `ldsfld` sites, e.g. `scale * vol_Master * vol_Sfx / 1.2f * CR.globalVolMute`) | Options rewritten to a `OptionsData` ScriptableObject: `Optionshandler.instance.OptionsData.GetSettingsData("OPTION_VOLUME_MASTER" / "OPTION_VOLUME_SFX").CurrentValueSlider`. These drive `SoundVolumeManager.SetAudioMixerVolume*` (log10 → dB). No static float is kept | Inject one static helper into CosmicRounds, e.g. `static float CompatVol(string key){ var o = Optionshandler.instance?.OptionsData?.GetSettingsData(key); return o == null ? 1f : o.CurrentValueSliderNormalized; }`. Replace each `ldsfld Optionshandler::vol_Master` with `ldstr "OPTION_VOLUME_MASTER"; call CompatVol` (same for SFX). The SoundVolumeManager mixer already applies the user's volume, so returning a constant `1f` is also defensible and avoids double attenuation | M. Slider range comes from asset data; `Normalized` guarantees 0..1. Old semantics of linear 0..1 are assumed |

## 6. Moved assemblies (TypeRef scope only, members unchanged)

| Old scope | New | Rewrite | Users | Conf |
|---|---|---|---|---|
| `[Assembly-CSharp-firstpass] Steamworks.CSteamID`, `Steamworks.SteamUser` (+ `CSteamID::m_SteamID` ulong, `SteamUser::GetSteamID()`) | `com.rlabrecque.steamworks.net.dll` (Steamworks.NET). Same namespace, same members (verified) | Add AssemblyRef `com.rlabrecque.steamworks.net, Version=0.0.0.0` and set those TypeRefs' ResolutionScope to it. Firstpass has **no** type forwarders, so this fails with TypeLoadException today | RarityLib (dev easter egg in one method) | H |
| `[UnityEngine.CoreModule] UnityEngine.Input` (`GetKeyDown(KeyCode)`) | `UnityEngine.InputLegacyModule.dll`. CoreModule has no forwarder; the `UnityEngine.dll` facade does | Set the TypeRef scope to AssemblyRef `UnityEngine.InputLegacyModule` (or `UnityEngine`, which forwards) | BossSloth CardBarPatch | H |
| `[Sirenix.Serialization]`, `[Sirenix.Serialization.Config]`, `[Sirenix.Utilities]` v2.1.6 (11 types, 24 members) | **Not shipped any more.** Managed/ only has `Sirenix.OdinInspector.Attributes.dll` | **No equivalent in game.** Ship Sirenix.Serialization/.Config/.Utilities 2.1.6 next to MapsExtended (from an older ROUNDS build or another package), or use an updated MapsExtended. Without them, MapsExtended map (de)serialization fails, and WillsWackyMapObjects (hard-depends on MapsExtended) goes with it | MapsExtended | H (diagnosis) |

## 7. Old-only items (excluded from the target set because replaced; recorded for completeness)

- **Old MMHOOK_Assembly-CSharp (willis81808-MMHook 1.0.0):** 231 TypeRefs and 1488 MemberRefs unresolved. 203 of the
  TypeRefs are Photon/emotitron types removed from the game. 1121 distinct `Type::Method` hooks target methods that no
  longer exist (list: `mmhook_missing_methods.txt`; for example `CardChoice::GetRanomCard` → `GetRandomCard`,
  `Optionshandler::*`/`MultiOptions::*` → OptionsData system, `ChangeColor::Start` → none, damage methods → new
  DamageSource overloads). **Superseded:** the staged MMHOOK is regenerated and clean. No remaining mod references a
  missing On./IL. hook type; the only On. hooks used are `MainMenuHandler.Awake`, `Screenshaker.OnGameFeel` and
  `ChomaticAberrationFeeler.OnGameFeel`, which all resolve.
- Old RWF 2.2.2: `PointVisualizer::text`, `Photon.Realtime.Room::get_PlayerCount` (byte → int), `RoomOptions::MaxPlayers`
  (byte → int), `PlayerAssigner::CreatePlayer(InputDevice,bool)`, `PlayerManager::RegisterPlayer`,
  `UIHandler::DisplayScreenText*`, `GM_ArmsRace::Start`/`GM_Test::Start` patches, and an ambiguous
  `PlayerAssigner.RemovePlayer` patch. Old UnboundLib 3.2.14: the same Photon byte→int changes, Steamworks, and
  `CardBar.OnHover` patch params. **Superseded** by staged builds (0 issues).

## 7b. Found by the top-100 Thunderstore sweep (handled since rounds-port 1.2.0)

| Old | New | `fix` |
|---|---|---|
| Global `Debug` class in Assembly-CSharp: `Log(string)`, `LogError(string)`, `LogWarning(string)`, `Log(object)`, `DrawLine(...)` | Removed | `UnityEngine.Debug` method with the same name (string params become object) |
| `UIHandler::ShowJoinGameText(string, Color)` | `ShowJoinGameText(LocalizedString, Color)` | helper: calls it with an empty `LocalizedString`, then `m_localizedJoinGameText.ResetReference(text)` |
| `UIHandler::DisplayScreenText(Color, string, float)`, `DisplayScreenTextLoop(Color, string)`, `DisplayScreenTextLoop(string)` | `LocalizedString` instead of `string` | same, through `gameOverText.ResetReference(text)` |
| `Photon.Realtime.Room::GetPlayer(int)` | `GetPlayer(int, bool findMaster)` | passes `false` |
| `Room::get_PlayerCount()` returns `byte` | returns `int` | new getter + `conv.u1` |
| `RoomOptions::MaxPlayers` (`byte` field) | `int` field | writes use the int field; reads add `conv.u1` |
| `TMP_Text::ForceMeshUpdate()` | `ForceMeshUpdate(bool ignoreActiveState, bool forceTextReparsing)` | passes `(false, false)` |
| `HealthHandler::RPCA_SendTakeDamage(Vector2, Vector2, bool, int)` called directly | + trailing `DamageSource` | passes `DamageSource.Player` |
| `CardBarButton::card` | public `m_cardInfo` | field reference renamed |
| `CardInfo::cardName` writes | private field | helper writes it by reflection (REVIEW) |
| `[HarmonyPatch]` `argumentTypes` of the old `TakeDamage(..., Color, ...)` etc. | + trailing `DamageSource` | appended to `argumentTypes` |
| `[HarmonyPatch(typeof(CardBar), "OnHover")]` without `argumentTypes` | ambiguous (two overloads) | adds `[HarmonyPatch(new[] { typeof(CardBarButton) })]` (REVIEW) |
| `typeof(UnityEngine.Input)` / Steamworks types inside `[HarmonyPatch]` | stored as an assembly-qualified name | same retarget as IL type references |
| `UnityEngine.UIVertex::uv0`…`uv3` (`Vector2`) | `Vector4` (Unity 2022) | `ldfld` + `Vector4.op_Implicit` → Vector2; `stfld` after Vector2 → Vector4 (z, w = 0); `ldflda` left MANUAL |

Not changed by the update, but reported: `RPC("RPCA_AddSlow", ...)` with 1 argument (RSClasses). The old game's
`RPCA_AddSlow` already took `(float slowToAdd, bool isFastSlow = false)`, and PUN matches the exact argument count
(defaults aren't filled in), so the call was dropped on the old game too. Left MANUAL: appending `false` would change
behaviour.

| `DontDestroyOnLoad(obj)` in a `BaseUnityPlugin`'s `Awake` or constructor | Plugins start at frame 0 with no scene loaded; loading "Main" destroys every object made before it, `DontDestroyOnLoad` or not (seen in game: MapsExtended's "Root Map Object Manager", which broke custom-map physics objects for every non-host player). Objects with `HideFlags.DontSave` survive | `__RoundsCompat.KeepAlive(obj)`: `DontDestroyOnLoad` plus `DontSave` on the root GameObject |
| A plugin's `Awake` that reaches `FindObjectsOfType`, `GameObject.Find`, `Camera.main`... (its own methods, a few calls deep) | No scene is loaded yet at plugin load, so these find nothing (ToggleEffectsMod's `Post_Main` volume: NRE, and its menu never registers). On the old game the first scene was there | `Awake` hands its body (now `Awake__RoundsCompat`) to `__RoundsCompat.AfterFirstScene`, which runs it once the first scene has loaded |
| `[HarmonyPatch]` whose target the game, Unity, Photon or Bknibb's UnboundLib/RWF no longer has (`CardBar.Update`, `ChangeColor.Start`, `TrickShot.Awake`...) | HarmonyX throws on the missing target and `PatchAll` stops, so the mod's later patches don't apply either | Disabled: the attribute naming the target is removed and the patch method renamed `__RoundsCompat_Disabled_<type>_<method>_<name>` (REVIEW: what it did is lost). rounds-port Runtime calls `CardBar.Update` ones every frame for each active `CardBar` |

The scanner now also follows HarmonyX: several complete `[HarmonyPatch]` attributes on one method are separate
targets (`AttributePatch.Create`), not one merged target.

## 7b2. Found by the in-game bench (handled since rounds-port 1.3.0)

| Old | New | `fix` |
|---|---|---|
| `Optionshandler::lockMouse`, `lockStick` (static bools) | `OptionsData` toggles `OPTION_MOUSE_AIM8DIR`, `OPTION_CONTROLLER_AIM8DIR` (what `PlayerInput` reads) | helper reads the toggle (fallback `false`) |
| `DamageOverTime::DoDamageOverTime(..., bool lethal)` | + trailing `DamageSource` (and private now, as `HealthHandler.dot` already was) | passes `DamageSource.Player` |
| `ObjectsToSpawn::SpawnObject(...)` returning `GameObject[]`, result dropped (`pop`) | returns `FriendlyFoe.PoolableWrapper[]` | calls the new method. When the result is used: MANUAL |
| `TMPro.TMP_FontAsset::HasCharacter(char, bool searchFallbacks)` | `HasCharacter(char, bool, bool tryAddCharacter)` | passes `false` |
| `UnityEngine.TextCoreModule` types | `UnityEngine.TextCoreFontEngineModule` / `TextCoreTextEngineModule` | type reference retargeted to the module that has it |
| Harmony patch on `CardBar::OnHover` with `CardInfo card` | `OnHover(CardBarButton cardButton)` | parameter becomes `CardBarButton cardButton`, each read becomes `cardButton.m_cardInfo` (REVIEW: the game's `OnHover` also sets `DoesHover` and moves the selection marker, which a prefix returning false skips) |
| Harmony patch on `CardBar::Update` | `CardBar` has no `Update` | MANUAL. HarmonyX throws on it and `PatchAll` stops there |

## 7c. UnboundLib 3 → 4 and RoundsWithFriends 2 → 3 (Bknibb's ports)

Public API of willis81808 UnboundLib 3.2.14 against Bknibb's 4.2.5: 7 of 517 types and members gone, none changed. Olavim
RoundsWithFriends 2.2.2 against Bknibb's 3.0.10: 24 of 412 gone. Of all that, the 98 sweep mods (and the libraries they
download) use 2 things; everything else they reference still resolves, and so do their 31 MMHOOK references.

| Old | New | `fix` | Mods |
|---|---|---|---|
| `Unbound.RegisterMaps(AssetBundle)`, `RegisterMaps(IEnumerable<string>)`, `RegisterMaps(IEnumerable<string>, string)` (obsolete forwarders) | removed | `LevelManager.RegisterMaps(..., "Modded")`, as each forwarder called it (the two-argument one ignored its category) | MapsPlus |
| `RWF.UI.PlayerSpotlight`, `RWF.UI.FollowPlayer` (the darkened screen with a light on each player) | removed, no replacement | MANUAL: remove the calls; RWF 3's own game modes run without it | Simple Gamemodes, Will's Wacky Game Modes |
| `NetworkConnectionHandlerExtensions.IsSearchingQuickMatch`, `SetSearchingQuickMatch`, `SetSearchingTwitch` | the game keeps one `m_searchingType`; RWF 3 has `GetSearchingType`/`SetSearchingType` | MANUAL (no mod uses them) | none |

Also gone, unused and not public API in practice: UnboundLib's `Patches.CardChoicePatchGetSourceCard` and
`Patches.GM_ArmsRace_Patch_Start` classes.

## 8. Summary: ambiguous or no-equivalent

1. `ChangeColor::Start` (PerformanceImprovements): no equivalent. Delete the patch.
2. `CardBar::OnHover(CardInfo,Vector3)` + Traverse `ci` (ModdingUtils): no equivalent. Needs a reimplemented helper.
3. `Optionshandler.vol_Master/vol_Sfx` (CosmicRounds): no static. Use OptionsData slider or a constant 1 (semantic choice).
4. Sirenix.Serialization (MapsExtended): removed from the game. Must ship the DLLs or update the mod.
5. `Player.teamID` writes: no public setter. Use private `m_teamID` (never `AssignTeamID` inside its own prefix).
6. `ObjectsToSpawn.SpawnObject` in PerformanceImprovements: types are mechanical, but the feature conflicts with pooling.
7. `CharacterData.maxHealth` writes via `set_MaxHealth`: can unlock the Titan achievement. Use `m_maxHealth` to avoid it.

Pre-existing (not new): CR `CustomCardOnRemoveCard` patch has no argumentTypes over 2 overloads. Harmony falls back to the
parameterless `OnRemoveCard()`, and the prefix declares `Player __instance` on a CustomCard. This behaviour is unchanged
from before the update.
