using HarmonyLib;

namespace RoundsPort.Runtime
{
    // UnboundLib's CustomCard makes every modded card its own sourceCard. Instantiate remaps that self-reference, so each
    // copy of the card points at itself, and the current CardInfo.Awake only looks the source up by name when sourceCard
    // is null. The picker's game sets sourceCard after spawning the offer, but every other player gets the offer through
    // Photon, so there the picked card's sourceCard is the pick-screen copy, destroyed right after the pick: that
    // player's currentCards holds dead cards (vanilla cards are fine). Mods that read currentCards then misbehave on
    // every machine but the picker's (ModdingUtils' card rules throw, Cosmic Rounds' Beetle never regenerates).
    // Clearing the self-reference lets Awake look the card up by name, as it does for vanilla cards; if the lookup
    // finds nothing the card keeps pointing at itself, as before.
    [HarmonyPatch(typeof(CardInfo), "Awake")]
    internal static class CardSelfSource_Fix
    {
        static void Prefix(CardInfo __instance, out bool __state)
        {
            __state = __instance.sourceCard == __instance;
            if (__state) __instance.sourceCard = null;
        }

        // a finalizer, so a card whose Awake throws later on still gets its self-reference back
        static void Finalizer(CardInfo __instance, bool __state)
        {
            if (__state && __instance.sourceCard == null) __instance.sourceCard = __instance;
        }
    }
}
