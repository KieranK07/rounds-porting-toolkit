using Mono.Cecil;

// What changed in the 2025 ROUNDS build (Unity 2022.3), and whether `fix` handles it. Source: docs/MAPPING.md.
static class Known
{
    static readonly string[] DamageMethods = { "CallTakeDamage", "TakeDamage", "DoDamage", "TakeDamageOverTime", "DoDamageOverTime", "RPCA_SendTakeDamage" };

    public static Issue Type(TypeReference t, string scope, string why, string? movedTo = null)
    {
        var name = t.FullName;
        if (movedTo != null)
            return new(Fix.Auto, "type", $"[{scope}] {name}", $"Unity 2022 split {scope} up; the type is in {movedTo} now, and fix points the reference there");
        if (scope == "Assembly-CSharp-firstpass" && t.Namespace == "Steamworks")
            return new(Fix.Auto, "type", $"[{scope}] {name}", "Steamworks moved to com.rlabrecque.steamworks.net (same types and members)");
        if (scope == "UnityEngine.CoreModule" && name == "UnityEngine.Input")
            return new(Fix.Auto, "type", $"[{scope}] {name}", "UnityEngine.Input lives in UnityEngine.InputLegacyModule in Unity 2022");
        if (scope == "Assembly-CSharp" && name == "Debug")
            return new(Fix.Auto, "type", $"[{scope}] {name}", "the old game's own Debug class is gone; fix calls UnityEngine.Debug (same Log, LogError, LogWarning, DrawLine)");
        if (scope.StartsWith("Sirenix."))
            return new(Fix.Manual, "type", $"[{scope}] {name}",
                "the game no longer ships Odin Serializer. Ship it with your mod (open-source: github.com/TeamSirenix/odin-serializer)");
        if (scope == "RoundsWithFriends" && name is "RWF.UI.PlayerSpotlight" or "RWF.UI.FollowPlayer")
            return new(Fix.Manual, "type", $"[{scope}] {name}",
                "RoundsWithFriends 3 (Bknibb's port) dropped the player spotlight (the darkened screen with a light on each player between rounds) and has no replacement. Remove the calls; the game mode then runs like RWF 3's own modes, without that effect");
        if (scope == "UnboundLib" || scope == "MMHOOK_Assembly-CSharp")
            return new(Fix.Manual, "type", $"[{scope}] {name}", why + ". UnboundLib 4 (Bknibb's port) changed some APIs; check github.com/Bknibb/UnboundLib");
        return new(Fix.Manual, "type", $"[{scope}] {name}", why);
    }

    public static Issue Member(MemberReference m, TypeDefinition dt, string why, bool resultDropped = false)
    {
        var type = dt.FullName; var name = m.Name;
        var what = $"{(m is FieldReference ? "field" : "method")} {type}::{name}";
        if (m is FieldReference fr)
        {
            if (type == "UnityEngine.UIVertex" && name.StartsWith("uv") && fr.FieldType.FullName == "UnityEngine.Vector2")
                return new(Fix.Auto, "field", what, "a Vector2 in Unity 2018, a Vector4 in Unity 2022; fix reads and writes it through Vector4's conversions (x, y kept)");
            switch (type, name)
            {
                case ("Player", "playerID"): return new(Fix.Auto, "field", what, "now the PlayerID property (reads) and SetPlayerID() (writes)");
                case ("Player", "teamID"): return new(Fix.Auto, "field", what, "now the TeamID property; writes go to the private m_teamID (AssignTeamID also syncs Photon, so fix avoids it)");
                case ("CharacterData", "maxHealth"): return new(Fix.Auto, "field", what, "now the MaxHealth property; writes go to m_maxHealth because the setter can unlock an achievement");
                case ("Optionshandler", "vol_Master" or "vol_Sfx"): return new(Fix.Review, "field", what, "the volume statics were removed; fix reads the options slider (0..1) instead");
                case ("Optionshandler", "lockMouse" or "lockStick"): return new(Fix.Auto, "field", what, $"removed; the game reads the option {(name == "lockMouse" ? "OPTION_MOUSE_AIM8DIR" : "OPTION_CONTROLLER_AIM8DIR")} (aim in 8 directions) instead, and fix reads the same");
                case ("Optionshandler", _): return new(Fix.Manual, "field", what, "Optionshandler statics were replaced by OptionsData settings (m_key, CurrentValueSliderNormalized)");
                case ("CardBar", "ci"): return new(Fix.Manual, "field", what, "removed: CardBar no longer caches cards. See docs/MAPPING.md section 4");
                case ("CardBar", "source"): return new(Fix.Manual, "field", what, "renamed m_source");
                case ("CardBarButton", "card"): return new(Fix.Auto, "field", what, "renamed to the public m_cardInfo (same type); fix uses it");
                case ("Photon.Realtime.RoomOptions", "MaxPlayers"): return new(Fix.Auto, "field", what, "a byte in the old Photon, an int now; fix uses the int field");
            }
        }
        else if (m is MethodReference mref)
        {
            if (type == "UnboundLib.Unbound" && name == "RegisterMaps")
                return new(Fix.Auto, "method", what, mref.Parameters.Count == 2
                    ? "UnboundLib 3's obsolete forwarder, gone in UnboundLib 4. It ignored categoryName and called LevelManager.RegisterMaps(paths, \"Modded\"); fix does the same"
                    : "UnboundLib 3's obsolete forwarder, gone in UnboundLib 4; fix calls LevelManager.RegisterMaps(..., \"Modded\") as it did");
            if (type == "RWF.NetworkConnectionHandlerExtensions" && name is "IsSearchingQuickMatch" or "SetSearchingQuickMatch" or "SetSearchingTwitch")
                return new(Fix.Manual, "method", what, "RoundsWithFriends 3 removed it: the game keeps one m_searchingType now. Use GetSearchingType() / SetSearchingType(NetworkConnectionHandlerExtensions.SearchingType...)");
            if (type is "Damagable" or "HealthHandler" or "DamageOverTime" && DamageMethods.Contains(name))
                return new(Fix.Auto, "method", what, "gained a trailing HealthHandler.DamageSource parameter; fix passes DamageSource.Player");
            if (type == "ObjectsToSpawn" && name == "SpawnObject")
                return resultDropped
                    ? new(Fix.Auto, "method", what, "now returns FriendlyFoe.PoolableWrapper[] (pooled objects). The mod drops the result, so fix calls the new one")
                    : new(Fix.Manual, "method", what, "now returns FriendlyFoe.PoolableWrapper[] (pooled; entries can be null, use .Instance). Don't Destroy() pooled objects");
            if (type == "TMPro.TMP_FontAsset" && name == "HasCharacter")
                return new(Fix.Auto, "method", what, "gained a tryAddCharacter parameter; fix passes false, so it only looks, as before");
            if (type == "CardBar" && name == "OnHover")
                return new(Fix.Manual, "method", what, "OnHover(CardInfo, Vector3) is gone; hover now takes a CardBarButton. See docs/MAPPING.md section 4");
            if (type == "PlayerManager" && name == "AddPlayerDiedAction")
                return new(Fix.Auto, "method", what, "removed; PlayerDiedAction is a public field now. fix adds your handler to it");
            if (type == "UIHandler" && name is "ShowJoinGameText" or "DisplayScreenText" or "DisplayScreenTextLoop")
                return new(Fix.Review, "method", what, "takes a LocalizedString now, not a string; fix shows your text as is (untranslated) through a helper");
            if (type == "Photon.Realtime.Room" && name == "GetPlayer")
                return new(Fix.Auto, "method", what, "gained a findMaster parameter; fix passes false (the old behaviour)");
            if (type == "Photon.Realtime.Room" && name == "get_PlayerCount")
                return new(Fix.Auto, "method", what, "returns an int now, not a byte; fix converts it");
            if (type is "TMPro.TMP_Text" or "TMPro.TextMeshProUGUI" or "TMPro.TextMeshPro" && name == "ForceMeshUpdate")
                return new(Fix.Auto, "method", what, "takes (bool ignoreActiveState, bool forceTextReparsing) now; fix passes (false, false), the old behaviour");
            if (type == "CardChoice" && name == "GetRanomCard")
                return new(Fix.Auto, "method", what, "the typo was fixed: GetRandomCard");
        }
        return new(Fix.Manual, m is FieldReference ? "field" : "method", what, why);
    }

    // RPCs with the same parameters as on the old game: a wrong argument count there is an old bug in the mod.
    static readonly Dictionary<string, string> UnchangedRpcs = new() { ["RPCA_AddSlow"] = "(float slowToAdd, bool isFastSlow)" };

    public static string RpcNote(Scanner.RpcSite r)
    {
        var note = "";
        if (r.Targets.Count == 1 && r.Args < r.Targets[0].Parameters.Count && r.Targets[0].Parameters.Skip(r.Args).All(p => p.HasDefault || p.IsOptional))
            note += ". The missing parameters have C# defaults, but PUN matches the exact argument count and doesn't fill them in";
        if (UnchangedRpcs.TryGetValue(r.Name, out var sig))
            note += $". Not from the update: the old game's {r.Name} took {sig} too, so this call was dropped there as well. Pass every argument to make it work";
        return note;
    }

    // Ends the text of a [HarmonyPatch] whose target is gone, which fix disables.
    public const string DisablesPatch = "fix disables this patch, so PatchAll doesn't stop at it (HarmonyX throws on a missing target, and the mod's later patches wouldn't apply)";

    public static Issue HarmonyTarget(HarmonyInfo h, string problem, bool damageSource, bool ported)
    {
        var what = $"[HarmonyPatch] {h}";
        var type = h.Type?.FullName ?? h.TypeName;
        var scope = h.Type?.Scope?.Name;
        if (damageSource)
            return new(Fix.Auto, "harmony", what, "the method gained a trailing HealthHandler.DamageSource parameter; fix adds it to argumentTypes");
        if (scope == "UnityEngine.CoreModule" && type == "UnityEngine.Input" || scope == "Assembly-CSharp-firstpass" && h.Type?.Namespace == "Steamworks")
            return new(Fix.Auto, "harmony", what, $"typeof({h.Type!.Name}) still names {scope}, where the type no longer is; fix points it at its new assembly");
        if (type == "CardBar" && h.Method == "OnHover" && h.ArgTypes == null && problem.StartsWith("ambiguous"))
            return new(Fix.Review, "harmony", what, "the game has OnHover(int) and OnHover(CardBarButton) now; fix adds argumentTypes { typeof(CardBarButton) }, the hover one. Check the patch's parameters against it");
        switch (type, h.Method)
        {
            case ("CardChoice", "GetRanomCard"): return new(Fix.Auto, "harmony", what, "the typo was fixed: GetRandomCard");
            case ("TrickShot", "Awake"): return new(Fix.Review, "harmony", what, "TrickShot has no Awake now; its setup moved to Start, and trail is an IScaleTrailFromDamage. " + DisablesPatch + "; what it did is lost");
            case ("ChangeColor", "Start"): return new(Fix.Review, "harmony", what, "ChangeColor is now an empty marker component. " + DisablesPatch);
            case ("CardBar", "Update"): return new(Fix.Review, "harmony", what, "CardBar has no Update now: nothing on it runs every frame (CardBarHandler.Update only handles d-pad input). " + DisablesPatch + "; rounds-port Runtime calls it every frame for each active CardBar instead, as Update did");
        }
        if (ported && !problem.StartsWith("ambiguous"))
            return new(Fix.Review, "harmony", what, problem + ". " + DisablesPatch + "; what it did is lost");
        return new(Fix.Manual, "harmony", what, problem);
    }
}
