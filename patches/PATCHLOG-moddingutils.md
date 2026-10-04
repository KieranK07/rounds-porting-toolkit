# PATCHLOG: Pykess-ModdingUtils 0.4.8 for ROUNDS (Dec 2025 / Unity 2022.3, macOS)

**Output:** `scratchpad/staging/patched/Pykess-ModdingUtils-0.4.8/`. This is a copy of the pristine package with a new
`ModdingUtils.dll` (sha1 `c2b3976f…`) and a new `.pdb`. `manifest.json`, `README.md`, `LICENSE` and `icon.png` are
unchanged.

## Path chosen: (a) rebuild from source

- Source: `github.com/pdcook/ModdingUtils` at HEAD `6cdc3f3` ("version 0.4.8"). The repo has no tags. The copy is at
  `scratchpad/mu/src`, and the patched tree is at `scratchpad/mu/build`.
- **Proof that the source matches the shipped binary.** I built the source with only the compile fixes, in the Debug
  configuration (the shipped DLL is a Debug build: it has `<x>5__N` iterator locals). I then compared every type,
  member and called-member set per method (Cecil dump, `mu/apidump`) against the shipped DLL. The only differences
  were the intended edits, plus Roslyn-version noise (`<>O` delegate cache, `RefSafetyRules`/`Embedded` attributes).
  Closure and iterator numbering is identical. The repo's committed `ModdingUtils.dll` is an older 0.4.6 build, so I
  ignored it.
- Build: `dotnet build -c Debug`, netstandard2.1. References: the current game `Managed/`, `BepInEx/core`
  (0Harmony/BepInEx/BepInEx.Harmony), and the staged UnboundLib 4.2.5 + MMHOOK. I added a reference to
  `Unity.Localization.dll`, which ships in Managed/. The assembly name `ModdingUtils` v1.0.0.0, the BepInPlugin
  (`pykess.rounds.plugins.moddingutils`, "Modding Utilities", `0.4.8`), the BepInDependency and the BepInProcess are
  all unchanged.
- Full source diff: `scratchpad/mu/moddingutils-0.4.8-compat.patch` (17 files, +226/−167).

## Changes

| # | Area | Change |
|---|---|---|
| 1 | `Player.playerID` / `teamID` reads (≈45 sites) | → `PlayerID` / `TeamID` property getters |
| 2 | `CharacterData.maxHealth` (AI setup, TemporaryModifiers `+=`/`-=`/`=1`) | → `MaxHealth` property (get/set) |
| 3 | `PlayerPatchAssignPlayerID.Prefix` (wrote `__instance.playerID`) | Signature is now `(Player __instance, int ID, ref int ___m_playerID)` and it writes `___m_playerID` |
| 4 | `PlayerPatchAssignTeamID.Prefix` (wrote `__instance.teamID`) | Same, using `ref int ___m_teamID`. Does **not** call `AssignTeamID`, which would recurse and write local Photon props |
| 5 | `Gun_PatchSendPlayerID.Transpiler` | `AccessTools.Field(Player,"playerID")` + `Ldfld` → `AccessTools.PropertyGetter(Player,"PlayerID")` + `Callvirt`. Anchors checked in current `Gun.<FireBurst>d__93.MoveNext` IL: all three `GetComponent<PhotonView>` → `ldstr RPCA_Init*` → `ldfld CharacterData::view` → `get_OwnerActorNr` sites are present |
| 6 | `CardChoicePatchGetRanomCard` attribute | `"GetRanomCard"` → `"GetRandomCard"` (class name unchanged, so the public API is unchanged) |
| 7 | `HealthHandler.TakeDamageOverTime` (EndStalemate) | Recompiled, so it now calls the new overload with `DamageSource.Player` (default) |
| 8 | `ObjectsToSpawn.SpawnObject` (ProjectileHit postfix) | Recompiled against the `PoolableWrapper[]` return type. The result was already discarded |
| 9 | `CardInfo.cardName` (now **private**, 10 sites: GetCardWithName, GetCardID(string), unique-card duplicate filter, "huge" fallback, card-bar letters, log lines) | → new internal `CompatShims.GetCardName(card)`. It returns the legacy `cardName` field if that is non-empty. Otherwise, for an UnboundLib `CustomCard`, it returns the `m_localizedCardName` entry key (UnboundLib 4.x stores the title there and never fills `cardName`). Otherwise it returns `CardInfo.CardName`. Just reading the private field would return `""` for every mod card, so every mod card would compare equal in the duplicate filter |
| 10 | `CardBarUtils.RPCA_ShowCard`: `CardBar.OnHover(CardInfo,Vector3)` was removed, and `Traverse "currentCard"` | → `CompatShims.ShowCardOnBar(bar, card)`. This copies the body of `CardBar.OnHover(CardBarButton)`: destroy `m_currentCard`, set `DoesHover`, `AddCardVisual` at `cardPos`, disable colliders, sorting layer `MostFront`, SetScaleToZero off at 1.15. It leaves out `SetSelection`. The result is then scaled to `cardLocalScaleMult` as before |
| 11 | Both `SilentAddToCardBar` copies (CardBarUtils, Cards): `Traverse "ci"`, `"source"`, CardBarButton `"card"`, and the TMP label | `ci` was dropped (the field is gone). The rest → `CompatShims.AddButtonToCardBar`: `m_source`, `m_cardInfo`, and the label set through `UILocalizedString.Text`. That getter sets `m_textModified`; without it, `Awake` blanks the text. TMP is the fallback. The button is also inserted into `m_cards`, as the game's `AddCard` does, so `ClearBar()` destroys it and D-pad hover reaches it |

## Verification

- **Public API:** the `apidump` diff of public/protected types, fields, methods (with default values), properties and
  events between the shipped and rebuilt DLLs is **identical**. Across all members, the only changes are the two
  private Prefix signatures (#3, #4) and the new `internal static class ModdingUtils.Utils.CompatShims`. No consumer
  (CR, ClassesManagerReborn, RarityLib, ModsPlus, WWMO, CardChoiceSpawnUniqueCardPatch) reflects into those.
- **compatscan** (target set + patched ModdingUtils substituted, output `mu/SCAN_mu.txt`): ModdingUtils has 0 type and
  0 member problems. The single remaining item is the known false positive `NullCard`
  (`CardChoiceSpawnUniqueCardPatch.NullCard` exists). There are 0 unresolved references from any consumer into
  `[ModdingUtils]`. Every other mod's counts are unchanged.
- **Harmony/reflection check** (`mu/hcheck`, output `mu/hcheck.txt`), against the current Assembly-CSharp: 38 attribute-targeted patch methods resolve. Every injected `__instance`, `__result`, named argument and `___field` (for example `___m_Tick`,
  `___m_PlayerAPI`, `___m_playerID`, `___m_teamID`) exists with a matching type. The 61 reflection names (Traverse,
  GetFieldValue, InvokeMethod, FieldRefAccess, PropertySetter) were checked against the **actual receiver type**,
  and all exist. The three TargetMethod patches were checked by hand:
  - ProjectileInit `RPCA_Init*` / `OFFLINE_Init*`: same signatures; the `players[senderID]` and
    `GetPlayerWithActorID` patterns are still present.
  - Gun FireBurst: see #5.
- **One pre-existing item, unchanged:** `CheckBulletsAfterGettingCards` declares `__instance` on the static
  `ApplyCardStats.CopyGunStats`. Harmony passes null, and the parameter is unused.

## Residual risks

1. **Not run in game** (by instruction). Runtime behaviour is verified only statically.
2. `GetCardName` for vanilla cards depends on asset data. If the serialized `cardName` was cleared in the update, it
   falls back to the localized `CardName`. Name lookups by English string (`GetCardWithName("Huge")`) could then fail
   in non-English locales.
3. `MaxHealth` setter: when a value ≥1000 is assigned for the local player, it can unlock the Titan achievement
   (this is game behaviour, and other ported mods share it).
4. `ShowCardOnBar` sets `DoesHover = true` without a hover index. D-pad input during the 1.5 s preview moves the
   selection to the real cards. This is harmless.
5. ModsPlus, ClassesManagerReborn and CR still read the private `CardInfo.cardName` field directly. That field
   resolves, but it is `""` for UnboundLib 4 cards. That is a cross-mod issue outside this patch.
6. `manifest.json` still lists `willis81808-UnboundLib-3.1.0` / `MMHook-1.0.0`. This matters only to mod managers;
   BepInEx uses the GUID dependency.
