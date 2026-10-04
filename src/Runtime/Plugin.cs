using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RoundsPort.Runtime
{
    // Fixes that can't be made in a mod's DLL: old mods running together on the current game throw or draw wrong in
    // ways only a runtime patch can catch. Each one restores what the old game did; nothing is added to the game's UI.
    // Hot-reload safe: a new copy can load while an old one is still around, and OnDestroy undoes everything.
    [BepInPlugin("rounds-port.runtime", "rounds-port Runtime", "1.3.0")]
    // The rounds-mac-modpack plugin carries the same fixes (and its own extras); when it is installed it takes over.
    [BepInIncompatibility("kieran.rounds.maccompatfixes")]
    // Soft dependencies only order the load: patches whose target mod is missing are skipped.
    [BepInDependency("com.willis.rounds.unbound", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("pykess.rounds.plugins.moddingutils", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.XAngelMoonX.rounds.CosmicRounds", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("io.olavim.rounds.mapsextended", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.willis.rounds.cardsplus", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        Harmony harmony;
        GameObject helper;
        ManualLogSource log;

        static readonly HashSet<Type> ShaderHooks = new HashSet<Type>
            { typeof(AssetBundleLoad_Hook), typeof(MaterialCtorShader_Hook), typeof(MaterialCtorMaterial_Hook) };

        // Only macOS (Metal) needs the shader work; on Windows the mods' own D3D shaders are fine.
        internal static bool IsMetal => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Metal;

        private void Awake()
        {
            log = BepInEx.Logging.Logger.CreateLogSource("rounds-port");
            // Unique id per load: an old copy's UnpatchSelf must never remove a newer copy's patches, whichever
            // order a loader destroys the old copy and starts the new one in.
            harmony = new Harmony("rounds-port.runtime." + Guid.NewGuid().ToString("N"));
            foreach (var t in typeof(Plugin).Assembly.GetTypes())
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                if (!IsMetal && ShaderHooks.Contains(t)) continue;
                try { harmony.CreateClassProcessor(t).Patch(); }
                catch (Exception e) { log.LogError($"patch {t.Name} failed: {e.GetBaseException().Message}"); }
            }
            ShaderFix.Log = log;
            CardBarHover_Fix.Log = log;
            StatsPanelGuard_Fix.Log = log;
            helper = LetterboxClear.Create();
            MapsExtClientSync_Fix.Log = log;
            MapsExtClientSync_Fix.Host = helper.AddComponent<CoroutineHost>();
            if (MapsExtClientSync_Fix.Active(harmony.Id)) log.LogInfo("MapsExtended client map sync fix: on");
            MissingText.Apply(log);
            CardVisualFixesRunner.Log = log;
            helper.AddComponent<CardVisualFixesRunner>();
            CardBarUpdateRunner.Log = log;
            helper.AddComponent<CardBarUpdateRunner>();
            if (IsMetal)
            {
                ShaderFix.Sweep();
                SceneManager.sceneLoaded += OnSceneLoaded;
                helper.AddComponent<ShaderFixRunner>();
            }
            log.LogInfo($"runtime fixes {Info.Metadata.Version} loaded ({SystemInfo.graphicsDeviceType})");
        }

        void OnSceneLoaded(Scene s, LoadSceneMode m) => ShaderFix.Sweep();

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            harmony?.UnpatchSelf();
            MissingText.Restore();
            if (helper != null) Destroy(helper);
            log?.LogInfo("runtime fixes unloaded");
            if (log != null) BepInEx.Logging.Logger.Sources.Remove(log);
        }
    }

    internal static class CardNames
    {
        public static string Strip(string s) => s?.Replace("(Clone)", "").Trim();

        public static CardInfo FindByName(string name)
        {
            if (CardChoice.instance != null)
                foreach (var c in CardChoice.instance.cards)
                    if (c != null && Strip(c.gameObject.name) == name) return c;
            foreach (var c in HiddenCards())
                if (c != null && Strip(c.gameObject.name) == name) return c;
            foreach (var c in Resources.FindObjectsOfTypeAll<CardInfo>())
                if (c != null && Strip(c.gameObject.name) == name) return c;
            return null;
        }

        // Cards picked from pooled/network-spawned objects don't always carry the exact "<name>(Clone)" name
        // UnboundLib's GetSourceCard prefix requires, so it returns null and the card bar gets an empty button.
        public static CardInfo FindSource(CardChoice choice, CardInfo info)
        {
            if (info == null) return null;
            var name = Strip(info.gameObject.name);
            foreach (var c in choice.cards)
                if (c != null && Strip(c.gameObject.name) == name) return c;
            foreach (var c in HiddenCards())
                if (c != null && Strip(c.gameObject.name) == name) return c;
            return null;
        }

        static IEnumerable<CardInfo> HiddenCards()
        {
            var t = AccessTools.TypeByName("ModdingUtils.Utils.Cards");
            var inst = t == null ? null : AccessTools.Field(t, "instance")?.GetValue(null);
            var hidden = inst == null ? null : AccessTools.Property(t, "HiddenCards")?.GetValue(inst, null) as IEnumerable<CardInfo>;
            return hidden ?? Array.Empty<CardInfo>();
        }
    }

    [HarmonyPatch(typeof(CardChoice), nameof(CardChoice.GetSourceCard))]
    internal static class GetSourceCard_Fallback
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        static void Postfix(CardChoice __instance, CardInfo info, ref CardInfo __result)
        {
            if (__result == null) __result = CardNames.FindSource(__instance, info);
        }
    }

    // Card bar buttons can end up pointing at a destroyed CardInfo (the hover then throws inside
    // CardChoice.AddCardVisual). Remember each button's card name and re-resolve it on hover.
    [HarmonyPatch(typeof(CardBar))]
    internal static class CardBarHover_Fix
    {
        static readonly Dictionary<int, string> buttonCard = new Dictionary<int, string>();
        static readonly AccessTools.FieldRef<CardBar, List<CardBarButton>> cardsRef = AccessTools.FieldRefAccess<CardBar, List<CardBarButton>>("m_cards");
        public static ManualLogSource Log;

        [HarmonyPostfix, HarmonyPatch(nameof(CardBar.AddCard))]
        static void AddCard_Postfix(CardBar __instance, CardInfo card)
        {
            var list = cardsRef(__instance);
            if (card == null || list == null || list.Count == 0 || list[0] == null) return;
            buttonCard[list[0].GetInstanceID()] = CardNames.Strip(card.gameObject.name);
            Log?.LogDebug($"card bar + {card.gameObject.name} (scene '{card.gameObject.scene.name}')");
        }

        [HarmonyPrefix, HarmonyPatch(nameof(CardBar.OnHover), typeof(CardBarButton))]
        static bool OnHover_Prefix(CardBarButton cardButton)
        {
            if (cardButton == null) return false;
            if (cardButton.m_cardInfo != null) return true;
            if (buttonCard.TryGetValue(cardButton.GetInstanceID(), out var name))
            {
                var found = CardNames.FindByName(name);
                if (found != null) { cardButton.m_cardInfo = found; return true; }
                Log?.LogWarning($"card bar hover: no live card named {name}");
            }
            return false;   // skip instead of throwing
        }
    }

    // UnboundLib builds mod cards with LocalizedString("StringTableCards", <title>) but never adds the entry,
    // so CardName returns the "missing translation" text (with the title in quotes). Return the title instead.
    [HarmonyPatch(typeof(CardInfo), nameof(CardInfo.CardName), MethodType.Getter)]
    internal static class CardName_MissingTranslation
    {
        static void Postfix(CardInfo __instance, ref string __result)
        {
            var ls = __instance.LocalizedCardName;
            if (ls == null || ls.IsEmpty || __result == null) return;
            var key = ls.TableEntryReference.Key;
            if (string.IsNullOrEmpty(key) || __result == key) return;
            if (__result.Contains("'" + key + "'") || __result.StartsWith("No translation found", StringComparison.Ordinal))
                __result = key;
        }
    }

    // UnboundLib registers mod card titles/descriptions as LocalizedString keys that don't exist in the game's
    // string tables, so every UI that localizes them shows the missing-translation text ('Title' in quotes).
    // Make a missing entry render as its key, which for mod cards is the intended text.
    internal static class MissingText
    {
        static string previous;
        public static void Apply(ManualLogSource log)
        {
            try
            {
                var db = UnityEngine.Localization.Settings.LocalizationSettings.StringDatabase;
                if (db == null) return;
                if (previous == null) previous = db.NoTranslationFoundMessage;
                db.NoTranslationFoundMessage = "{key}";
                log.LogInfo($"missing-translation text: '{previous}' -> '{{key}}'");
            }
            catch (Exception e) { log.LogWarning("missing-translation text not changed: " + e.Message); }
        }
        public static void Restore()
        {
            try { if (previous != null) UnityEngine.Localization.Settings.LocalizationSettings.StringDatabase.NoTranslationFoundMessage = previous; }
            catch { }
        }
    }

    // The game letterboxes to 16:9; on 16:10 screens (most Macs) nothing clears the bars, so old UI pixels ghost
    // there. A camera behind everything clears the whole screen to black. Also hosts the plugin's helper components.
    // DontSave keeps it through the first scene load, which destroys everything made before it.
    internal static class LetterboxClear
    {
        public static GameObject Create()
        {
            var go = new GameObject("rounds-port Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var cam = go.AddComponent<Camera>();
            cam.depth = -100;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;
            cam.rect = new Rect(0, 0, 1, 1);
            cam.useOcclusionCulling = false;
            return go;
        }
    }
}
