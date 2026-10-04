using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RoundsPort.Runtime
{
    // UnboundLib 4.2.5's card-pick stats panel ("Gun Stats" / "Other Stats") builds its baseline from components made
    // with `new` (on no GameObject) and calls ResetStats on them, the first time a pick opens. Other mods patch
    // ResetStats and call GetComponent there (ModdingUtils, Cosmic Rounds, RespawnPatch, Regeneration Patch...), which
    // throws on such a component. The exception escapes the panel's static constructor and RoundsWithFriends' start of
    // game, so the first card pick never shows (and the panel stays broken for the session). Not Mac-specific.

    // On those detached components a failed ResetStats is ignored: their field defaults are the baseline anyway.
    [HarmonyPatch]
    internal static class StatsBaseline_Fix
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CharacterStatModifiers), "ResetStats");
            yield return AccessTools.Method(typeof(Block), "ResetStats");
        }

        // A component made with `new` has no native object: Unity's == null is true while the reference isn't null.
        static Exception Finalizer(Exception __exception, Component __instance)
        {
            if (__exception != null && !ReferenceEquals(__instance, null) && __instance == null) return null;
            return __exception;
        }
    }

    // Whatever else goes wrong in the panel, it can't stop a card pick: its error is logged instead of thrown.
    [HarmonyPatch]
    internal static class StatsPanelGuard_Fix
    {
        internal static BepInEx.Logging.ManualLogSource Log;
        static Type Attached => Types.Find("UnboundLib.StatsViewer.AttachedCardChoiceUI");
        static bool Prepare() => Attached != null;
        static MethodBase TargetMethod() => AccessTools.Method(Attached, "ChangePlayer");

        static bool logged;
        static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!logged) { logged = true; Log?.LogWarning("UnboundLib's stats panel failed; the card pick carries on without it: " + __exception.GetBaseException()); }
            return null;
        }
    }
}
