using System;
using System.Reflection;
using FriendlyFoe;
using UnityEngine;
using UnityEngine.Localization;

// Methods of this class are CLONED by compatfix into each patched mod as <mod>.__RoundsCompat.
// Keep them self-contained: only reference game/Unity/mscorlib types and other members of this class.
public static class __RoundsCompat
{
    private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static FieldInfo s_maxHealth;
    private static FieldInfo s_teamID;
    private static FieldInfo s_cardName;

    // stfld CharacterData::maxHealth -> raw write of private m_maxHealth (no Titan-achievement side effect of set_MaxHealth)
    public static void SetMaxHealthRaw(CharacterData data, float value)
    {
        if ((object)data == null) throw new NullReferenceException();
        if (s_maxHealth == null) s_maxHealth = typeof(CharacterData).GetField("m_maxHealth", BF);
        s_maxHealth.SetValue(data, value);
    }

    // stfld Player::teamID -> raw write of private m_teamID (never AssignTeamID: Photon props side effect)
    public static void SetTeamIDRaw(Player player, int value)
    {
        if ((object)player == null) throw new NullReferenceException();
        if (s_teamID == null) s_teamID = typeof(Player).GetField("m_teamID", BF);
        s_teamID.SetValue(player, value);
    }

    // ldsfld Optionshandler::vol_Master / vol_Sfx -> current slider value, 0..1 (old statics were linear 0..1). Fallback 1.
    public static float GetVolume(string key)
    {
        try
        {
            Optionshandler oh = Optionshandler.instance;
            if (oh == null) return 1f;
            OptionsData od = oh.OptionsData;
            if (od == null) return 1f;
            var list = od.SettingsList;
            if (list == null) return 1f;
            for (int i = 0; i < list.Count; i++)
            {
                OptionsData.SettingsData s = list[i];
                if (s != null && s.m_key == key) return Mathf.Clamp01(s.CurrentValueSliderNormalized);
            }
        }
        catch (Exception) { }
        return 1f;
    }

    // ldsfld Optionshandler::lockMouse / lockStick -> the aim-in-8-directions toggle ("OPTION_MOUSE_AIM8DIR" /
    // "OPTION_CONTROLLER_AIM8DIR"), which PlayerInput reads where it used to read those statics. Fallback false.
    public static bool GetToggle(string key)
    {
        try
        {
            Optionshandler oh = Optionshandler.instance;
            if (oh == null) return false;
            OptionsData od = oh.OptionsData;
            if (od == null) return false;
            var list = od.SettingsList;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                OptionsData.SettingsData s = list[i];
                if (s != null && s.m_key == key) return s.CurrentValueToggle;
            }
        }
        catch (Exception) { }
        return false;
    }

    // ldfld CardInfo::cardName (now private; UnboundLib 4 never fills it for mod cards). Same order as ModdingUtils'
    // CompatShims.GetCardName so cross-mod name comparisons agree: legacy field if non-empty -> for UnboundLib
    // CustomCard cards the m_localizedCardName entry key (= GetTitle()) -> CardName property -> GameObject name.
    public static string CardName(CardInfo card)
    {
        if ((object)card == null) throw new NullReferenceException();
        try
        {
            if (s_cardName == null) s_cardName = typeof(CardInfo).GetField("cardName", BF);
            string raw = (string)s_cardName.GetValue(card);
            if (!string.IsNullOrEmpty(raw)) return raw;
        }
        catch (Exception) { }
        try
        {
            LocalizedString ls = card.LocalizedCardName;
            if (ls != null && !ls.IsEmpty && IsCustomCard(card))
            {
                string key = ls.TableEntryReference.Key;
                if (!string.IsNullOrEmpty(key)) return key;
            }
        }
        catch (Exception) { }
        try
        {
            string name = card.CardName;
            if (name != null) return name;
        }
        catch (Exception) { }
        return card.name ?? "";
    }

    // card.GetComponent<UnboundLib.Cards.CustomCard>() != null, without a compile-time UnboundLib reference
    private static bool IsCustomCard(CardInfo card)
    {
        MonoBehaviour[] all = card.GetComponents<MonoBehaviour>();
        for (int i = 0; i < all.Length; i++)
        {
            if ((object)all[i] == null) continue;
            for (Type t = all[i].GetType(); t != null; t = t.BaseType)
                if (t.FullName == "UnboundLib.Cards.CustomCard") return true;
        }
        return false;
    }

    // ldfld CardInfo::cardDestription (private now; UnboundLib 4 leaves it empty for mod cards) -> legacy field if
    // non-empty, else the localized description the game shows (CardDescription), else "".
    public static string CardDescription(CardInfo card)
    {
        if ((object)card == null) throw new NullReferenceException();
        try
        {
            string raw = (string)GameField("CardInfo", "cardDestription").GetValue(card);
            if (!string.IsNullOrEmpty(raw)) return raw;
        }
        catch (Exception) { }
        try { return card.CardDescription ?? ""; }
        catch (Exception) { return ""; }
    }

    // Any other game field that's private now (the runtime throws FieldAccessException): read and write it through
    // reflection. typeName is the reflection name ("Outer+Inner"). ponytail: no FieldInfo cache, add one if a mod
    // does this per frame and it shows up in a profile.
    private static FieldInfo GameField(string typeName, string name) =>
        typeof(Player).Assembly.GetType(typeName, true).GetField(name, BF | BindingFlags.Static | BindingFlags.Public);

    public static object GetGameField(object obj, string typeName, string name) => GameField(typeName, name).GetValue(obj);

    public static void SetGameField(object obj, object value, string typeName, string name) => GameField(typeName, name).SetValue(obj, value);

    public static void SetStaticGameField(object value, string typeName, string name) => GameField(typeName, name).SetValue(null, value);

    // A Harmony patch's __result from ObjectsToSpawn.SpawnObject, which returned GameObject[] and returns
    // PoolableWrapper[] now: the objects inside, as the patch used to see them.
    public static GameObject[] PooledObjects(PoolableWrapper[] wrappers)
    {
        if (wrappers == null) return null;
        var objects = new GameObject[wrappers.Length];
        for (int i = 0; i < wrappers.Length; i++) objects[i] = wrappers[i]?.Instance;
        return objects;
    }

    // stfld CardInfo::cardName (private now) -> raw write, so CardName() and ModdingUtils' name lookups still see it.
    // The title the game shows comes from m_localizedCardName.
    public static void SetCardNameRaw(CardInfo card, string value)
    {
        if ((object)card == null) throw new NullReferenceException();
        if (s_cardName == null) s_cardName = typeof(CardInfo).GetField("cardName", BF);
        s_cardName.SetValue(card, value);
    }

    // UIHandler's screen texts take a LocalizedString now. The old string overloads: run the new method with an empty
    // LocalizedString (it clears the text), then show the raw text, untranslated, through UILocalizedString.
    public static void ShowJoinGameText(UIHandler ui, string text, Color color)
    {
        if ((object)ui == null) throw new NullReferenceException();
        ui.ShowJoinGameText(new LocalizedString(), color);
        if (ui.m_localizedJoinGameText != null) ui.m_localizedJoinGameText.ResetReference(text);
    }

    public static void DisplayScreenText(UIHandler ui, Color color, string text, float speed)
    {
        if ((object)ui == null) throw new NullReferenceException();
        ui.DisplayScreenText(color, new LocalizedString(), speed);
        if (ui.gameOverText != null) ui.gameOverText.ResetReference(text);
    }

    public static void DisplayScreenTextLoop(UIHandler ui, Color color, string text)
    {
        if ((object)ui == null) throw new NullReferenceException();
        ui.DisplayScreenTextLoop(color, new LocalizedString());
        if (ui.gameOverText != null) ui.gameOverText.ResetReference(text);
    }

    public static void DisplayScreenTextLoopNoColor(UIHandler ui, string text)
    {
        if ((object)ui == null) throw new NullReferenceException();
        ui.DisplayScreenTextLoop(new LocalizedString());
        if (ui.gameOverText != null) ui.gameOverText.ResetReference(text);
    }

    // DontDestroyOnLoad from a plugin's Awake or constructor. BepInEx now starts plugins before the game has loaded any
    // scene, and loading the first one destroys every object made before it, DontDestroyOnLoad or not. DontSave on the
    // root GameObject keeps it (tested in game).
    public static void KeepAlive(UnityEngine.Object target)
    {
        UnityEngine.Object.DontDestroyOnLoad(target);
        GameObject go = target as GameObject;
        if ((object)go == null)
        {
            Component c = target as Component;
            if ((object)c != null) go = c.gameObject;
        }
        if ((object)go != null) go.transform.root.gameObject.hideFlags |= HideFlags.DontSave;
    }

    // A plugin Awake that looks up scene objects (FindObjectsOfType, Camera.main...). BepInEx now starts plugins before
    // the game has loaded any scene; on the old game the first scene was there. Runs it once the first scene has loaded.
    public static void AfterFirstScene(Action awake)
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isLoaded) { awake(); return; }
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += awake.OnFirstScene;
    }

    // Bound to the Awake it runs, so removing an equal delegate (same method, same Awake) unsubscribes it.
    private static void OnFirstScene(this Action awake, UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= awake.OnFirstScene;
        try { awake(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // PlayerManager.AddPlayerDiedAction(action) was removed; PlayerDiedAction is a public field now.
    public static void AddPlayerDiedAction(PlayerManager manager, Action<Player, int> action)
    {
        if ((object)manager == null) throw new NullReferenceException();
        manager.PlayerDiedAction = (Action<Player, int>)Delegate.Combine(manager.PlayerDiedAction, action);
    }

    // GrowPatch FixedTrickShot: trail is now IScaleTrailFromDamage (pooled ScaleTrailFromDamagePooled); lazy lookup.
    public static IScaleTrailFromDamage FindTrail(Component self, IScaleTrailFromDamage current)
    {
        if (current != null)
        {
            UnityEngine.Object uo = current as UnityEngine.Object;
            if ((object)uo == null || uo != null) return current;
        }
        if (self == null) return null;
        return self.transform.root.GetComponentInChildren<IScaleTrailFromDamage>();
    }
}
