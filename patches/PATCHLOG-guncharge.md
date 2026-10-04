# PATCHLOG: willuwontu-GunChargePatch 0.0.4 (and Sfinford-ProjectileChargePatch 0.0.1)

59 Thunderstore packages depend on GunChargePatch. Sfinford-ProjectileChargePatch 0.0.1 ships the same DLL
renamed (same SHA-256), so the same patch applies to it.

## The break

`Gun_PatchTranspiler` patches `Gun.<FireBurst>d__93.MoveNext` so bullets are created through its own
`ChargedProjectileInit` (`OFFLINE_InitCharge`, `RPCA_InitCharge` and the `noAmmoUse` / `SeparateGun` variants) with
the gun's charge as an extra argument. Most of it finds instructions by what they are. Three it took by their index in
the old game's FireBurst:

- `val[24]` = `ldarg.0`, `val[25]` = `ldfld float32 <FireBurst>d__93::charge`: the charge, pushed before each Init call
  and stored into each RPC's argument array;
- `val[293]` = `box float32`, to box it.

The current game added a 25-instruction loop at the top of FireBurst (it drops destroyed bullets from
`Gun.m_spawnedBullets`), so those indexes point at other instructions (`ldfld m_spawnedBullets`, `ldloc.2`,
`ldfld CharacterData::view`). The patched method is invalid IL (`InvalidProgramException ... IL_020a: ldfld 0`), Harmony
throws, and `PatchAll` stops, so the mod's other patches don't apply either.

## The patch

`tools/guncharge-patcher` (Mono.Cecil, deterministic): in `Gun_PatchTranspiler.Transpiler`, each
`new CodeInstruction(val[N].opcode, val[N].operand)` for N = 24, 25, 293 (5 places) becomes the instruction it was
meant to copy:

- `new CodeInstruction(OpCodes.Ldarg_0, null)`
- `new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(GetNestedIDoBlockTransitionType(), "charge"))`
- `new CodeInstruction(OpCodes.Box, typeof(float))`

Nothing else changes; the assembly references are identical. AutoFix then fixes the rest at startup (`Player.playerID`,
`UnityEngine.Input`, `Optionshandler.lockMouse`/`lockStick`).

| File | Before (SHA-256) | After |
|---|---|---|
| GunChargePatch.dll / ProjectileChargePatch.dll | `bcc43b2fac595c1fdc09ed95066f91062659a5ba5dae24beaf13c1c0145d46e2` | `7fd5f587c1de2a8783c103881ecde77f30ba0af84a6d9478bb96d6af8d210be3` |

## Verification (2026-10-03, macOS, in game)

- Alone with its dependencies, through the Gale layer and AutoFix: no errors; the FireBurst transpiler is applied.
- The transpiled FireBurst (the mod's own `NewFireBurstCode`): all three `OFFLINE_Init*` calls go to the `*Charge`
  versions with `ldarg.0; ldfld charge; conv.r4` before them; the three RPCs are renamed to `RPCA_*Charge` with their
  arrays grown 4/4/5 → 5/5/6 and the boxed charge stored at the last index.
- `ChargedProjectileInit` reaches the bullet prefabs (Bullet_Base, Bullet_NoTrail, Bullet_EMP): they load with the
  Main scene, before the mod's `Start()` runs.

Not tested: firing a charged gun in a match.
