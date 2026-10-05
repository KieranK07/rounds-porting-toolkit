using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

// Bench-only: drives ROUNDS by itself so the bench can test gameplay without anyone at the keyboard.
//   ROUNDS_PILOT=match   a real local Arms Race between two AI players. Every point ends in a pick phase, and the first
//                        card offered is the next untested card, which the AI picks (AIs take the first card), so cards
//                        go through the real pick path. Errors are logged against the last picked card.
//   ROUNDS_PILOT=host    the same online, two copies of the game in one private room through RoundsWithFriends' lobby
//   ROUNDS_PILOT=join    (how modded games are played online). Each copy's character gets the game's AI, each copy tests
//                        half the cards, and state.tsv records every player's cards and stats at each battle so the two
//                        copies can be compared for desyncs. ROUNDS_PILOT_ROOM is the file the host writes its room code to.
//   ROUNDS_PILOT_OUT     folder for cards.tsv, errors.tsv, state.tsv, shots/*.png and "done".
//   ROUNDS_PILOT_MINUTES time budget (default 20).
[BepInPlugin("rounds-bench.pilot", "rounds-bench pilot", "0.3.0")]
[BepInDependency("com.willis.rounds.unbound", BepInDependency.DependencyFlags.SoftDependency)]
public class Pilot : BaseUnityPlugin
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Pilot me;
    string mode, outDir;
    string current = "(startup)";
    int shot;
    bool pilotFailed, stopped;

    // a mod's exception that came out through a call the pilot made: recorded as the mod's, without the pilot's own
    // frames (an error with them means the pilot itself failed)
    void Caught(Exception e)
    {
        var inner = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
        Logger.LogWarning("pilot: caught " + inner);
        Record(inner.GetType().Name + ": " + inner.Message, inner.StackTrace);
    }
    readonly Dictionary<string, List<string>> errors = new Dictionary<string, List<string>>();
    readonly HashSet<string> shotFor = new HashSet<string>();
    readonly Queue<CardInfo> queue = new Queue<CardInfo>();
    readonly List<string> picked = new List<string>();
    bool offerNext;   // the next SpawnUniqueCard is the first card of a pick phase
    // ROUNDS_PILOT_NATURAL=1: the game deals its own offers (the AI still takes the first card)
    readonly bool natural = Environment.GetEnvironmentVariable("ROUNDS_PILOT_NATURAL") == "1";
    bool layoutLogged;
    int failedPicks;

    // every object under a card with its components, to see what mods that look things up by name find on the 2025 card
    static string Layout(Transform t, string path)
    {
        var here = path + "/" + t.name + "[" + string.Join(",", t.GetComponents<Component>().Where(x => x != null && !(x is Transform)).Select(x => x.GetType().Name)) + "]";
        return here + string.Concat(Enumerable.Range(0, t.childCount).Select(i => " " + Layout(t.GetChild(i), path + "/" + t.name)));
    }

    void Awake()
    {
        me = this;
        mode = Environment.GetEnvironmentVariable("ROUNDS_PILOT");
        outDir = Environment.GetEnvironmentVariable("ROUNDS_PILOT_OUT");
        if (string.IsNullOrEmpty(mode) || string.IsNullOrEmpty(outDir)) return;
        Directory.CreateDirectory(Path.Combine(outDir, "shots"));
        Application.logMessageReceived += OnLog;
        new Harmony("rounds-bench.pilot").PatchAll(typeof(Pilot).Assembly);
        new System.Threading.Thread(Watchdog) { IsBackground = true }.Start();
        StartCoroutine(Run());
    }

    void OnLog(string message, string stack, LogType type)
    {
        if (stopped || (type != LogType.Error && type != LogType.Exception)) return;
        // the game's own log lines that use LogError
        if (message.Contains("shader compiler platform") || message.StartsWith("Command Line") || message.StartsWith("Creating player")
            || message.StartsWith("Create room") || message.StartsWith("Added player") || message.StartsWith("Joined room")
            || message.StartsWith("Stack trace from when the above hook was added")
            || message.StartsWith("Coroutine couldn't be started")   // recorded with its caller by StartCoroutine_Inactive
            || message.Contains("progress achievement") || message.StartsWith("Failed to set status of STAT_")) return;   // Steam stats
        if (stack != null && stack.Contains("Pilot")) pilotFailed = true;
        Record(message, stack);
    }

    static string Frame(string f)
    {
        var m = System.Text.RegularExpressions.Regex.Match(f, @"^\(wrapper dynamic-method\) \S*?DMD<(.+?)::([^>]+)>\(");
        return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value + " (patched)" : f.Split('(')[0].Trim();
    }

    void Record(string message, string stack)
    {
        var frames = (stack ?? "").Split('\n').Select(l => l.Trim()).Select(l => l.StartsWith("at ") ? l.Substring(3) : l).Where(l => l.Length > 0).ToList();
        var first = frames.FirstOrDefault() ?? "";
        // where it came from: UnboundLib's hook runner names the mod ("[PICKEND HOOK] [Mod] threw..."); otherwise the first
        // frame in a mod namespace (not System, Unity or the hook runner itself)
        var hook = System.Text.RegularExpressions.Regex.Match(message, @"^\[\w+ HOOK\] \[([^\]]+)\]");
        var from = hook.Success ? hook.Groups[1].Value : frames.Select(Frame).FirstOrDefault(f => f.Contains(".") && !f.StartsWith("System.")
            && !f.StartsWith("UnityEngine.") && !f.StartsWith("UnboundLib.GameModes") && !f.StartsWith("UnboundLib.ExtensionMethods")
            && !f.StartsWith("Photon.") && !f.StartsWith("ExitGames.")
            && !f.StartsWith("Rethrow")) ?? "?";
        // a Harmony-patched method shows as "(wrapper dynamic-method) Type.DMD<Type::Method>": the error is in that method
        // or in a mod's patch on it
        var patched = from.EndsWith(" (patched)");
        var key = from == "?" ? "after picking " + current : from.Split('.')[0].Split('+')[0].Replace(" (patched)", "") + (patched ? " (patched)" : "");
        if (!errors.TryGetValue(key, out var list)) errors[key] = list = new List<string>();
        var line = (message.Split('\n')[0] + " | " + first + " | last pick: " + current).Replace('\t', ' ').Replace('\r', ' ');
        if (list.Count < 5 && !list.Contains(line)) list.Add(line);
        if (shotFor.Add(current)) StartCoroutine(Shot("error_" + current));
        // an area effect that finds a player-layer collider with no Player above it (Arcana's Crimson Aura): what is it?
        if (!orphansLogged && first.Contains("ApplyEffectInArea"))
        {
            orphansLogged = true;
            var layer = LayerMask.NameToLayer("Player");
            var orphans = FindObjectsOfType<Collider2D>().Where(c => c.gameObject.layer == layer && c.GetComponentInParent<Player>() == null)
                .Select(c => { var t = c.transform; var p = t.name; while (t.parent != null) { t = t.parent; p = t.name + "/" + p; } return p + "[" + c.GetType().Name + "]"; });
            Logger.LogInfo("pilot: player-layer colliders with no Player: " + string.Join(", ", orphans.Take(20)));
        }
    }
    bool orphansLogged;

    // The game's AI picks random hiding spots until one is more than 13 units from the other player, with no limit: on a
    // map with no such spot it spins forever and freezes the game (and throws with no spots at all). Only the test's AI
    // players run it, so cap it here: a random spot far enough away, else the farthest one.
    [HarmonyPatch(typeof(PlayerAIZorro), nameof(PlayerAIZorro.GetPosAwayFrom))]
    static class AiHidingSpot_Cap
    {
        static bool Prefix(Vector3 point, Vector3[] ___surfacesToHideOn, ref Vector3 __result)
        {
            if (___surfacesToHideOn == null || ___surfacesToHideOn.Length == 0) { __result = point; return false; }
            var far = ___surfacesToHideOn.Where(s => Mathf.Abs(s.x - point.x) > 13f).ToArray();
            __result = far.Length > 0 ? far[UnityEngine.Random.Range(0, far.Length)]
                : ___surfacesToHideOn.OrderByDescending(s => Mathf.Abs(s.x - point.x)).First();
            return false;
        }
    }

    // Unity logs "Coroutine couldn't be started because the game object 'X' is inactive" with no stack; this records who did it
    [HarmonyPatch(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), new[] { typeof(IEnumerator) })]
    static class StartCoroutine_Inactive
    {
        static void Prefix(MonoBehaviour __instance)
        {
            if (me == null || me.stopped || __instance == null || __instance.gameObject.activeInHierarchy) return;
            me.Record($"Coroutine started on inactive '{__instance.gameObject.name}' ({__instance.GetType().Name})",
                new System.Diagnostics.StackTrace(2, false).ToString());
        }
    }

    IEnumerator Shot(string name)
    {
        yield return new WaitForEndOfFrame();
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '-').ToArray());
        if (safe.Length > 60) safe = safe.Substring(0, 60);
        ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "shots", $"{shot++:000}_{safe}.png"));
    }

    IEnumerator Run()
    {
        while (MainMenuHandler.instance == null || CardChoice.instance == null) yield return new WaitForSecondsRealtime(1);
        yield return new WaitForSecondsRealtime(8);   // mods finish registering cards
        current = "(menu)";
        yield return Shot("menu");
        // vanilla cards old mods load by path (Cosmic Rounds: Drone, Careen, Flamethrower...): which still resolve
        Logger.LogInfo("pilot: Resources paths: " + string.Join(", ", new[] { "Demonic pact", "Empower", "Explosive bullet", "Homing", "Mayhem" }
            .Select(n => $"{n}={(Resources.Load("0 cards/" + n) != null ? "ok" : "MISSING")}"))
            + "; cards named like Homing: " + string.Join(", ", CardChoice.instance.cards.Where(c => c != null && c.name.IndexOf("oming", StringComparison.OrdinalIgnoreCase) >= 0).Select(c => c.name)));
        // a step that throws ends its coroutine, so run it apart and stop waiting if the pilot itself fails
        bool finished = false;
        var body = mode == "match" ? Match() : mode == "host" || mode == "join" ? Online() : null;
        if (body != null) StartCoroutine(Then(body, () => finished = true));
        else finished = true;
        while (!finished && !pilotFailed) yield return new WaitForSecondsRealtime(1);
        current = "(end)";
        yield return Shot("end");
        Write();
        File.WriteAllText(Path.Combine(outDir, "done"), DateTime.Now.ToString("s"));
        yield return new WaitForSecondsRealtime(2);
        Application.Quit();
    }

    IEnumerator Then(IEnumerator body, Action done) { yield return body; done(); }

    int total;

    void Enqueue(int part, int parts)
    {
        var cards = CardChoice.instance.cards.Where(c => c != null).ToList();
        for (int i = part; i < cards.Count; i += parts) queue.Enqueue(cards[i]);
        total = queue.Count;
    }

    // the first of the preferred game modes that exists, set so every point ends in a pick phase
    object SetMode(params string[] prefer)
    {
        var gmm = FindType("UnboundLib.GameModes.GameModeManager", false);
        if (gmm == null)
        {
            // no UnboundLib (the vanilla baseline): the game's own Arms Race (offline it waits for StartGame)
            var ar = Resources.FindObjectsOfTypeAll<GM_ArmsRace>().First(x => x.gameObject.scene.IsValid());
            ar.roundsToWinGame = 999;
            ar.gameObject.SetActive(true);
            Logger.LogInfo("pilot: no UnboundLib: the game's own Arms Race");
            return ar;
        }
        var ids = ((IDictionary)gmm.GetField("handlers", Any).GetValue(null)).Keys.Cast<string>().ToList();
        var id = prefer.FirstOrDefault(ids.Contains);
        Logger.LogInfo($"pilot: game modes: {string.Join(", ", ids)}; using {id}");
        if (id == null) throw new Exception($"pilot: none of {string.Join(", ", prefer)} (have {string.Join(", ", ids)})");
        gmm.GetMethod("SetGameMode", Any, null, new[] { typeof(string) }, null).Invoke(null, new object[] { id });
        var handler = gmm.GetProperty("CurrentHandler", Any).GetValue(null, null);
        var change = handler.GetType().GetMethod("ChangeSetting", Any);
        change.Invoke(handler, new object[] { "pointsToWinRound", 1 });
        change.Invoke(handler, new object[] { "roundsToWinGame", 999 });
        return handler;
    }

    static void Timeout(float until, string what)
    {
        if (Time.realtimeSinceStartup > until) throw new Exception("pilot: timed out " + what);
    }

    IEnumerator Match()
    {
        Enqueue(0, 1);
        // offline, as the menu's Local button does
        current = "(going offline)";
        if (PhotonNetwork.IsConnected) { PhotonNetwork.Disconnect(); while (PhotonNetwork.IsConnected) yield return null; }
        PhotonNetwork.OfflineMode = true;
        // offline this makes the local room, as SetOfflineMode.SetOffline does. Mods' OnJoinedRoom callbacks run inside it
        // and their exceptions come out here (Will's Wacky Managers); the menu button carries on past them too
        try { PhotonNetwork.JoinRandomRoom(); } catch (Exception e) { Caught(e); }
        while (!PhotonNetwork.InRoom) yield return null;

        current = "(match start)";
        MainMenuHandler.instance.Close();
        // RoundsWithFriends swaps the game modes for its own
        var handler = SetMode("Arms race", "Team Deathmatch", "Deathmatch");
        for (int i = 0; i < 2; i++)
        {
#if OLDGAME
            PlayerAssigner.instance.StartCoroutine(PlayerAssigner.instance.CreatePlayer(null, true));   // a coroutine before 2025
#else
            PlayerAssigner.instance.CreatePlayer(null, true);
#endif
            yield return new WaitForSecondsRealtime(i == 0 ? 1 : 2);
        }
        handler?.GetType().GetMethod("StartGame", Any, null, Type.EmptyTypes, null).Invoke(handler, null);
        yield return Play();
    }

    IEnumerator Online()
    {
#if OLDGAME
        throw new Exception("pilot: the old-game build only plays local matches");   // ponytail: the lobby calls below are 2025 API
#else
        bool host = mode == "host";
        var roomFile = Environment.GetEnvironmentVariable("ROUNDS_PILOT_ROOM");
        var prh = FindType("RWF.PrivateRoomHandler");
        var lobby = prh.GetField("instance", Any).GetValue(null);
        Enqueue(host ? 0 : 1, 2);
        var until = Time.realtimeSinceStartup + 180;
        current = "(lobby)";
        // the menu the way a player gets there: past the intro, then the Online page
        FindObjectOfType<SkipIntro>(true)?.Skip();
        yield return new WaitForSecondsRealtime(1);
        MainMenuHandler.instance.PlayOnlineMultiplayer();
        yield return new WaitForSecondsRealtime(2);
        if (host)
        {
            // the menu's Host button under RoundsWithFriends
            prh.GetMethod("Open", Any).Invoke(lobby, null);
            FindType("RWF.NetworkConnectionHandlerExtensions").GetMethod("HostPrivate", Any).Invoke(null, new object[] { NetworkConnectionHandler.instance });
            while (!PhotonNetwork.InRoom) { Timeout(until, "hosting a room"); yield return null; }
            yield return new WaitForSecondsRealtime(1);   // after RWF's OnJoinedRoom sets its default mode
            SetMode("Deathmatch", "Arms race");   // free-for-all: the two characters always get different colours, so the lobby can start
            // the room code, and LobbyImprovements' lobby code when it's installed (what a player pastes to join)
            var li = FindType("LobbyImprovements.Networking.LobbyCodeHandler", false);
            var liCode = li == null ? "" : (string)li.GetMethod("GetCode", Any).Invoke(null, null);
            File.WriteAllText(roomFile, (string)PhotonNetwork.CurrentRoom.CustomProperties[NetworkConnectionHandler.ROOM_CODE] + "\n" + liCode);
        }
        else
        {
            string code;
            while ((code = File.Exists(roomFile) ? File.ReadAllText(roomFile).Trim() : "") == "") { Timeout(until, "waiting for the host's room code"); yield return new WaitForSecondsRealtime(1); }
            var lines = code.Split('\n');
            code = lines[0].Trim();
            var li = FindType("LobbyImprovements.Networking.LobbyCodeHandler", false);
            if (li != null && lines.Length > 1 && lines[1].Trim() != "")
            {
                // the lobby code, as a player joins with LobbyImprovements (it carries the host's region too)
                var result = li.GetMethod("ConnectToRoom", Any).Invoke(null, new object[] { lines[1].Trim() });
                Logger.LogInfo($"pilot: LobbyImprovements lobby code {lines[1].Trim()}: {result}");
            }
            else NetworkConnectionHandler.instance.JoinRoom(code);
            while (!PhotonNetwork.InRoom) { Timeout(until, "joining room " + code); yield return null; }
        }
        Logger.LogInfo($"pilot: in room {PhotonNetwork.CurrentRoom.Name} as {mode}");
        yield return new WaitForSecondsRealtime(2);
        yield return Shot("lobby");
        Logger.LogInfo("pilot: UI in the lobby: " + UiState());

        // the keyboard's JOIN/READY: the first press adds a character, the next readies it. Ready only once both copies
        // are in, so the settings sync when the client arrives can't unready anyone.
        var toggle = prh.GetMethod("ToggleReady", Any);
        var find = prh.GetMethod("FindLobbyCharacter", Any, null, new[] { typeof(int), typeof(int) }, null);
        while (PlayerManager.instance.players.Count < 2)
        {
            Timeout(until, $"starting from the lobby ({PhotonNetwork.CurrentRoom?.PlayerCount} in the room)");
            var c = find.Invoke(lobby, new object[] { PhotonNetwork.LocalPlayer.ActorNumber, 0 });
            bool ready = c != null && (bool)c.GetType().GetField("ready").GetValue(c);
            if (c == null || (!ready && PhotonNetwork.CurrentRoom.PlayerCount > 1))
                StartCoroutine((IEnumerator)toggle.Invoke(lobby, new object[] { null, false }));
            yield return new WaitForSecondsRealtime(2);
        }
        // the characters are keyboard players; give this copy's the AI brain, as PlayerAssigner.CreatePlayer does for AI
        foreach (var p in PlayerManager.instance.players.Where(p => p.data.view.IsMine))
        {
            p.data.SetAI();
            Instantiate(PlayerAssigner.instance.player1AI, p.transform.position, p.transform.rotation, p.transform);
        }
        current = "(match start)";
        yield return Play();
#endif
    }

    // which menu objects are showing: the RWF lobby's place in the menu tree, the main menu's first child (what
    // MainMenuHandler.Close hides) and the menu page stack
    static string UiState()
    {
        string Path(Transform t) { var s = t.name; while ((t = t.parent) != null) s = t.name + "/" + s; return s; }
        var parts = new List<string>();
        var prh = FindType("RWF.PrivateRoomHandler", false)?.GetField("instance", Any)?.GetValue(null) as Component;
        if (prh != null) parts.Add($"lobby {Path(prh.transform)} active={prh.gameObject.activeInHierarchy}");
        var child0 = MainMenuHandler.instance.transform.GetChild(0);
        parts.Add($"main menu child 0 {Path(child0)} active={child0.gameObject.activeSelf}");
        var stack = typeof(ListMenu).GetField("m_menuStack", Any)?.GetValue(ListMenu.instance) as IEnumerable;
        if (stack != null) parts.Add("pages " + string.Join(" < ", stack.Cast<Component>().Select(p => $"{p.name}({(p.gameObject.activeInHierarchy ? "shown" : "hidden")})")));
        foreach (var c in FindObjectsOfType<Canvas>().Where(c => c.isRootCanvas && c.gameObject.activeInHierarchy))
            parts.Add($"canvas {Path(c.transform)} order={c.sortingOrder} layer={c.sortingLayerName}");
        return string.Join("; ", parts);
    }

    // a card reference as the game holds it: null, a destroyed object, or a live one
    static string Describe(CardInfo c) => ReferenceEquals(c, null) ? "(null)" : !c ? $"(destroyed #{c.GetInstanceID()})" : c.gameObject.name;

    static Player PlayerById(int id) => PlayerManager.instance.players.FirstOrDefault(p => p != null && Id(p) == id);

    // the members the 2025 game renamed; OLDGAME builds against the old-rounds-for-mods beta (bin/old)
#if OLDGAME
    static int Id(Player p) => p.playerID;
    static float MaxHp(CharacterData d) => d.maxHealth;
#else
    static int Id(Player p) => p.PlayerID;
    static float MaxHp(CharacterData d) => d.MaxHealth;
#endif

    IEnumerator Play()
    {
        var minutes = float.TryParse(Environment.GetEnvironmentVariable("ROUNDS_PILOT_MINUTES"), out var m) ? m : 20f;
        var deadline = Time.realtimeSinceStartup + minutes * 60;
        // ROUNDS_PILOT_POINT: seconds of fighting before the host ends a point (default 12: every card gets shot with)
        var pointSeconds = float.TryParse(Environment.GetEnvironmentVariable("ROUNDS_PILOT_POINT"), out var ps) ? ps : 12f;
        bool online = !PhotonNetwork.OfflineMode;
        Logger.LogInfo($"pilot: {(online ? "online" : "local")} match with {PlayerManager.instance.players.Count} AI players, {total} cards to pick, {minutes} min");
        // mods colour things with Player.GetTeamColors(): the skin each player has
        Logger.LogInfo("pilot: skins: " + string.Join(", ", PlayerManager.instance.players.Where(p => p != null).Select(p =>
        { try { return $"player {Id(p)}: " + (p.GetTeamColors() == null ? "null" : "ok"); } catch (Exception e) { return $"player {Id(p)}: {e.GetType().Name}"; } })));
        float pointStart = Time.realtimeSinceStartup, lastShot = 0;
        bool wasBattle = false;
        int battles = 0;
        // online, both copies play to the deadline so neither leaves mid-match
        while (Time.realtimeSinceStartup < deadline && (online || queue.Count > 0 || CardChoice.instance.IsPicking))
        {
            bool battle = GameManager.instance.battleOngoing && !CardChoice.instance.IsPicking;
            if (battle && !wasBattle) { pointStart = Time.realtimeSinceStartup; StartCoroutine(Snapshot(++battles)); }
            wasBattle = battle;
            if (battle && Time.realtimeSinceStartup - lastShot > 15) { lastShot = Time.realtimeSinceStartup; yield return Shot("battle_" + current); }
            // AI pickers have an input object with no buttons, so the game's "no controller: take the first card" never
            // runs and the offer waits forever. Do what it would do, once the offer has settled. Only the copy that owns
            // the picker can pick.
            var offered = (List<GameObject>)typeof(CardChoice).GetField("spawnedCards", Any).GetValue(CardChoice.instance);
            var picker = PlayerById(CardChoice.instance.pickrID);
            if (CardChoice.instance.IsPicking && picker != null && picker.data.view.IsMine && offered != null && offered.Count > 0 && offered[0] != null)
            {
                yield return new WaitForSecondsRealtime(2f);   // cards finish flipping face up
                if (CardChoice.instance.IsPicking && CardChoice.instance.pickrID != -1 && offered.Count > 0 && offered[0] != null)
                {
                    if (!layoutLogged) { layoutLogged = true; Logger.LogInfo("pilot: card layout: " + Layout(offered[0].transform, "")); }
                    // a mod's pick hook can throw; log it with its stack, and next time take another card, as a player
                    // would (the game only clears the picker after a pick that worked)
                    var card = offered[failedPicks % offered.Count] ?? offered[0];
                    // selected, as a player's choice is, so the shot shows the card's face
                    try { PressPick(picker, offered.IndexOf(card), press: false); } catch (Exception e) { Caught(e); }
                    yield return new WaitForSecondsRealtime(1f);
                    yield return Shot("offer_" + card.name.Replace("(Clone)", ""));
                    // the picker is the point's loser: is it dead (inactive) while it picks? Compared between game builds
                    Logger.LogInfo($"pilot: picking {card.name.Replace("(Clone)", "")} as player {Id(picker)}: active {picker.gameObject.activeInHierarchy}, dead {picker.data.dead}");
                    int before = picked.Count;
                    bool threw = false;
                    try { PressPick(picker, offered.IndexOf(card)); }
                    catch (Exception e)
                    {
                        Caught(e); failedPicks++; threw = true;
                        // mods find their player with GetComponentInParent<Player>(), which skips inactive objects
                        Logger.LogInfo($"pilot: picker {Id(picker)}: active {picker.gameObject.activeInHierarchy}, dead {picker.data.dead}, " +
                                       $"GetComponentInParent<Player> {(picker.gameObject.GetComponentInParent<Player>() != null ? "found" : "null")}");
                    }
                    if (picked.Count == before && CardChoice.instance.pickrID != -1 && !threw)
                    {
                        // the press didn't reach the pick (a mod's DoPlayerSelect prefix, or controls that ignore input)
                        Logger.LogWarning("pilot: Jump didn't pick; picking directly, as before (skips mods' DoPlayerSelect patches)");
                        try { CardChoice.instance.Pick(card); CardChoice.instance.pickrID = -1; } catch (Exception e) { Caught(e); failedPicks++; threw = true; }
                    }
                    if (!threw) failedPicks = 0;   // another card only within the offer that threw
                    yield return new WaitForSecondsRealtime(1);
                }
            }
            // a point the AIs can't finish (both too tanky, or stuck) ends after pointSeconds; online the host decides
            if (battle && PhotonNetwork.IsMasterClient && Time.realtimeSinceStartup - pointStart > pointSeconds)
            {
                var alive = PlayerManager.instance.players.Where(p => p != null && !p.data.dead).ToList();
                if (alive.Count > 1)
                {
                    var loser = alive.OrderBy(p => p.data.health).First();
                    loser.data.view.RPC("RPCA_Die", RpcTarget.All, Vector2.up);
                }
                pointStart = Time.realtimeSinceStartup;
            }
            yield return new WaitForSecondsRealtime(0.5f);
        }
        stopped = true;   // errors from here on are the game shutting down
        Logger.LogInfo($"pilot: picked {picked.Count} of {total} cards");
    }

    static readonly FieldInfo thisState = AccessTools.Field(typeof(InControl.OneAxisInputControl), "thisState");
    static readonly FieldInfo lastState = AccessTools.Field(typeof(InControl.OneAxisInputControl), "lastState");

    // A player's pick as the game takes it: Jump pressed on the picker's controls while a card is selected, then the
    // game's DoPlayerSelect with every mod's patches on it (MorePicksPatch keeps the picker id set past the pick there;
    // calling Pick directly skipped that). The AI's controls never press anything by themselves.
    // AI players have no controls (playerActions null), so they get a bare set, no bindings, for the press only.
    // press false: only move the selection there, which turns that card face up (what a player sees while choosing).
    static void PressPick(Player picker, int index, bool press = true)
    {
        var own = picker.data.playerActions;
        if (own == null) picker.data.playerActions = blankControls ?? (blankControls = new PlayerActions());
        var jump = picker.data.playerActions.Jump;
        AccessTools.Field(typeof(CardChoice), "currentlySelectedCard").SetValue(CardChoice.instance, Math.Max(0, index));
        thisState.SetValue(jump, new InControl.InputControlState { State = press });
        lastState.SetValue(jump, new InControl.InputControlState());
        try { AccessTools.Method(typeof(CardChoice), "DoPlayerSelect").Invoke(CardChoice.instance, null); }
        finally { thisState.SetValue(jump, new InControl.InputControlState()); picker.data.playerActions = own; }
    }
    static PlayerActions blankControls;

    // a screen-covering object: each named root object's renderers, then a screenshot with each one switched off.
    // Every camera's render settings and components are logged too.
    IEnumerator Probe(string[] names)
    {
        foreach (var c in Camera.allCameras)
            Logger.LogInfo($"pilot: probe camera {c.name}: hdr {c.allowHDR} msaa {c.allowMSAA} path {c.renderingPath}/{c.actualRenderingPath} rt {c.forceIntoRenderTexture} depthtex {c.depthTextureMode} buffers {c.commandBufferCount} stereo {c.stereoTargetEye} display {c.targetDisplay} rect {c.rect} finalblit {c.GetComponents<Behaviour>().Where(x => x.GetType().Name == "PostProcessLayer").Select(x => x.GetType().GetField("finalBlitToCameraTarget")?.GetValue(x)).FirstOrDefault()} components "
                + string.Join(", ", c.GetComponents<Component>().Select(x => x.GetType().FullName)));
        // "try:<camera>.<setting>=<value>+...": a screenshot with those changed together, then put back
        foreach (var name in names.Where(n => n.StartsWith("try:")))
        {
            var undo = new List<Action>();
            foreach (var change in name.Substring(4).Split('+'))
            {
                var parts = change.Split('=', '.');   // camera.setting=value (camera names have no dots)
                var cam = Camera.allCameras.FirstOrDefault(c => c.name == parts[0]);
                if (cam == null) { Logger.LogInfo($"pilot: probe {change}: no camera"); continue; }
                var v = parts[2];
                var ppl = cam.GetComponents<Behaviour>().FirstOrDefault(b => b.GetType().Name == "PostProcessLayer");
                switch (parts[1])
                {
                    case "enabled": { var o = cam.enabled; cam.enabled = bool.Parse(v); undo.Add(() => cam.enabled = o); break; }
                    case "hdr": { var o = cam.allowHDR; cam.allowHDR = bool.Parse(v); undo.Add(() => cam.allowHDR = o); break; }
                    case "msaa": { var o = cam.allowMSAA; cam.allowMSAA = bool.Parse(v); undo.Add(() => cam.allowMSAA = o); break; }
                    case "rt": { var o = cam.forceIntoRenderTexture; cam.forceIntoRenderTexture = bool.Parse(v); undo.Add(() => cam.forceIntoRenderTexture = o); break; }
                    case "clear": { var o = cam.clearFlags; cam.clearFlags = (CameraClearFlags)Enum.Parse(typeof(CameraClearFlags), v); undo.Add(() => cam.clearFlags = o); break; }
                    case "depth": { var o = cam.depth; cam.depth = float.Parse(v); undo.Add(() => cam.depth = o); break; }
                    case "ppl": if (ppl) { var o = ppl.enabled; ppl.enabled = bool.Parse(v); undo.Add(() => ppl.enabled = o); } break;
                    case "finalblit": if (ppl) { var f = ppl.GetType().GetField("finalBlitToCameraTarget"); var o = f.GetValue(ppl); f.SetValue(ppl, bool.Parse(v)); undo.Add(() => f.SetValue(ppl, o)); } break;
                }
            }
            yield return Shot("probe_" + name.Substring(4));
            yield return null; yield return null;
            foreach (var u in undo) u();
            yield return null;
        }
        foreach (var name in names.Where(n => !n.StartsWith("try:")))
        {
            var go = GameObject.Find(name);
            if (go == null) { Logger.LogInfo($"pilot: probe {name}: not found"); continue; }
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                var what = c is Renderer r ? $"{r.GetType().Name} on {r.enabled} layer {r.gameObject.layer} sort {r.sortingLayerName}/{r.sortingOrder} shader {(r.sharedMaterial ? r.sharedMaterial.shader.name : "-")} bounds {r.bounds.size}"
                    + (r is SpriteMask m ? $" custom {m.isCustomRangeActive} {m.backSortingOrder}..{m.frontSortingOrder} sprite {(m.sprite ? m.sprite.name : "-")}" : "")
                    + (r is SpriteRenderer s ? $" mask {s.maskInteraction} color {s.color}" : "")
                    + (r is ParticleSystemRenderer p ? $" mask {p.maskInteraction}" : "")
                    : c is Canvas cv ? $"Canvas {cv.renderMode} sort {cv.sortingOrder} camera {(cv.worldCamera ? cv.worldCamera.name : "-")}"
                    : c is UnityEngine.UI.Graphic g ? $"{g.GetType().Name} on {g.enabled} color {g.color} shader {(g.material ? g.material.shader.name : "-")} rect {g.rectTransform.rect.size} scale {g.transform.lossyScale}"
                    : null;
                if (what != null) Logger.LogInfo($"pilot: probe {name}/{c.gameObject.name} (active {c.gameObject.activeInHierarchy}): {what}");
            }
            go.SetActive(false);
            yield return Shot("probe_without_" + name);
            yield return null; yield return null;   // the capture is taken at the end of a later frame
            go.SetActive(true);
        }
    }

    // every player's cards and main stats as this copy sees them, shortly into a battle (card stats arrive by RPC)
    IEnumerator Snapshot(int battle)
    {
        yield return new WaitForSecondsRealtime(1);
        if (battle == 1) Logger.LogInfo("pilot: UI at the first battle: " + UiState());
        // what each camera draws over the ones below it (a mod's added camera that clears the screen hides the game)
        if (battle == 1) Logger.LogInfo("pilot: cameras: " + string.Join("; ", Camera.allCameras.OrderBy(c => c.depth).Select(c =>
            $"{c.name} depth {c.depth} clear {c.clearFlags} bg {c.backgroundColor} mask {c.cullingMask} size {c.orthographicSize:0.#} at {c.transform.position} clip {c.nearClipPlane}..{c.farClipPlane} target {(c.targetTexture ? c.targetTexture.name : "-")}")));
        if (battle == 1 && Environment.GetEnvironmentVariable("ROUNDS_PILOT_PROBE") is string probe)
            yield return Probe(probe.Split(','));
        // hits on map objects are sent as an index into the map's colliders, so both copies need the same ones
        var map = MapManager.instance.currentMap?.Map;
        if (map != null) state.Add($"{battle}\tmap\t{map.name.Replace("(Clone)", "")}\t{map.GetComponentsInChildren<Collider2D>().Length} colliders\t");
        foreach (var p in PlayerManager.instance.players.Where(p => p != null).OrderBy(p => Id(p)))
            state.Add($"{battle}\t{Id(p)}\t{string.Join(",", p.data.currentCards.Select(Describe))}\t{MaxHp(p.data):0.##}\t{p.data.weaponHandler.gun.damage:0.###}");
    }

    [HarmonyPatch(typeof(CardChoice), nameof(CardChoice.StartPick))]
    static class StartPick_Mark { static void Postfix() { if (me != null) me.offerNext = true; } }

    // runs after every other mod's SpawnUniqueCard patches: the first card of the offer becomes the next untested card
    [HarmonyPatch(typeof(CardChoice), "SpawnUniqueCard")]
    static class SpawnUniqueCard_Queue
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        static void Postfix(CardChoice __instance, Vector3 pos, Quaternion rot, ref GameObject __result)
        {
            if (me == null || !me.offerNext || me.queue.Count == 0 || me.natural) return;
            me.offerNext = false;
            var next = me.NextAllowed(__instance);
            if (next == null) return;
            var spawned = (GameObject)AccessTools.Method(typeof(CardChoice), "Spawn").Invoke(__instance, new object[] { next.gameObject, pos, rot });
            if (spawned == null) return;
            spawned.GetComponent<CardInfo>().sourceCard = next;
            if (__result != null) PhotonNetwork.Destroy(__result);
            __result = spawned;
        }
    }

    readonly List<string> notAllowed = new List<string>();
    readonly Dictionary<CardInfo, int> refused = new Dictionary<CardInfo, int>();
    readonly HashSet<string> whyLogged = new HashSet<string>();

    // which of ModdingUtils' rules turned a card down: a held card's blacklist, the player's blacklist, a mod's validator
    static string WhyNot(Type cards, object inst, Player player, CardInfo c)
    {
        var why = new List<string>();
        foreach (var held in player.data.currentCards)
            if (held != null && c.categories.Intersect(held.blacklistedCategories ?? new CardCategory[0]).Any()) why.Add("blacklisted by held " + held.gameObject.name);
        try
        {
            var ext = FindType("ModdingUtils.Extensions.CharacterStatModifiersExtension", false);
            var data = ext?.GetMethod("GetAdditionalData", Any)?.Invoke(null, new object[] { player.data.stats });
            if (data?.GetType().GetField("blacklistedCategories", Any)?.GetValue(data) is List<CardCategory> bl && c.categories.Intersect(bl).Any())
                why.Add("player blacklist: " + string.Join(",", c.categories.Intersect(bl).Select(x => x.name)));
        }
        catch (Exception e) { why.Add("player blacklist: " + e.GetType().Name); }
        if (!c.allowMultiple && player.data.currentCards.Any(h => h != null && h.gameObject.name == c.gameObject.name)) why.Add("already held");
        if (cards.GetField("cardValidationFunctions", Any)?.GetValue(inst) is List<Func<Player, CardInfo, bool>> fs)
            foreach (var f in fs)
            {
                bool ok;
                try { ok = f(player, c); } catch (Exception e) { why.Add($"validator {f.Method.DeclaringType?.FullName} threw {e.GetType().Name}"); continue; }
                if (!ok) why.Add($"validator {f.Method.DeclaringType?.FullName}.{f.Method.Name}");
            }
        if (why.Count == 0)
        {
            // then a mod's Harmony patch on PlayerIsAllowedCard said no
            var info = Harmony.GetPatchInfo(cards.GetMethod("PlayerIsAllowedCard", Any, null, new[] { typeof(Player), typeof(CardInfo) }, null));
            var patches = info == null ? new List<Patch>() : info.Prefixes.Concat(info.Postfixes).ToList();
            return "no ModdingUtils rule; patched by " + (patches.Count == 0 ? "nobody" :
                string.Join(", ", patches.Select(p => $"{p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name}")));
        }
        return string.Join("; ", why);
    }

    // the next queued card the game would offer this picker (ModdingUtils' rules: classes, blacklists, hidden cards)
    CardInfo NextAllowed(CardChoice choice)
    {
        var cards = FindType("ModdingUtils.Utils.Cards", false);
        var inst = cards?.GetField("instance", Any)?.GetValue(null);
        var allowed = cards?.GetMethod("PlayerIsAllowedCard", Any, null, new[] { typeof(Player), typeof(CardInfo) }, null);
        var player = PlayerById(choice.pickrID);
        // a refusal can be for this pick only (Pick Phase Improvements' conditional picks: "pick an Aura"), so a refused
        // card goes back in the queue; refused three times, it counts as not offered
        var later = new List<CardInfo>();
        CardInfo found = null;
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (inst == null || allowed == null || player == null || (bool)allowed.Invoke(inst, new object[] { player, c })) { found = c; break; }
            refused[c] = refused.TryGetValue(c, out var n) ? n + 1 : 1;
            if (refused[c] < 3) later.Add(c); else notAllowed.Add(c.gameObject.name);
            if (whyLogged.Count >= 40) continue;
            var why = WhyNot(cards, inst, player, c);   // one example card per reason
            if (whyLogged.Add(why)) Logger.LogInfo($"pilot: {c.gameObject.name} not allowed for player {Id(player)}: {why}");
        }
        foreach (var c in later) queue.Enqueue(c);
        return found;
    }

    [HarmonyPatch(typeof(CardChoice), nameof(CardChoice.Pick))]
    static class Pick_Record
    {
        static void Prefix(GameObject pickedCard)
        {
            if (me == null || pickedCard == null) return;
            me.picked.Add(pickedCard.name.Replace("(Clone)", "").Trim());
        }
    }

    // a card going onto a player, on every copy (Pick only runs on the picker's)
    [HarmonyPatch(typeof(ApplyCardStats), "ApplyStats")]
    static class ApplyStats_Record
    {
        static void Prefix(ApplyCardStats __instance)
        {
            if (me == null) return;
            me.current = __instance.gameObject.name.Replace("(Clone)", "").Trim();
            var src = __instance.GetComponent<CardInfo>().sourceCard;
            me.Logger.LogInfo($"pilot: applying {me.current}: source {Describe(src)}, in the card list: {CardChoice.instance.cards.Contains(src)}");
        }
    }

    readonly Dictionary<string, string> owners = new Dictionary<string, string>();

    // the mod a card comes from: the first component type that isn't the game's or Unity's
    string Owner(string cardName)
    {
        if (owners.TryGetValue(cardName, out var o)) return o;
        var card = CardChoice.instance.cards.FirstOrDefault(c => c != null && c.gameObject.name == cardName);
        o = "game";
        if (card != null)
            foreach (var c in card.GetComponents<MonoBehaviour>())
            {
                if (c == null) continue;
                var asm = c.GetType().Assembly.GetName().Name;
                if (asm != "Assembly-CSharp" && !asm.StartsWith("Unity") && !asm.StartsWith("Photon")) { o = asm; break; }
            }
        return owners[cardName] = o;
    }

    static Type FindType(string name, bool required = true) =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null)
        ?? (required ? throw new Exception("pilot: type not found: " + name) : null);

    readonly List<string> state = new List<string> { "battle\tplayer\tcards\tmaxHealth\tdamage" };

    void Write()
    {
        File.WriteAllLines(Path.Combine(outDir, "cards.tsv"), new[] { "card\tmod" }.Concat(picked.Select(n => n + "\t" + Owner(n)))
            .Concat(notAllowed.Select(n => n + "\t(not offered: the game wouldn't allow it)")));
        File.WriteAllLines(Path.Combine(outDir, "state.tsv"), state);
        File.WriteAllLines(Path.Combine(outDir, "errors.tsv"),
            new[] { "context\terror" }.Concat(errors.SelectMany(kv => kv.Value.Select(v => kv.Key + "\t" + v))));
    }

    // a mod stuck in a loop freezes the game with nothing logged. If the main thread stops for 15 s, record its stack and
    // the last card applied, then end the run (the launcher kills the frozen game)
    [System.Runtime.InteropServices.DllImport("mono-2.0-bdwgc.dll")] static extern void mono_threads_request_thread_dump();
    long beat = DateTime.UtcNow.Ticks;
    void Update() => System.Threading.Interlocked.Exchange(ref beat, DateTime.UtcNow.Ticks);

    void Watchdog()
    {
        while (!File.Exists(Path.Combine(outDir, "done")))
        {
            System.Threading.Thread.Sleep(2000);
            if (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref beat)).TotalSeconds < 15) continue;
            // Mono can't read another thread's stack from managed code, but its runtime can: the finalizer thread suspends
            // every thread and prints their stacks to stdout (the launcher saves it as run/stdout.log)
            string where = "every thread's stack is in run/stdout.log";
            try { mono_threads_request_thread_dump(); System.Threading.Thread.Sleep(3000); }
            catch (Exception e) { where = "(no thread dump: " + e.Message + ")"; }
            File.WriteAllText(Path.Combine(outDir, "hang.txt"), $"the game froze; last card applied: {current}\n{where}");
            errors["FROZE"] = new List<string> { ("game froze after " + current + " | " + where.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) + " | last pick: " + current).Replace('\t', ' ') };
            try { Write(); } catch { }
            File.WriteAllText(Path.Combine(outDir, "done"), "froze");
        }
    }
}
