using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

// Runtime fixes for Cosmic Rounds 2.7.0 (CosmicRounds.dll) on the current ROUNDS build.
// CR types are never referenced at compile time: every target is resolved by name at patch time, and a patch
// class whose targets are missing is skipped through Prepare() returning false. No static mutable state, so a
// hot-reloaded copy of this assembly behaves the same as the first one.
namespace RoundsPort.Runtime
{
    internal static class CosmicRoundsTypes
    {
        // Same as CR's [BepInPlugin] GUID. Plugin needs a soft BepInDependency on it so CR is loaded before we patch.
        public const string Guid = "com.XAngelMoonX.rounds.CosmicRounds";

        public static Type Find(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (a.IsDynamic) continue;
                    var t = a.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        public static bool Loaded => Find("CR.MonoBehaviors.FireballMono") != null;

        // Methods declared directly on the named CR.MonoBehaviors types. Missing types or methods are skipped.
        public static IEnumerable<MethodBase> Declared(string method, params string[] typeNames)
        {
            foreach (var n in typeNames)
            {
                var t = Find("CR.MonoBehaviors." + n);
                var m = t == null ? null : AccessTools.DeclaredMethod(t, method, Type.EmptyTypes);
                if (m != null) yield return m;
            }
        }

        public static bool HasField(Type t, string name, Type fieldType)
            => AccessTools.DeclaredField(t, name)?.FieldType == fieldType;
    }

    // FireballMono (Flamethrower/Ignite/Sun), HolyFireMono (Halo) and BeeSpriteMono (Hive) are added to bullets
    // by the owner's Shoot hook, but their Awake does `player = GetComponent<Player>()` on the *bullet*, which is
    // always null. Update then dereferences player.data.currentCards and throws every frame for every bullet
    // (FireballMono.Update IL_00a5, ~47k times in one session). FireballMono also only sets `move` inside the
    // crSpecialVFX branch, so with VFX off it would throw on move.velocity too.
    // Fix: resolve the owner from the bullet (ProjectileHit.ownPlayer, else SpawnedAttack.spawner) and MoveTransform
    // when missing; if they can't be resolved yet, skip the frame instead of throwing.
    [HarmonyPatch]
    internal static class CR_BulletVfxOwner_Fix
    {
        static IEnumerable<MethodBase> Targets() =>
            CosmicRoundsTypes.Declared("Update", "FireballMono", "HolyFireMono", "BeeSpriteMono")
                .Where(m => CosmicRoundsTypes.HasField(m.DeclaringType, "player", typeof(Player))
                         && CosmicRoundsTypes.HasField(m.DeclaringType, "move", typeof(MoveTransform)));

        static bool Prepare() => Targets().Any();
        static IEnumerable<MethodBase> TargetMethods() => Targets();

        static bool Prefix(MonoBehaviour __instance, ref Player ___player, ref MoveTransform ___move)
        {
            if (___player == null)
            {
                var hit = __instance.GetComponent<ProjectileHit>();
                Player owner = hit != null ? hit.ownPlayer : null;
                if (owner == null)
                {
                    var spawned = __instance.GetComponent<SpawnedAttack>();
                    if (spawned != null) owner = spawned.spawner;
                }
                if (owner == null || owner.data == null)
                {
                    if (hit == null) __instance.enabled = false;   // not on a bullet: nothing to track
                    return false;
                }
                ___player = owner;
            }
            if (___move == null)
            {
                ___move = __instance.GetComponent<MoveTransform>();
                if (___move == null) { __instance.enabled = false; return false; }
            }
            return true;
        }
    }

    // Cards build their AddToProjectile template as a live scene object: new GameObject("A_Dark", typeof(DarkMono)).
    // Unity runs Start on that template, which has no bullet parent, so Start throws on
    // GetComponentInParent<ProjectileHit/SyncProjectile>() == null (DarkMono.Start IL_0006, MeteorMono.Start IL_0012;
    // HaloBlockMono/HiveBlockMono create a new template on every block). The bullet copies get their own Start.
    // Every one of these Starts throws before it assigns anything, and every Update is already guarded by a
    // `transform.parent != null` check, so skipping Start on a parentless template changes nothing else.
    // The template is NOT disabled: Instantiate copies `enabled`, which would turn the component off on every bullet.
    [HarmonyPatch]
    internal static class CR_ProjectileTemplateStart_Fix
    {
        static IEnumerable<MethodBase> Targets() => CosmicRoundsTypes.Declared("Start",
            "DarkMono", "MeteorMono", "MoonMono", "RingMono", "AsteroidMono", "CareenMono", "CloudMono", "CometMono",
            "DriveMono", "DroneMono", "BeeMono", "MistletoeMono", "PulseMono", "SatelliteMono", "ShootingStarMono",
            "SquidMono", "SunMono");

        static bool Prepare() => Targets().Any();
        static IEnumerable<MethodBase> TargetMethods() => Targets();

        static bool Prefix(MonoBehaviour __instance) => __instance.GetComponentInParent<ProjectileHit>() != null;
    }

    // DroneMono (Drone) has two bugs of its own, on any game version. Start ends by reading the Homing card's
    // AddObjectToPlayer, which vanilla Homing doesn't have, so it throws on that last line (a sound it never plays).
    // Update steers the bullet, then sets rot1/rot2.target: two RotSpring fields nothing assigns, so every homing bullet
    // throws every frame, and logging an exception per bullet per frame costs frames. Both throws come after the real
    // work: Update skips the two unassigned springs, and Start's throw is dropped once everything before it is set.
    [HarmonyPatch]
    internal static class CR_DroneSprings_Fix
    {
        static MethodBase Target() => CosmicRoundsTypes.Declared("Update", "DroneMono").FirstOrDefault();
        static bool Prepare() => Target() != null;
        static MethodBase TargetMethod() => Target();

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.Field(typeof(RotSpring), "target");
            var set = AccessTools.Method(typeof(CR_DroneSprings_Fix), nameof(SetTarget));
            foreach (var ins in instructions)
                yield return ins.StoresField(target) ? new CodeInstruction(OpCodes.Call, set) { labels = ins.labels, blocks = ins.blocks } : ins;
        }

        public static void SetTarget(RotSpring spring, float value) { if (spring != null) spring.target = value; }
    }

    [HarmonyPatch]
    internal static class CR_DroneStart_Fix
    {
        static MethodBase Target() => CosmicRoundsTypes.Declared("Start", "DroneMono").FirstOrDefault();
        static bool Prepare() => Target() != null;
        static MethodBase TargetMethod() => Target();

        static Exception Finalizer(Exception __exception, MoveTransform ___move, Component ___sync)
            => __exception is NullReferenceException && ___move != null && ___sync != null ? null : __exception;
    }

    // IceTrailMono (Ice Shard) creates its trail with RemoveAfterSeconds(0.5) and then moves it in Update with no
    // null check. Once the trail is destroyed, Update throws every frame: on any bullet older than 0.5 s, and forever
    // on the card's in-scene template. Skip Update once the trail is gone.
    [HarmonyPatch]
    internal static class CR_IceTrailUpdate_Fix
    {
        static IEnumerable<MethodBase> Targets() =>
            CosmicRoundsTypes.Declared("Update", "IceTrailMono")
                .Where(m => CosmicRoundsTypes.HasField(m.DeclaringType, "trail", typeof(GameObject)));

        static bool Prepare() => Targets().Any();
        static IEnumerable<MethodBase> TargetMethods() => Targets();

        static bool Prefix(GameObject ___trail) => ___trail != null;
    }

    // BurnMono.Hit (Flamethrower), HolyMono and FrostMono Object.Instantiate Demonic Pact's hit effect
    // (A_DemonicExplosion: Explosion damage 2, range 4, force 2000, auto) as a visual, with no SpawnedAttack.
    // Explosion.Start -> Explode -> DoExplosionEffects then calls spawned.IsMine() on null (IL_0377) whenever the
    // visual overlaps a player, which aborts the rest of Explode. Vanilla bullets always get a SpawnedAttack
    // (ObjectsToSpawn.SpawnObject copies it), so this only happens for objects spawned that way.
    // Fix: swallow exactly that NRE (spawned == null). Everything before the throw runs as before; nothing is
    // applied to the player (the old behaviour, since nothing owns the explosion), and Explode carries on with the
    // remaining colliders. Only applied when CR is loaded.
    [HarmonyPatch]
    internal static class CR_OwnerlessExplosion_Fix
    {
        static MethodBase Target() => AccessTools.DeclaredMethod(typeof(Explosion), "DoExplosionEffects");

        static bool Prepare() => CosmicRoundsTypes.Loaded && Target() != null
                                 && CosmicRoundsTypes.HasField(typeof(Explosion), "spawned", typeof(SpawnedAttack));
        static MethodBase TargetMethod() => Target();

        static Exception Finalizer(Exception __exception, SpawnedAttack ___spawned)
        {
            if (__exception is NullReferenceException && ___spawned == null) return null;
            return __exception;
        }
    }
}
