using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RoundsHotReload
{
    // UnboundLib's menus are built once (toggle-cards menu) or once per main-menu load (credits, MODS), so a mod
    // that leaves or arrives mid-session needs its entries taken out of / put into the live UI by hand: its card
    // category, credits page, MODS button, update check and client-side flag. Same for every mod.
    static partial class Libraries
    {
        // ---------------------------------------------------------------- who registered what (by name)
        static readonly Dictionary<Assembly, List<(string kind, string key)>> named = new Dictionary<Assembly, List<(string, string)>>();

        static void TrackNamedRegistrations(Harmony h)
        {
            var ub = T("UnboundLib", "UnboundLib.Unbound"); if (ub == null) return;
            foreach (var m in ub.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                string kind = m.Name switch { "RegisterCredits" => "credits", "RegisterUpdateChecker" => "update", "RegisterClientSideMod" => "client", _ => null };
                if (kind == null || m.GetParameters().Length == 0 || m.GetParameters()[0].ParameterType != typeof(string)) continue;
                Try(() => h.Patch(m, prefix: new HarmonyMethod(typeof(Libraries), kind == "credits" ? nameof(CreditsPrefix) : kind == "update" ? nameof(UpdatePrefix) : nameof(ClientPrefix))));
            }
        }
        static void Record(string kind, string key)
        {
            var owner = Trackers.Owner(false);
            if (owner == null || key == null) return;
            if (!named.TryGetValue(owner, out var l)) named[owner] = l = new List<(string, string)>();
            if (!l.Contains((kind, key))) l.Add((kind, key));
        }
        static void CreditsPrefix(string __0) => Record("credits", __0);
        static void UpdatePrefix(string __0) => Record("update", __0);
        static void ClientPrefix(string __0) => Record("client", __0);

        static void UndoNamed(Assembly a, Cleanup.Report r)
        {
            if (!named.TryGetValue(a, out var list)) return;
            named.Remove(a);
            foreach (var (kind, key) in list)
            {
                if (kind == "credits") { RemoveCredits(key); r.Add("credits pages", 1); }
                else if (kind == "update") (I(S(T("UnboundLib", "UnboundLib.Utils.UI.UpdateChecker"), "Instance"), "modUpdateCheckers") as IDictionary)?.Remove(key);
                else if (kind == "client") (S(T("UnboundLib", "UnboundLib.Networking.SyncModClients"), "clientSideGUIDs") as IList)?.Remove(key);
            }
        }

        // ---------------------------------------------------------------- credits
        static readonly Dictionary<string, int> creditsSlot = new Dictionary<string, int>();

        static void RemoveCredits(string key)
        {
            var credits = S(T("UnboundLib", "UnboundLib.Utils.UI.Credits"), "Instance");
            (I(credits, "modCredits") as IDictionary)?.Remove(key);
            if (I(credits, "creditsMenus") is IDictionary pages && pages.Contains(key))
            {
                if (pages[key] is GameObject page && page != null) Object.Destroy(page);
                pages.Remove(key);
            }
            if (MenuButton(I(credits, "CreditsMenu") as GameObject, key) is GameObject button)
            {
                creditsSlot[key] = button.transform.GetSiblingIndex();
                Object.Destroy(button);
            }
        }

        static void AddCreditsUi(Assembly a)
        {
            if (!named.TryGetValue(a, out var list)) return;
            var ct = T("UnboundLib", "UnboundLib.Utils.UI.Credits");
            var credits = S(ct, "Instance");
            if (!(I(credits, "CreditsMenu") is GameObject menu) || menu == null) return;   // not on the main menu: built on the next load
            var pages = I(credits, "creditsMenus") as IDictionary;
            var all = I(credits, "modCredits") as IDictionary;
            foreach (var (kind, key) in list.Where(x => x.kind == "credits"))
            {
                if (all == null || !all.Contains(key) || MenuButton(menu, key) != null) continue;
                var slot = creditsSlot.TryGetValue(key, out var s) ? s : -1;
                var page = Call(T("UnboundLib", "UnboundLib.Utils.UI.MenuHandler"), "CreateMenu", null, key, null, menu, 30, true, true, null, true, slot) as GameObject;
                if (page == null) continue;
                if (pages != null) pages[key] = page;
                Call(ct, "AddModCredits", credits, all[key], page);
            }
        }

        // The button CreateMenu put under a menu page (it's named after the entry).
        static GameObject MenuButton(GameObject parentMenu, string name)
        {
            if (parentMenu == null) return null;
            var content = parentMenu.transform.Find("Group/Grid/Scroll View/Viewport/Content") ?? parentMenu.transform.Find("Group") ?? parentMenu.transform;
            return content.Find(name)?.gameObject;
        }

        // ---------------------------------------------------------------- MODS menu
        static GameObject ModsPage => I(S(T("UnboundLib", "UnboundLib.Utils.UI.ModOptions"), "instance"), "modOptionsMenu") as GameObject;

        static void RemoveModMenuUi(IEnumerable<string> names)
        {
            var mods = ModsPage;
            foreach (var n in names)
            {
                if (MenuButton(mods, n) is GameObject b) Object.Destroy(b);
                if (MainMenuHandler.instance != null && MainMenuHandler.instance.transform.Find("Canvas/ListSelector/" + n) is Transform page) Object.Destroy(page.gameObject);
            }
        }

        static void AddModMenuUi(Assembly a)
        {
            var mods = ModsPage;
            if (mods == null || !(S(T("UnboundLib", "UnboundLib.Utils.UI.ModOptions"), "modMenus") is IList menus)) return;
            foreach (var m in menus.Cast<object>().Where(m => Owned(I(m, "guiAction") as Delegate, a) || Owned(I(m, "buttonAction") as Delegate, a)).ToList())
            {
                var name = I(m, "menuName") as string;
                if (name == null || MenuButton(mods, name) != null) continue;
                var page = Call(T("UnboundLib", "UnboundLib.Utils.UI.MenuHandler"), "CreateMenu", null, name, I(m, "buttonAction"), mods, 60, true, false, null, true, -1) as GameObject;
                if (page == null) continue;
                try { (I(m, "guiAction") as Delegate)?.DynamicInvoke(page); }
                catch (Exception e) { HotReloadPlugin.Log.LogWarning($"building menu '{name}': {e.InnerException?.Message ?? e.Message}"); }
            }
        }

        // ---------------------------------------------------------------- toggle-cards categories
        static Type MenuType => T("UnboundLib", "UnboundLib.Utils.UI.ToggleCardsMenuHandler");

        static List<string> CategoriesOf(IEnumerable<string> cardKeys)
        {
            var cards = S(CardManager, "cards") as IDictionary;
            return cards == null ? new List<string>() : cardKeys.Where(cards.Contains).Select(k => I(cards[k], "category") as string).Where(c => c != null).Distinct().ToList();
        }

        static readonly HashSet<string> pendingCategories = new HashSet<string>();
        public static void FlushPendingCategories()
        {
            if (pendingCategories.Count == 0) return;
            var list = pendingCategories.ToList(); pendingCategories.Clear();
            var n = RemoveEmptyCategories(list);
            if (n > 0) HotReloadPlugin.Log.LogInfo($"removed {n} card categor{(n == 1 ? "y" : "ies")} the new copy no longer uses");
        }

        // A category with no cards left (all its mods removed) goes: from CardManager, the menu, its page and button.
        static int RemoveEmptyCategories(List<string> candidates)
        {
            var cm = CardManager; var tc = MenuType;
            if (cm == null || !(S(cm, "categories") is List<string> cats) || !(S(cm, "cards") is IDictionary cards)) return 0;
            var inst = S(tc, "instance");
            int n = 0;
            foreach (var cat in candidates.Where(c => c != "Vanilla" && cats.Contains(c)))
            {
                if (cards.Values.Cast<object>().Any(c => I(c, "category") as string == cat)) continue;
                if (I(inst, "currentCategory") as string == cat) ShowCategory("Vanilla");
                cats.Remove(cat);
                (S(cm, "categoryBools") as IDictionary)?.Remove(cat);   // the on/off setting stays in the config file
                if (S(tc, "scrollViews") is IDictionary views && views.Contains(cat))
                {
                    if (views[cat] is Transform v && v != null) Object.Destroy(v.gameObject);
                    views.Remove(cat);
                }
                (I(inst, "cardObjectsInCategory") as IDictionary)?.Remove(cat);
                if ((I(inst, "categoryContent") as Transform)?.Find(cat) is Transform button)
                {
                    if (button.GetComponentInChildren<Toggle>(true) is Toggle tg) (I(inst, "togglesToDisable") as IList)?.Remove(tg);
                    Object.Destroy(button.gameObject);
                }
                n++;
            }
            return n;
        }

        // Make sure a category exists in CardManager and the toggle menu, the way UnboundLib builds it at boot.
        static void EnsureCategory(string cat)
        {
            var cm = CardManager; var tc = MenuType; var inst = S(tc, "instance");
            if (cm == null || inst == null || cat == null || !(S(cm, "categories") is List<string> cats)) return;
            if (!cats.Contains(cat)) cats.Add(cat);
            if (S(cm, "categoryBools") is IDictionary bools && !bools.Contains(cat))
            {
                var bind = T("UnboundLib", "UnboundLib.Unbound").GetMethod("BindConfig", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).MakeGenericMethod(typeof(bool));
                bools[cat] = bind.Invoke(null, new object[] { "Card categories", cat, true, null });
            }
            var views = (IDictionary)S(tc, "scrollViews");
            if (!views.Contains(cat))
            {
                // Not UnboundLib's SetActive(view, false): a frame later it also hides the category being viewed.
                var v = Object.Instantiate((GameObject)I(inst, "cardScrollViewAsset"), (Transform)I(inst, "scrollViewTrans"));
                v.SetActive(false);
                v.name = cat;
                views[cat] = v.transform;
            }
            if (I(inst, "cardObjectsInCategory") is IDictionary byCat && !byCat.Contains(cat)) byCat[cat] = new List<GameObject>();

            var content = (Transform)I(inst, "categoryContent");
            if (content == null || content.Find(cat) != null) return;
            // Copy an existing category button rather than UnboundLib's template, so it looks like its siblings
            // (including anything other mods changed, e.g. Mac Compat Fixes' selected colour), then rewire it.
            var model = content.Cast<Transform>().FirstOrDefault(c => c.GetComponent<Button>() != null)?.gameObject;
            var b = Object.Instantiate(model ?? (GameObject)I(inst, "categoryButtonAsset"), content);
            b.GetComponent<Button>().onClick.RemoveAllListeners();
            if (b.GetComponentInChildren<Toggle>(true) is Toggle copied) copied.onValueChanged.RemoveAllListeners();
            b.SetActive(true);
            b.name = cat;
            SetText(b, cat);
            var order = new[] { "Vanilla" }.Concat(cats.OrderBy(x => x).Where(x => x != "Vanilla")).ToList();
            b.transform.SetSiblingIndex(Math.Max(0, Math.Min(order.IndexOf(cat), content.childCount - 1)));
            b.GetComponent<Button>().onClick.AddListener(() => ShowCategory(cat));
            var toggle = b.GetComponentInChildren<Toggle>(true);
            if (toggle != null)
            {
                (I(inst, "togglesToDisable") as IList)?.Add(toggle);
                toggle.isOn = Call(cm, "IsCategoryActive", null, cat) is bool on && on;
                Darken(cat, !toggle.isOn);
                toggle.onValueChanged.AddListener(value => SetCategoryEnabled(cat, value));
            }
        }

        static void SetText(GameObject go, string text)
        {
            var tmp = T("Unity.TextMeshPro", "TMPro.TextMeshProUGUI");
            if (tmp != null && go.GetComponentInChildren(tmp, true) is Component c) tmp.GetProperty("text")?.SetValue(c, text, null);
        }

        // What a category button does when clicked (UnboundLib's listener, for categories built here).
        static void ShowCategory(string cat)
        {
            var tc = MenuType; var inst = S(tc, "instance");
            if (inst == null || !(S(tc, "scrollViews") is IDictionary views) || !views.Contains(cat)) return;
            var canvas = (GameObject)S(tc, "cardMenuCanvas");
            var viewing = canvas.transform.Find("CardMenu/Top/Viewing");
            var tmp = T("Unity.TextMeshPro", "TMPro.TMP_Text");
            var label = tmp == null ? null : viewing?.GetComponentInChildren(tmp, true);
            var text = "Viewing: " + cat;
            if (label != null && tmp.GetProperty("text").GetValue(label, null) as string == text) return;
            if (label != null) tmp.GetProperty("text").SetValue(label, text, null);
            foreach (DictionaryEntry e in views)
            {
                Call(tc, "DisableCardsInCategory", inst, (string)e.Key);
                Call(tc, "SetActive", null, (Transform)e.Value, false);
            }
            var view = (Transform)views[cat];
            if (view.GetComponent<ScrollRect>() is ScrollRect sr) sr.normalizedPosition = new Vector2(0f, 1f);
            Call(tc, "SetActive", null, view, true);
            tc.GetField("currentCategory", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(inst, cat);
        }

        // What a category's on/off toggle does (UnboundLib's listener, for categories built here).
        static void SetCategoryEnabled(string cat, bool on)
        {
            var cm = CardManager; var tc = MenuType;
            if (!(S(tc, "scrollViews") is IDictionary views) || !views.Contains(cat)) return;
            Darken(cat, !on);
            if (S(cm, "categoryBools") is IDictionary bools && bools[cat] != null) bools[cat].GetType().GetProperty("Value").SetValue(bools[cat], on, null);
            foreach (Transform card in ((Transform)views[cat]).Find("Viewport/Content"))
                if (card.Find("Darken/Darken") is Transform d && !d.gameObject.activeInHierarchy && Call(cm, "GetCardInfoWithName", null, card.name) is CardInfo ci)
                    Call(cm, on ? "EnableCard" : "DisableCard", null, ci, true);
            Call(cm, on ? "EnableCategory" : "DisableCategory", null, cat);
            if (Call(cm, "GetCardsInCategory", null, cat) is string[] names && S(tc, "cardObjs") is IDictionary objs)
                foreach (var g in objs.Keys.Cast<GameObject>().Where(g => g != null && names.Contains(g.name)).ToList())
                    Call(tc, "UpdateVisualsCardObj", null, g, null);
        }

        // After cards were (re)built into the menu: one layout pass, the user's sort order, and if they're looking
        // at an affected category, UnboundLib's normal open (cards appear one per frame) instead of all at once.
        static void RefreshMenu(IEnumerable<string> categories)
        {
            var tc = MenuType; var inst = S(tc, "instance");
            if (inst == null) return;
            Call(tc, "UpdateCardColumnAmountMenus", null);
            if (tc.GetField("sortedByName", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is bool byName) Call(tc, "SortCardMenus", inst, byName);
            var cur = I(inst, "currentCategory") as string;
            var canvas = S(tc, "cardMenuCanvas") as GameObject;
            if (cur == null || canvas == null || !canvas.activeInHierarchy || !categories.Contains(cur) || !(S(tc, "scrollViews") is IDictionary views) || !views.Contains(cur)) return;
            Call(tc, "DisableCardsInCategory", inst, cur);
            Call(tc, "SetActive", null, (Transform)views[cur], true);
        }

        static void Darken(string cat, bool dark)
        {
            if (S(MenuType, "scrollViews") is IDictionary views && views.Contains(cat) && ((Transform)views[cat]).Find("Darken") is Transform d) d.gameObject.SetActive(dark);
        }
    }
}
