using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RoundsPort.Runtime
{
    // Cards Plus' Cyberpunk cards add a CyberCardEffect in SetupCard, so one also lands on the card prefab UnboundLib
    // builds: a card with no visual (BuildCard destroys the CardBase the template card made). Its Start looks for the
    // visual's name text and throws "Transform child out of bounds". Skip it there; cards in play have their visual and
    // run it as before.
    [HarmonyPatch]
    internal static class CyberCardPrefab_Fix
    {
        static System.Type Effect => Types.Find("CardsPlusPlugin.Cards.Cyberpunk.CyberCardEffect");
        static bool Prepare() => Effect != null;
        static MethodBase TargetMethod() => AccessTools.Method(Effect, "Start");

        static bool Prefix(MonoBehaviour __instance)
        {
            var card = __instance.GetComponentInParent<CardInfo>();
            return card == null || card.transform.childCount > 0;
        }
    }
}
