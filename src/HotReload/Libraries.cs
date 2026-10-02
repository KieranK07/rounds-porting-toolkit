using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoundsHotReload
{
    // What the shared ROUNDS libraries keep about a mod, and how to take it back out (and put the new copy in).
    // Everything goes through reflection on type names, so a library that isn't installed is simply skipped.
    // Where each piece lives was worked out from the decompiled libraries; see docs/HOTRELOAD.md.
    static partial class Libraries
    {
        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // ---------------------------------------------------------------- reflection helpers
        static Type T(string asm, string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == asm && a.GetType(full, false) is Type t) return t;
            return null;
        }
        static object S(Type t, string n) => t == null ? null : t.GetField(n, BF)?.GetValue(null) ?? t.GetProperty(n, BF)?.GetValue(null, null);
        static object I(object o, string n)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(n, BF | BindingFlags.DeclaredOnly); if (f != null) return f.GetValue(o);
                var p = t.GetProperty(n, BF | BindingFlags.DeclaredOnly); if (p != null) return p.GetValue(o, null);
            }
            return null;
        }
        static object Call(Type t, string m, object self, params object[] args)
        {
            var mi = t.GetMethods(BF).FirstOrDefault(x => x.Name == m && x.GetParameters().Length == args.Length);
            return mi?.Invoke(self, args);
        }
        static void RemoveWhere(IList l, Func<object, bool> p) { if (l == null) return; for (int i = l.Count - 1; i >= 0; i--) if (p(l[i])) l.RemoveAt(i); }

        // A delegate from the assembly, also through compiler closures in a library that capture one (RegisterHandshake).
        static bool Owned(Delegate d, Assembly a, int depth = 0)
        {
            if (d == null) return false;
            foreach (var inv in d.GetInvocationList())
            {
                if (inv.Method?.DeclaringType?.Assembly == a) return true;
                var tgt = inv.Target; if (tgt == null) continue;
                var tt = tgt.GetType();
                if (tt.Assembly == a) return true;
                if (depth < 3 && tt.IsDefined(typeof(CompilerGeneratedAttribute), false))
                    foreach (var f in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var v = f.GetValue(tgt);
                        if (v is Delegate inner && Owned(inner, a, depth + 1)) return true;
                        if (v != null && v.GetType().Assembly == a) return true;
                    }
            }
            return false;
        }

        static bool CardOwned(CardInfo ci, Assembly a) => ci != null && ci.GetComponents<MonoBehaviour>().Any(c => c != null && c.GetType().Assembly == a);

        static Type CardManager => T("UnboundLib", "UnboundLib.Utils.CardManager");
        static IDictionary PrefabCache => I(T("PhotonUnityNetworking", "Photon.Pun.PhotonNetwork") is Type pn ? S(pn, "PrefabPool") : null, "ResourceCache") as IDictionary;

        // ---------------------------------------------------------------- install (once, at startup)
        public static void Install(Harmony h)
        {
            // RarityLib refuses AddRarity after its Start. A reloaded mod asks again for a rarity it already added:
            // hand back the existing one instead of throwing (which would stop the mod's Awake on its first line).
            var ru = T("RarityLib", "RarityLib.Utils.RarityUtils");
            var add = ru?.GetMethod("AddRarity", BindingFlags.Public | BindingFlags.Static);
            if (add != null) Try(() => h.Patch(add, prefix: new HarmonyMethod(typeof(Libraries), nameof(AddRarityPrefix))));

            // Mods that reopen a plugin's DLL from disk (Classes Manager Reborn finds class handlers that way) get the
            // live hot-loaded copy instead of a second, unconnected one. And a hot mod's Location is its file.
            foreach (var name in new[] { "LoadFile", "LoadFrom" })
            {
                var m = typeof(Assembly).GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (m != null) Try(() => h.Patch(m, prefix: new HarmonyMethod(typeof(Libraries), nameof(LoadFilePrefix))));
            }
            TrackNamedRegistrations(h);
            var uv = AccessTools.Method(T("UnboundLib", "UnboundLib.Utils.UI.ToggleCardsMenuHandler"), "UpdateVisualsCardObj");
            if (uv != null) Try(() => h.Patch(uv, prefix: new HarmonyMethod(typeof(Libraries), nameof(UpdateVisualsPrefix))));
            var loc = AccessTools.PropertyGetter(typeof(Assembly).Assembly.GetType("System.Reflection.RuntimeAssembly") ?? typeof(Assembly), "Location");
            if (loc != null && loc.GetMethodBody() != null) Try(() => h.Patch(loc, postfix: new HarmonyMethod(typeof(Libraries), nameof(LocationPostfix))));
        }

        static void Try(Action a) { try { a(); } catch (Exception e) { HotReloadPlugin.Log.LogWarning("hook not installed: " + e.Message); } }

        static bool AddRarityPrefix(string name, ref int __result)
        {
            var ru = T("RarityLib", "RarityLib.Utils.RarityUtils");
            if (!(S(ru, "Finalized") is bool done) || !done) return true;
            foreach (DictionaryEntry e in (IDictionary)S(ru, "rarities"))
                if ((string)I(e.Value, "name") == name) { __result = (int)e.Key; return false; }
            HotReloadPlugin.Log.LogWarning($"RarityLib only adds new rarities at startup: '{name}' is Common until you restart");
            __result = 0;
            return false;
        }

        static bool LoadFilePrefix(string __0, ref Assembly __result)
        {
            var mod = HotLoader.ByPath(__0);
            if (mod == null) return true;
            __result = mod.Assembly;
            return false;
        }

        static void LocationPostfix(Assembly __instance, ref string __result)
        {
            if (string.IsNullOrEmpty(__result) && HotLoader.PathOf(__instance) is string p) __result = p;
        }

        // ---------------------------------------------------------------- unload
        // Card keys and instance ids of the cards removed, so other mods' references can be pointed at the new ones.
        // By reference: Unity resets a destroyed object's instance id to 0.
        internal static readonly Dictionary<object, string> RemovedCards = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);

        public static void Unload(HotMod m, HashSet<object> destroyed, Cleanup.Report r)
        {
            var a = m.Assembly;
            var cards = CardsOf(a);
            var keys = cards.Select(c => c.gameObject.name).ToList();   // before they're destroyed
            var categories = CategoriesOf(keys);
            for (int i = 0; i < cards.Count; i++) { RemovedCards[cards[i]] = keys[i]; RemoveCard(cards[i], destroyed); }
            HideMenuEntries(keys);
            // A reload keeps the category (and the user's view of it) unless the new copy no longer has cards there.
            if (m.Reloading) pendingCategories.UnionWith(categories);
            else r.Add("card categories", RemoveEmptyCategories(categories));
            UndoNamed(a, r);
            r.Add("cards", cards.Count);
            r.Add("network prefabs", PrunePool(a, destroyed));
            r.Add("UnboundLib registrations", UndoUnbound(a, m.Guids));
            r.Add("ModdingUtils registrations", UndoModdingUtils(a));
            r.Add("objects", SweepDontDestroyOnLoad(a, destroyed));
        }

        // Every card the mod built: the network prefab pool holds them all (also disabled and hidden cards).
        static List<CardInfo> CardsOf(Assembly a)
        {
            var set = new HashSet<CardInfo>();
            if (PrefabCache is IDictionary pool)
                foreach (var v in pool.Values) if (v is GameObject go && go != null && go.GetComponent<CardInfo>() is CardInfo ci && CardOwned(ci, a)) set.Add(ci);
            if (S(CardManager, "cards") is IDictionary cards)
                foreach (var c in cards.Values) if (I(c, "cardInfo") is CardInfo ci && CardOwned(ci, a)) set.Add(ci);
            return set.ToList();
        }

        static void RemoveCard(CardInfo ci, HashSet<object> destroyed)
        {
            string key = ci.gameObject.name;
            var cm = CardManager;
            if (cm != null)
            {
                if (S(cm, "cards") is IDictionary cards && cards.Contains(key) && ReferenceEquals(I(cards[key], "cardInfo"), ci)) cards.Remove(key);
                if (S(cm, "activeCards") is IList active) while (active.Contains(ci)) active.Remove(ci);   // re-read: BuildCard replaces it
                if (S(cm, "inactiveCards") is IList inactive) while (inactive.Contains(ci)) inactive.Remove(ci);
                if (CardChoice.instance != null && S(cm, "activeCards") is IList now) CardChoice.instance.cards = now.Cast<CardInfo>().ToArray();
            }
            else if (CardChoice.instance != null) CardChoice.instance.cards = CardChoice.instance.cards.Where(c => !ReferenceEquals(c, ci)).ToArray();
            if (PrefabCache is IDictionary pool && pool.Contains(key) && ReferenceEquals(pool[key], ci.gameObject)) pool.Remove(key);

            if (S(T("ModdingUtils", "ModdingUtils.Utils.Cards"), "instance") is object mu && I(mu, "hiddenCards") is IList hidden) while (hidden.Contains(ci)) hidden.Remove(ci);
            if (T("RarityLib", "RarityLib.Utils.RarityUtils") is Type ru)
                foreach (var f in new[] { "CardRarities", "CardRaritiesAdd", "CardRaritiesMul" }) (S(ru, f) as IDictionary)?.Remove(ci);
            if (T("ClassesManagerReborn", "ClassesManagerReborn.ClassesRegistry") is Type cr)
            {
                (S(cr, "Registry") as IDictionary)?.Remove(ci);
                if (S(cr, "ClassInfos") is IList l) while (l.Contains(ci)) l.Remove(ci);
            }
            (S(T("ModsPlus", "ModsPlus.Patches.CardBarPatches"), "customAbbreviations") as IDictionary)?.Remove(ci);

            destroyed.Add(ci);
            if (ci.gameObject.scene.IsValid()) { destroyed.Add(ci.gameObject); Object.DestroyImmediate(ci.gameObject); }
        }

        // Non-card prefabs the mod registered for networking (projectiles, effects): a stale entry would win over
        // the new one, because registering an existing name keeps the old prefab.
        static int PrunePool(Assembly a, HashSet<object> destroyed)
        {
            if (!(PrefabCache is IDictionary pool)) return 0;
            int n = 0;
            foreach (var k in pool.Keys.Cast<object>().ToList())
            {
                var go = pool[k] as GameObject;
                if (go == null || destroyed.Contains(go) || go.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c != null && c.GetType().Assembly == a))
                { pool.Remove(k); n++; }
            }
            return n;
        }

        static int UndoUnbound(Assembly a, List<string> guids)
        {
            int n = 0;
            var gmm = T("UnboundLib", "UnboundLib.GameModes.GameModeManager");
            if (gmm != null)
            {
                foreach (var f in new[] { "hooks", "onceHooks" })
                    if (S(gmm, f) is IDictionary hooks)
                        foreach (DictionaryEntry e in hooks)
                            if (e.Value is IList l)
                            {
                                int before = l.Count;
                                RemoveWhere(l, reg => Owned(I(I(reg, "Hook"), "Action") as Delegate, a) || (string)I(reg, "Identifier") == a.GetName().Name);
                                n += before - l.Count;
                            }
                // (Cast<DictionaryEntry>() doesn't work on a generic Dictionary seen as IDictionary: go by key.)
                if (S(gmm, "handlers") is IDictionary handlers)
                    foreach (var key in handlers.Keys.Cast<object>().Where(k => handlers[k] != null && handlers[k].GetType().Assembly == a).ToList())
                    {
                        if (S(gmm, "CurrentHandlerID") as string == key as string) gmm.GetMethod("SetGameMode", new[] { typeof(string) })?.Invoke(null, new object[] { null });
                        Call(gmm, "RemoveHandler", null, key); n++;
                    }
            }
            var nm = T("UnboundLib", "UnboundLib.NetworkingManager");
            if (nm != null && S(nm, "events") is IDictionary ev)
            {
                var dead = ev.Keys.Cast<string>().Where(k => Owned(ev[k] as Delegate, a)
                    || guids.Any(g => k == "ModLoader_" + g + "_StartHandshake" || k == "ModLoader_" + g + "_FinishHandshake")).ToList();
                var closures = new HashSet<object>(dead.Select(k => (ev[k] as Delegate)?.Target).Where(t => t != null));
                foreach (var k in dead) ev.Remove(k);
                n += dead.Count;
                RemoveWhere(S(T("UnboundLib", "UnboundLib.Unbound"), "handShakeActions") as IList, x => x == null || closures.Contains(((Delegate)x).Target) || Owned((Delegate)x, a));
                if (S(nm, "rpcMethodCache") is IDictionary cache)
                    foreach (var k in cache.Keys.Cast<object>().Where(k => (I(k, "Item1") as Type)?.Assembly == a).ToList()) cache.Remove(k);
            }
            var mo = T("UnboundLib", "UnboundLib.Utils.UI.ModOptions");
            if (mo != null)
            {
                if (S(mo, "modMenus") is IList menus)
                {
                    var gone = menus.Cast<object>().Where(x => Owned(I(x, "guiAction") as Delegate, a) || Owned(I(x, "buttonAction") as Delegate, a)).ToList();
                    foreach (var x in gone) menus.Remove(x);
                    RemoveModMenuUi(gone.Select(x => I(x, "menuName") as string).Where(x => x != null));
                    n += gone.Count;
                }
                if (S(mo, "GUIListeners") is IDictionary gl)
                    foreach (var k in gl.Keys.Cast<object>().Where(k => Owned(I(gl[k], "guiAction") as Delegate, a)).ToList()) { gl.Remove(k); n++; }
            }
            RemoveWhere(S(CardManager, "FirstStartCallbacks") as IList, d => Owned(d as Delegate, a));
            return n;
        }

        static int UndoModdingUtils(Assembly a)
        {
            int n = 0;
            if (S(T("ModdingUtils", "ModdingUtils.Utils.Cards"), "instance") is object inst)
                foreach (var f in new[] { "removalCallbacks", "cardValidationFunctions" })
                    if (I(inst, f) is IList l) { int b = l.Count; RemoveWhere(l, d => Owned(d as Delegate, a)); n += b - l.Count; }
            var ihT = T("ModdingUtils", "ModdingUtils.GameModes.InterfaceGameModeHooksManager");
            if (S(ihT, "instance") is object ih)
                foreach (var f in ihT.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Where(f => typeof(IList).IsAssignableFrom(f.FieldType)))
                    if (f.GetValue(ih) is IList l) { int b = l.Count; RemoveWhere(l, o => o == null || o.GetType().Assembly == a); n += b - l.Count; }
            return n;
        }

        // DontDestroyOnLoad objects the mod made itself (helpers, managers). Ones it shares with the game or other
        // mods only lose its components.
        static int SweepDontDestroyOnLoad(Assembly a, HashSet<object> destroyed)
        {
            var probe = new GameObject("HotReload probe");
            Object.DontDestroyOnLoad(probe);
            var roots = probe.scene.GetRootGameObjects();
            Object.DestroyImmediate(probe);
            int n = 0;
            foreach (var root in roots)
            {
                if (root == null) continue;
                var mbs = root.GetComponentsInChildren<MonoBehaviour>(true).Where(c => c != null).ToArray();
                if (!mbs.Any(c => c.GetType().Assembly == a)) continue;
                bool shared = mbs.Any(c =>
                {
                    var ca = c.GetType().Assembly; var cn = ca.GetName().Name;
                    return ca != a && !(cn == "Assembly-CSharp" || cn == "Assembly-CSharp-firstpass" || cn.StartsWith("UnityEngine") || cn.StartsWith("Unity.") || cn.StartsWith("Photon"));
                });
                if (shared) continue;   // its own components go in the component pass
                destroyed.Add(root);
                Object.DestroyImmediate(root);
                n++;
            }
            return n;
        }

        // The toggle menu is built once and cardObjs/defaultCardActions are index-aligned, so a removed card's
        // button stays in those but leaves everything that walks the visible cards (its category list and the
        // scroll view, which the menu re-sorts by looking each card up by name). MenuEntry puts it back.
        sealed class Parked { public Transform Parent; public int Sibling; public bool Active; public string Category; }
        static readonly Dictionary<string, Parked> parked = new Dictionary<string, Parked>();
        static GameObject parkingLot;

        static void HideMenuEntries(List<string> keys)
        {
            var tc = T("UnboundLib", "UnboundLib.Utils.UI.ToggleCardsMenuHandler");
            if (!(S(tc, "cardObjs") is IDictionary cardObjs) || keys.Count == 0) return;
            var byCat = I(S(tc, "instance"), "cardObjectsInCategory") as IDictionary;
            if (parkingLot == null)
            {
                parkingLot = new GameObject("HotReload parked menu cards");
                parkingLot.SetActive(false);
                Object.DontDestroyOnLoad(parkingLot);
                parkingLot.hideFlags = HideFlags.HideAndDontSave;
            }
            var set = new HashSet<string>(keys);
            foreach (var g in cardObjs.Keys.Cast<GameObject>().Where(g => g != null && set.Contains(g.name) && !parked.ContainsKey(g.name)).ToList())
            {
                var p = new Parked { Parent = g.transform.parent, Sibling = g.transform.GetSiblingIndex(), Active = g.activeSelf };
                if (byCat != null)
                    foreach (DictionaryEntry e in byCat)
                        if (e.Value is List<GameObject> l && l.Remove(g)) p.Category = (string)e.Key;
                g.SetActive(false);
                g.transform.SetParent(parkingLot.transform, false);
                parked[g.name] = p;
            }
        }

        static void Unpark(GameObject g)
        {
            if (!parked.TryGetValue(g.name, out var p)) return;
            parked.Remove(g.name);
            var parent = p.Parent;
            if (parent == null && p.Category != null && S(MenuType, "scrollViews") is IDictionary views && views.Contains(p.Category))
                parent = ((Transform)views[p.Category]).Find("Viewport/Content");   // its category was removed and rebuilt
            if (parent != null) { g.transform.SetParent(parent, false); g.transform.SetSiblingIndex(Math.Min(p.Sibling, parent.childCount - 1)); }
            var byCat = I(S(T("UnboundLib", "UnboundLib.Utils.UI.ToggleCardsMenuHandler"), "instance"), "cardObjectsInCategory") as IDictionary;
            if (p.Category != null && byCat?[p.Category] is List<GameObject> l && !l.Contains(g)) l.Add(g);
            g.SetActive(p.Active);
        }

        // The menu refreshes a card's look by name; one that's mid-swap (or removed) isn't registered: skip it.
        static bool UpdateVisualsPrefix(GameObject cardObject, bool? cardEnabled)
        {
            if (cardEnabled.HasValue || cardObject == null) return true;
            return S(CardManager, "cards") is IDictionary cards && cards.Contains(cardObject.name);
        }

        // ---------------------------------------------------------------- after a live load
        // Things that only happen once at boot: put the new copy's cards into the toggle menu (with their saved
        // on/off state), run its Classes Manager handlers, set up its bundle rarities and themes, and fire its
        // all-cards callbacks. Waits until its cards stop appearing (BuildCard finishes a few frames late).
        public static IEnumerator AfterLiveLoad(HotMod m)
        {
            var cm = CardManager;
            int last = -1, stable = 0;
            for (int f = 0; f < 600 && stable < 15; f++)
            {
                yield return null;
                int count = CardsOf(m.Assembly).Count;
                stable = count == last ? stable + 1 : 0;
                last = count;
            }
            if (!HotLoader.IsLive(m.Assembly)) yield break;
            var mine = CardsOf(m.Assembly);
            try
            {
                foreach (var ci in mine)
                {
                    MenuEntry(ci.gameObject.name);
                    if (cm != null && S(cm, "cards") is IDictionary cards && cards.Contains(ci.gameObject.name)
                        && I(I(cards[ci.gameObject.name], "config"), "Value") is bool on && !on)
                        Call(cm, "DisableCard", null, ci, false);
                }
                FlushPendingCategories();
                RefreshMenu(CategoriesOf(mine.Select(c => c.gameObject.name)));
                SetUpAdders(m.Assembly);
                AddCreditsUi(m.Assembly);
                AddModMenuUi(m.Assembly);
                RemapClassesManager(mine);
                if (S(cm, "FirstStartCallbacks") is IList cbs && S(cm, "allCards") is CardInfo[] all)
                    foreach (var d in cbs.Cast<Delegate>().Where(d => Owned(d, m.Assembly)).ToList())
                        try { d.DynamicInvoke(new object[] { all }); } catch (Exception e) { HotReloadPlugin.Log.LogWarning("all-cards callback: " + e.InnerException?.Message); }
                DedupeCategories();
            }
            catch (Exception e) { HotReloadPlugin.Log.LogWarning("after-load setup: " + e); }
            if (mine.Count > 0) HotReloadPlugin.Log.LogInfo($"{m.Name}: {mine.Count} cards in the toggle menu");
            yield return RunClassHandlers(m.Assembly);
        }

        // Rebind the toggle-menu button for this card key (or add one): its old action captured the old card.
        static void MenuEntry(string key)
        {
            var cm = CardManager; var tc = T("UnboundLib", "UnboundLib.Utils.UI.ToggleCardsMenuHandler");
            if (cm == null || tc == null || S(tc, "instance") == null) return;
            var cards = (IDictionary)S(cm, "cards"); if (!cards.Contains(key)) return;
            EnsureCategory(I(cards[key], "category") as string);
            var cardObjs = (IDictionary)S(tc, "cardObjs");
            var cardObj = cardObjs.Keys.Cast<GameObject>().FirstOrDefault(g => g != null && g.name == key);
            bool isNew = cardObj == null;
            if (isNew)
            {
                var cat = (string)I(cards[key], "category");
                if (!(S(tc, "scrollViews") is IDictionary views) || !views.Contains(cat)) return;   // new category: shows after a restart
                var inst = S(tc, "instance");
                cardObj = Object.Instantiate((GameObject)I(inst, "cardObjAsset"), ((Transform)views[cat]).Find("Viewport/Content"));
                cardObj.name = key;
                var byCat = (IDictionary)I(inst, "cardObjectsInCategory");
                if (!byCat.Contains(cat)) byCat[cat] = new List<GameObject>();
                ((List<GameObject>)byCat[cat]).Add(cardObj);
                ((IList)I(inst, "buttonsToDisable")).Add(cardObj.GetComponent<UnityEngine.UI.Button>());
            }
            else
            {
                Unpark(cardObj);   // back into its scroll view and category list before anything looks at its parents
                if (T("UnboundLib", "UnboundLib.Cards.MenuCard") is Type menuCard)
                    foreach (var g in cardObj.GetComponentsInChildren(menuCard, true).Select(c => c.gameObject).ToList()) Object.DestroyImmediate(g);
            }

            var canvas = (GameObject)S(tc, "cardMenuCanvas"); var view = cardObj.transform.parent.parent.parent.gameObject;
            bool cW = canvas.activeSelf, vW = view.activeSelf, oW = cardObj.activeSelf;
            canvas.SetActive(true); view.SetActive(true); cardObj.SetActive(true);
            try { Call(tc, "SetupCardVisuals", null, (CardInfo)I(cards[key], "cardInfo"), cardObj); }
            finally { cardObj.SetActive(isNew ? false : oW); view.SetActive(vW); canvas.SetActive(cW); }

            Action act = () =>
            {
                var c = cards[key]; var ci = (CardInfo)I(c, "cardInfo");
                if ((bool)I(c, "enabled")) Call(cm, "DisableCard", null, ci, true); else Call(cm, "EnableCard", null, ci, true);
                Call(tc, "UpdateVisualsCardObj", null, cardObj, null);
            };
            var defaults = (IList)S(tc, "defaultCardActions");
            if (isNew) { cardObjs[cardObj] = act; defaults.Add(act); }
            else
            {
                var idx = Call(tc, "GetActionIndex", null, cardObj) is int i ? i : -1;
                cardObjs[cardObj] = act;
                if (idx >= 0 && idx < defaults.Count) defaults[idx] = act;
            }
            var ev = new UnityEngine.UI.Button.ButtonClickedEvent(); ev.AddListener(new UnityEngine.Events.UnityAction(act));
            cardObj.GetComponent<UnityEngine.UI.Button>().onClick = ev;
            // Just the on/off shade; the animated refresh happens once for the whole category (RefreshMenu).
            if (cardObj.transform.Find("Darken/Darken") is Transform shade) shade.gameObject.SetActive(!(I(cards[key], "enabled") is bool en && en));
        }

        // RarityAdder / ThemeAdder components on the mod's prefabs normally run once at boot.
        static void SetUpAdders(Assembly a)
        {
            foreach (var (asm, type) in new[] { ("RarityLib", "RarityLib.Utils.RarityAdder"), ("CardThemeLib", "CardThemeLib.ThemeAdder") })
            {
                var t = T(asm, type); if (t == null) continue;
                foreach (Component c in Resources.FindObjectsOfTypeAll(t))
                    if (c != null && c.GetComponentInParent<CardInfo>() is CardInfo ci && CardOwned(ci, a))
                        try { Call(t, "SetUp", c); } catch { }
            }
        }

        // Other mods' class definitions that pointed at the old cards now point at the new ones (by card key).
        static void RemapClassesManager(List<CardInfo> fresh)
        {
            var cr = T("ClassesManagerReborn", "ClassesManagerReborn.ClassesRegistry"); if (cr == null || !(S(cr, "Registry") is IDictionary reg)) return;
            var byKey = fresh.GroupBy(c => c.gameObject.name).ToDictionary(g => g.Key, g => g.First());
            CardInfo Map(CardInfo c) => (object)c != null && RemovedCards.TryGetValue(c, out var k) ? (byKey.TryGetValue(k, out var n) ? n : null) : c;
            foreach (var co in reg.Values)
            {
                foreach (var f in new[] { "whiteList", "blackList" })
                    if (I(co, f) is List<CardInfo> l)
                        for (int i = l.Count - 1; i >= 0; i--) { var mapped = Map(l[i]); if ((object)mapped != null) l[i] = mapped; else l.RemoveAt(i); }
                if (I(co, "RequiredClassesTree") is CardInfo[][] tree)
                    co.GetType().GetField("RequiredClassesTree", BF)?.SetValue(co, tree.Select(x => x.Select(Map).Where(c => (object)c != null).ToArray()).ToArray());
            }
        }

        // Classes Manager runs ClassHandler.Init/PostInit (and bundle ClassCardHandlers) once at boot.
        static IEnumerator RunClassHandlers(Assembly a)
        {
            var chT = T("ClassesManagerReborn", "ClassesManagerReborn.ClassHandler"); if (chT == null) yield break;
            var cchT = T("ClassesManagerReborn", "ClassesManagerReborn.ClassCardHandler");
            var handlers = new List<object>();
            foreach (var t in SafeTypes(a))
                if (t.IsClass && !t.IsAbstract && t.IsSubclassOf(chT))
                    try { handlers.Add(Activator.CreateInstance(t)); } catch { }
            var cards = cchT == null ? new List<Component>() : Resources.FindObjectsOfTypeAll(cchT).Cast<Component>()
                .Where(c => c != null && c.GetComponent<CardInfo>() is CardInfo ci && CardOwned(ci, a)).ToList();
            if (handlers.Count == 0 && cards.Count == 0) yield break;
            yield return RunAll(handlers.Select(h => (IEnumerator)Call(chT, "Init", h)).Concat(cards.Select(c => (IEnumerator)Call(cchT, "Regester", c))));
            yield return RunAll(handlers.Select(h => (IEnumerator)Call(chT, "PostInit", h)).Concat(cards.Select(c => (IEnumerator)Call(cchT, "PostRegester", c))));
            HotReloadPlugin.Log.LogInfo($"{a.GetName().Name}: ran {handlers.Count + cards.Count} Classes Manager handlers");
        }

        static IEnumerator RunAll(IEnumerable<IEnumerator> jobs)
        {
            int running = 0;
            IEnumerator Wrap(IEnumerator e)
            {
                running++;
                while (true)
                {
                    object cur;
                    try { if (e == null || !e.MoveNext()) break; cur = e.Current; }
                    catch (Exception ex) { HotReloadPlugin.Log.LogWarning("class handler: " + ex.Message); break; }
                    yield return cur;
                }
                running--;
            }
            foreach (var j in jobs.ToList()) HotReloadPlugin.Instance.StartCoroutine(Wrap(j));
            for (int f = 0; running > 0 && f < 300; f++) yield return null;
        }

        static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        // Some mods append a category to every card on load without checking (KeysCards' __NonTreasure__).
        static void DedupeCategories()
        {
            if (!(S(CardManager, "cards") is IDictionary cards)) return;
            foreach (var c in cards.Values)
                if (I(c, "cardInfo") is CardInfo ci && ci != null)
                {
                    if (ci.categories != null) ci.categories = ci.categories.Where(x => x != null).Distinct().ToArray();
                    if (ci.blacklistedCategories != null) ci.blacklistedCategories = ci.blacklistedCategories.Where(x => x != null).Distinct().ToArray();
                }
        }
    }
}
