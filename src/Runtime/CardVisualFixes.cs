using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

// Card visuals in UnboundLib's toggle-cards menu (and anything else that nests a card inside a UI canvas).
// UnboundLib types are resolved by name, so nothing here breaks if UnboundLib changes or is missing.
namespace RoundsPort.Runtime
{
    internal static class UnboundUi
    {
        static Type menuCard, animHandler, toggleCards, toggleLevels;
        static MethodInfo toggleAnim;
        public static Type MenuCard => menuCard ?? (menuCard = Types.Find("UnboundLib.Cards.MenuCard"));
        public static Type AnimHandler => animHandler ?? (animHandler = Types.Find("UnboundLib.Utils.UI.CardAnimationHandler"));
        static Type ToggleCards => toggleCards ?? (toggleCards = Types.Find("UnboundLib.Utils.UI.ToggleCardsMenuHandler"));
        static Type ToggleLevels => toggleLevels ?? (toggleLevels = Types.Find("UnboundLib.Utils.UI.ToggleLevelMenuHandler"));

        static FieldInfo cardMenuField, mapInstanceField, mapCanvasField;

        public static GameObject CardMenuCanvas
        {
            get
            {
                if (cardMenuField == null && ToggleCards != null) cardMenuField = AccessTools.Field(ToggleCards, "cardMenuCanvas");
                return cardMenuField?.GetValue(null) as GameObject;
            }
        }

        public static GameObject MapMenuCanvas
        {
            get
            {
                if (mapInstanceField == null && ToggleLevels != null)
                {
                    mapInstanceField = AccessTools.Field(ToggleLevels, "instance");
                    mapCanvasField = AccessTools.Field(ToggleLevels, "mapMenuCanvas");
                }
                var inst = mapInstanceField?.GetValue(null);
                return inst == null ? null : mapCanvasField?.GetValue(inst) as GameObject;
            }
        }

        public static void ToggleAnimation(Component handler, bool on)
        {
            if (handler == null) return;
            if (toggleAnim == null) toggleAnim = AccessTools.Method(handler.GetType(), "ToggleAnimation", new[] { typeof(bool) });
            toggleAnim?.Invoke(handler, new object[] { on });
        }
    }

    // RarityLib NRE (x67 in Player.log): CardRarityColor.Awake adds Toggle to the parent CardVisuals'
    // toggleSelectionAction. UnboundLib's toggle menu then destroys the card's "Back" (4 rarity triangles), but the
    // delegates stay registered. When the card's CardVisuals.Start runs later (cards in the visible category start
    // deferred), ChangeSelected invokes Toggle on the destroyed components; RarityLib's Toggle prefix calls
    // GetComponentInParent on them and throws, which also aborts the rest of ChangeSelected (colours, curve
    // animations, card flip). Drop delegates whose target component is gone before they are invoked.
    [HarmonyPatch(typeof(CardVisuals), nameof(CardVisuals.ChangeSelected))]
    internal static class CardVisuals_DeadToggleTargets_Fix
    {
        static bool IsDead(Delegate d) => d.Target is UnityEngine.Object o && o == null;

        static void Prefix(CardVisuals __instance) => Prune(__instance);

        internal static void Prune(CardVisuals __instance)
        {
            var action = __instance.toggleSelectionAction;
            if (action == null) return;
            var list = action.GetInvocationList();
            bool any = false;
            foreach (var d in list) if (IsDead(d)) { any = true; break; }
            if (!any) return;
            Action<bool> kept = null;
            foreach (var d in list) if (!IsDead(d)) kept += (Action<bool>)d;
            __instance.toggleSelectionAction = kept;
        }
    }

    // Safety net for any other route into a destroyed CardRarityColor (RarityLib / Classes Manager prefixes).
    [HarmonyPatch(typeof(CardRarityColor), nameof(CardRarityColor.Toggle))]
    internal static class CardRarityColor_Destroyed_Fix
    {
        static Exception Finalizer(Exception __exception, CardRarityColor __instance)
            => __exception is NullReferenceException && __instance == null ? null : __exception;
    }

    [HarmonyPatch(typeof(CardVisuals), "Start")]
    internal static class CardVisualsStart_MenuFix
    {
        static void Prefix(CardVisuals __instance) => MenuCards.Fix(__instance);
    }

    internal static class MenuCards
    {
        internal static readonly List<Component> added = new List<Component>();

        public static int Fix(CardVisuals v)
        {
            if (v == null) return 0;
            int r = HideCoveringParticles(v) ? 1 : 0;
            var mc = UnboundUi.MenuCard == null ? null : v.GetComponentInParent(UnboundUi.MenuCard, true);
            var button = mc == null ? null : mc.transform.parent;
            if (button != null && button.GetComponent<MenuCardHover>() == null)
            {
                added.Add(button.gameObject.AddComponent<MenuCardHover>());
                r |= 2;
            }
            return r;
        }

        // The new card prefab draws its moving background with a world-space ParticleSystem under
        // Canvas/Front/Background/Particles (SpriteMask + SortingGroup on MostFront/8). In game the card's Canvas is a
        // root world-space canvas on MostFront/10, so the particles sit behind the art and text. Nested in a UI canvas
        // (UnboundLib's toggle menu: MostFront/0, ScreenSpaceCamera while open, Overlay while closed) the card Canvas
        // inherits the parent's sorting, so the particle group draws over the whole card: the dark navy panel hiding
        // art and text. Under an overlay canvas they would land at pixel coordinates in the world instead. Either way
        // they are never useful there (the menu draws its own opaque card background), so stop drawing them.
        static bool HideCoveringParticles(CardVisuals v)
        {
            var particles = v.transform.Find("Canvas/Front/Background/Particles");
            var canvas = v.transform.Find("Canvas")?.GetComponent<Canvas>();
            if (particles == null || canvas == null) return false;
            // The canvas whose sorting the card actually draws with: its own if it is a root or overrides sorting,
            // otherwise the nearest parent that does. Walked by hand so it also works on inactive (unopened) menus.
            var sorting = canvas;
            if (!canvas.overrideSorting)
                for (var t = canvas.transform.parent; t != null; t = t.parent)
                {
                    var c = t.GetComponent<Canvas>();
                    if (c == null) continue;
                    sorting = c;
                    if (c.overrideSorting) break;
                }
            if (sorting == canvas) return false;   // card canvas sorts itself (in game): particles are behind, as designed
            if (sorting.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                var group = particles.GetComponent<SortingGroup>();
                int layerId = group != null ? group.sortingLayerID : canvas.sortingLayerID;
                int order = group != null ? group.sortingOrder : canvas.sortingOrder;
                int lg = SortingLayer.GetLayerValueFromID(layerId), lc = SortingLayer.GetLayerValueFromID(sorting.sortingLayerID);
                if (lg < lc || (lg == lc && order < sorting.sortingOrder)) return false;   // already behind the card
            }
            bool changed = false;
            foreach (var r in particles.GetComponentsInChildren<Renderer>(true))
                if (r.enabled) { r.enabled = false; changed = true; }
            return changed;
        }

        // Cards that already ran Start before this copy of the plugin loaded (hot reload).
        public static string FixExisting()
        {
            int n = 0, hidden = 0, hover = 0;
            foreach (var v in UnityEngine.Object.FindObjectsOfType<CardVisuals>(true))
            {
                n++;
                int r = Fix(v);
                if ((r & 1) != 0) hidden++;
                if ((r & 2) != 0) hover++;
                CardVisuals_DeadToggleTargets_Fix.Prune(v);
            }
            return $"{n} cards, {hidden} with covering particles hidden, {hover} hover forwarders";
        }

        public static void RemoveAdded()
        {
            foreach (var c in added) if (c != null) UnityEngine.Object.Destroy(c);
            added.Clear();
        }
    }

    // UnboundLib only animates menu card art while the pointer is over the card (CardAnimationHandler keeps every
    // Animator / PositionNoise off otherwise). The handler sits on the card copy, but the copy's graphics belong to
    // the card's own nested Canvas, which has no GraphicRaycaster, so the pointer only ever hits the menu button's
    // Back/Darken images and the handler never gets OnPointerEnter: the art never animates. Forward the button's
    // hover to it.
    internal class MenuCardHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public bool Hovered { get; private set; }
        public void OnPointerEnter(PointerEventData eventData) => Set(true);
        public void OnPointerExit(PointerEventData eventData) => Set(false);

        void Set(bool on)
        {
            Hovered = on;
            if (UnboundUi.AnimHandler == null) return;
            foreach (var h in GetComponentsInChildren(UnboundUi.AnimHandler, true)) UnboundUi.ToggleAnimation(h, on);
        }
    }

    // UnboundLib's menu bundle predates ColorBlock.selectedColor, so every button/toggle in its card and map menus
    // has Unity's default selected tint (0.96 white). After a click the button stays "selected": the category button
    // turns into a white box with white text, blown out further by the camera's bloom. Old Unity showed the
    // highlighted tint for a selected button; use that, and a slightly stronger one for the category buttons so the
    // one just picked stays visible.
    internal static class MenuSelectedColors
    {
        public const string CategoriesPath = "CardMenu/Top/Categories/ButtonsScroll/Viewport/Content";
        static readonly Color unityDefault = ColorBlock.defaultColorBlock.selectedColor;

        static bool IsDefault(Color c)
            => Mathf.Abs(c.r - unityDefault.r) < 0.01f && Mathf.Abs(c.g - unityDefault.g) < 0.01f
               && Mathf.Abs(c.b - unityDefault.b) < 0.01f && c.a > 0.99f;

        public static int Apply(GameObject canvas)
        {
            if (canvas == null) return 0;
            var categories = canvas.transform.Find(CategoriesPath);
            int n = 0;
            foreach (var s in canvas.GetComponentsInChildren<Selectable>(true))
            {
                if (s.transition != Selectable.Transition.ColorTint) continue;
                var cb = s.colors;
                if (!IsDefault(cb.selectedColor)) continue;
                Color c = cb.highlightedColor;
                if (categories != null && s.transform.parent == categories)
                {
                    c = Color.Lerp(cb.highlightedColor, cb.pressedColor, 0.5f);
                    c.a = 1f;
                }
                if (c == cb.selectedColor) continue;   // stock Unity colour block: highlighted == selected already
                cb.selectedColor = c;
                s.colors = cb;
                n++;
            }
            return n;
        }
    }

    internal class CardVisualFixesRunner : MonoBehaviour
    {
        public static BepInEx.Logging.ManualLogSource Log;
        float next;
        int cardMenuDone;
        bool mapMenuWasActive;

        void Start()
        {
            try { Log?.LogInfo("menu card fix (existing): " + MenuCards.FixExisting()); }
            catch (Exception e) { Log?.LogWarning("menu card fix (existing cards) failed: " + e.Message); }
        }

        void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            try
            {
                // The card menu is built once, shortly after launch: fix it once its category buttons exist.
                var card = UnboundUi.CardMenuCanvas;
                if (card != null && card.GetInstanceID() != cardMenuDone)
                {
                    var cats = card.transform.Find(MenuSelectedColors.CategoriesPath);
                    if (cats != null && cats.childCount > 0)
                    {
                        cardMenuDone = card.GetInstanceID();
                        Log?.LogInfo($"menu selected-colour fix: {MenuSelectedColors.Apply(card)} controls");
                    }
                }
                // The map menu fills in as levels load: re-check each time it opens.
                var map = UnboundUi.MapMenuCanvas;
                bool mapActive = map != null && map.activeInHierarchy;
                if (mapActive && !mapMenuWasActive) MenuSelectedColors.Apply(map);
                mapMenuWasActive = mapActive;
            }
            catch (Exception e) { Log?.LogWarning("menu selected-colour fix failed: " + e.Message); next = float.MaxValue; }
        }

        void OnDestroy() => MenuCards.RemoveAdded();
    }
}
