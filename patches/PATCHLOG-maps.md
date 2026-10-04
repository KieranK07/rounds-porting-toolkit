# PATCHLOG: MapsExtended 1.4.2 + WillsWackyMapObjects 1.2.4 (ROUNDS Unity 2022.3, macOS)

Outputs (drop-in, same layout as `~/ROUNDS-plugins-backup-20260930`):

- `staging/patched/olavim-MapsExtended-1.4.2/`: `plugins\MapsExtended.dll` (+ regenerated `.pdb` and `.dll.mdb`;
  the literal backslash filenames are kept as installed), plus **new** `Sirenix.Serialization.dll`,
  `Sirenix.Serialization.Config.dll`, `Sirenix.Utilities.dll` and `Sirenix-OdinSerializer-LICENSE.txt`.
  icon, README and manifest are unchanged.
- `staging/patched/willuwontu-WillsWackyMapObjects-1.2.4/`: `WillsWackyMapObjects.dll`. icon, README and manifest
  are unchanged.

Approach: **IL patch with Cecil** of the shipped DLLs (no source rebuild), plus a **clean-room-legal Sirenix
replacement** built from open-source OdinSerializer. Assembly names and versions (MapsExtended 0.0.0.0, WWMO 1.0.0.0),
BepInPlugin GUIDs and versions (`io.olavim.rounds.mapsextended` 1.4.2, `com.willuwontu.rounds.MapObjects` 1.2.4) and
the full type/member surface are byte-for-byte identical. A type+member dump diff between original and patched is
empty for both DLLs.

## 1. Sirenix (the blocker)

- MapsExtended references `Sirenix.Serialization`, `Sirenix.Serialization.Config` and `Sirenix.Utilities`, all
  **v2.1.6.0, PublicKeyToken=null**. The current game ships only `Sirenix.OdinInspector.Attributes.dll`. No game
  assembly references Sirenix.Serialization any more, so nothing else will ever load ours.
- The original Odin 2.1.6 DLLs are proprietary (Odin Inspector EULA). A copy exists in Poly Bridge 2's Managed/. It was
  used **only to read metadata** (which types live in `.Config`) and was **not** redistributed.
- **Source:** TeamSirenix/odin-serializer @ `ba19025` (Apache-2.0, Copyright Sirenix IVS). This is the open-sourced
  core of the same serializer, and it uses the same JSON wire format.
  - Namespaces renamed: `OdinSerializer` → `Sirenix.Serialization` and `OdinSerializer.Utilities` → `Sirenix.Utilities`.
    The emitted-assembly names were renamed to match.
  - Split into 3 assemblies that mirror Odin's layout. `Sirenix.Utilities` = Utilities/**. `Sirenix.Serialization.Config` =
    DataFormat, ErrorHandlingPolicy, LoggingPolicy, ILogger, CustomLogger, DefaultLoggers and GlobalSerializationConfig
    (the same 7 public types as the real `.Config`). `Sirenix.Serialization` = Core + Unity Integration. Cross-assembly
    internals are opened with InternalsVisibleTo.
  - Built for net472 against the game's `UnityEngine.*.dll`, with defines `CAN_EMIT;UNITY_STANDALONE;UNITY_5_6_OR_NEWER`
    (the JIT/Mono build; not UNITY_EDITOR, not ODIN_INSPECTOR). AssemblyVersion is 2.1.6.0 and there is no strong name.
    The build tree is in `scratchpad/maps/sirenix/` (`dotnet build Sirenix.Serialization/Sirenix.Serialization.csproj -c Release`).
  - Every MemberRef MapsExtended uses exists with an identical signature. That includes the enum ordinals it hard-codes
    (`EntryType` StartOfNode=7, EndOfNode=8, EndOfStream=15; `DataFormat.JSON`=1) and the abstract
    `TwoWaySerializationBinder` members that V0/V1MapObjectBinder override.
  - The license plus a "modified" notice ship in the package (Apache §4).
- **Placement:** package root with plain filenames. BepInEx 5.4.23 resolves a missing reference by searching
  `BepInEx/plugins/**/<AssemblyName>.dll`. A `plugins\`-prefixed name would **not** resolve.
- **Collisions:** none. No `Sirenix.Serialization*`/`Sirenix.Utilities` file exists anywhere in the ROUNDS install. 0 of
  our 325 type full-names collide with the ~33.7k types in Managed/, BepInEx/core, plugins/ or staged UnboundLib.
- **.map compatibility:** `.map` files are Sirenix JSON (`"$type": "N|MapsExt.CustomMap, MapsExtended"`, Unity
  Vector2/3/Quaternion/Color via Odin's Unity formatters). Only MapsExtended (de)serializes them; the game does not, so
  the game update does not affect the format. **Offline test:** a .NET 8 harness loaded the patched MapsExtended + WWMO +
  our Sirenix (a no-emit test build, because .NET 8 lacks Mono's `DefineDynamicModule(string,bool)`) and ran the real
  `MapLoader.ForVersion(ver).Load()` on **94 maps** from Zenith Maps 4.2.3 and BeepsMaps 2.2.0 (format versions 1.2.6 and 1.4.2).
  Result: 94/94 parsed. Every null map object maps 1:1 to a type from a mod that was not loaded (ExtraMapObjects,
  MapImageObjects, VanillaMapObjects), which MapsExtended pre-checks and rejects in game exactly as before.

## 2. MapsExtended IL changes (`maps/patcher`, mode `maps`)

| # | Where | Change |
|---|---|---|
| 1 | `MapManagerPatch_GetSpawnPoints/<>c::<Postfix>b__0_0` | `ldfld Player::teamID` → `callvirt Player::get_TeamID()` |
| 2 | `CardBarPatch_OnHover_Patch` attribute | `[HarmonyPatch(typeof(CardBar),"OnHover")]` → `[HarmonyPatch(typeof(CardBar),"OnHover", new[]{typeof(CardBarButton)})]` (fixes AmbiguousMatchException; `OnHover(int)` calls this overload, and it still sets the SetScaleToZero child to 1.15 that the postfix rescales) |
| 3 | `CardBarPatch_OnHover_Patch.Postfix` | param `___currentCard` → `___m_currentCard` |

Checked, and no change needed: the DamageBox.Collide / OutOfBoundsHandler / ScreenEdgeBounce / PhotonMapObject.Update
transpiler anchors all still match the new IL (local 4 is still `CharacterData`; the constants ±35.56/±20 are present;
the WorldToScreenPoint/ScreenToWorldPoint windows are intact; `PhotonNetwork.Instantiate` has a single overload,
followed by `pop`). Reflection names (`photonSpawned`, `map`, `levelID`, `ToggleLevelMenuHandler.ScrollViews`, which is
static readonly in both UnboundLib 3.2.14 and 4.2.5) all exist. MapsExtended does **not** use SpawnObject, Steamworks
or Input, so those MAPPING entries do not apply.

## 3. WWMO IL changes (`maps/patcher`, mode `wwmo`)

There are 5 call sites, each getting a trailing `HealthHandler.DamageSource.Player` (0). The original call instruction
becomes `ldc.i4.0` (so branch targets stay valid) and the retargeted call follows it:
`AcidMono.HandlePlayer`, `LavaMono.HandlePlayer`, `BoxTouchingLava_Mono.FixedUpdate` (TakeDamageOverTime, 8→9 args),
`AcidMono.HandleBox`, `LavaMono.HandleBox` (Damagable.CallTakeDamage, 5→6 args).

## 4. Visibility audit (per the ModdingUtils cardName finding)

A Cecil audit (`maps/access`) of every MemberRef/TypeRef from both patched DLLs into the game, UnboundLib 4.2.5,
patched ModdingUtils and Sirenix found **0** private/internal targets. All 23 (MapsExtended) and 25 (WWMO) game fields
they touch are still public and still read by the game, except `SpawnPoint.TEAMID`. TEAMID is write-only in both old
and new spawn logic (`PlayerManager.MovePlayers` indexes by player), so there is no semantic change.

## 5. Verification

`compat/SCAN_maps.txt` is the target set plus all `staging/patched/*` substituted:
- MapsExtended: **0 types / 0 members** (was 11/24). The Sirenix refs now resolve. The 5 remaining harmony/reflect
  items are the documented false positives (Camera-vs-Vector3, `TDelegate`, `ReadProperty`, MapEmbiggener). The real
  6th item (OnHover ambiguity) is fixed.
- WWMO: **0/0/0** (was 0/2/0).
- Sirenix.*: 0/0. The 8 reflect items in Sirenix.Serialization are generic-`T`/BCL false positives
  (ColorBlockFormatter<T>, Dictionary.Comparer, KeyValuePair.Key).
- TOTAL across the set: 0 types, 0 members.

## 6. Residual risks

1. Not run in game (by instruction). The emit (CAN_EMIT) path that Mono will use was not exercised offline; only the
   reflection path was. That is Odin's standard Mono-standalone configuration.
2. OdinSerializer ≠ Odin 2.1.6 internals. MapsExtended's API surface matches, but a third-party map-object mod that calls
   some other Sirenix 2.1.6 API absent from OdinSerializer would fail. None is installed.
3. ArchitectureInfo is never initialized (`[RuntimeInitializeOnLoadMethod]` does not fire for late-loaded DLLs). It
   defaults to safe aligned reads, which affects only binary-format speed. Maps are JSON.
4. Debug symbols are regenerated (portable PDB + MDB) and match the new IL. The original PDB was a Windows PDB.
5. WWMO hard-depends on ModdingUtils (patched separately) and MapsExtended. Install all three together.
