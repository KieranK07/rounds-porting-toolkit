using Mono.Cecil;

// What changed in the 2025 ROUNDS build (Unity 2022.3), and whether `fix` handles it. Source: docs/MAPPING.md.
static class Known
{
    static readonly string[] DamageMethods = { "CallTakeDamage", "TakeDamage", "DoDamage", "TakeDamageOverTime" };

    public static Issue Type(TypeReference t, string scope, string why)
    {
        var name = t.FullName;
        if (scope == "Assembly-CSharp-firstpass" && t.Namespace == "Steamworks")
            return new(Fix.Auto, "type", $"[{scope}] {name}", "Steamworks moved to com.rlabrecque.steamworks.net (same types and members)");
        if (scope == "UnityEngine.CoreModule" && name == "UnityEngine.Input")
            return new(Fix.Auto, "type", $"[{scope}] {name}", "UnityEngine.Input lives in UnityEngine.InputLegacyModule in Unity 2022");
        if (scope.StartsWith("Sirenix."))
            return new(Fix.Manual, "type", $"[{scope}] {name}",
                "the game no longer ships Odin Serializer. Ship it with your mod (open-source: github.com/TeamSirenix/odin-serializer)");
        if (scope == "UnboundLib" || scope == "MMHOOK_Assembly-CSharp")
            return new(Fix.Manual, "type", $"[{scope}] {name}", why + ". UnboundLib 4 (Bknibb's port) changed some APIs; check github.com/Bknibb/UnboundLib");
        return new(Fix.Manual, "type", $"[{scope}] {name}", why);
    }

    public static Issue Member(MemberReference m, TypeDefinition dt, string why)
    {
        var type = dt.FullName; var name = m.Name;
        var what = $"{(m is FieldReference ? "field" : "method")} {type}::{name}";
        if (m is FieldReference)
        {
            switch (type, name)
            {
                case ("Player", "playerID"): return new(Fix.Auto, "field", what, "now the PlayerID property (reads) and SetPlayerID() (writes)");
                case ("Player", "teamID"): return new(Fix.Auto, "field", what, "now the TeamID property; writes go to the private m_teamID (AssignTeamID also syncs Photon, so fix avoids it)");
                case ("CharacterData", "maxHealth"): return new(Fix.Auto, "field", what, "now the MaxHealth property; writes go to m_maxHealth because the setter can unlock an achievement");
                case ("Optionshandler", "vol_Master" or "vol_Sfx"): return new(Fix.Review, "field", what, "the volume statics were removed; fix reads the options slider (0..1) instead");
                case ("Optionshandler", _): return new(Fix.Manual, "field", what, "Optionshandler statics were replaced by OptionsData settings (m_key, CurrentValueSliderNormalized)");
                case ("CardBar", "ci"): return new(Fix.Manual, "field", what, "removed: CardBar no longer caches cards. See docs/MAPPING.md section 4");
                case ("CardBar", "source"): return new(Fix.Manual, "field", what, "renamed m_source");
                case ("CardBarButton", "card"): return new(Fix.Manual, "field", what, "renamed to the public m_cardInfo");
            }
        }
        else if (m is MethodReference)
        {
            if (type is "Damagable" or "HealthHandler" or "DamageOverTime" && DamageMethods.Contains(name))
                return new(Fix.Auto, "method", what, "gained a trailing HealthHandler.DamageSource parameter; fix passes DamageSource.Player");
            if (type == "ObjectsToSpawn" && name == "SpawnObject")
                return new(Fix.Manual, "method", what, "now returns FriendlyFoe.PoolableWrapper[] (pooled; entries can be null, use .Instance). Don't Destroy() pooled objects");
            if (type == "CardBar" && name == "OnHover")
                return new(Fix.Manual, "method", what, "OnHover(CardInfo, Vector3) is gone; hover now takes a CardBarButton. See docs/MAPPING.md section 4");
            if (type == "PlayerManager" && name == "AddPlayerDiedAction")
                return new(Fix.Auto, "method", what, "removed; PlayerDiedAction is a public field now. fix adds your handler to it");
            if (type == "CardChoice" && name == "GetRanomCard")
                return new(Fix.Auto, "method", what, "the typo was fixed: GetRandomCard");
        }
        return new(Fix.Manual, m is FieldReference ? "field" : "method", what, why);
    }

    public static Issue HarmonyTarget(HarmonyInfo h, string problem)
    {
        var what = $"[HarmonyPatch] {h}";
        var type = h.Type?.FullName ?? h.TypeName;
        switch (type, h.Method)
        {
            case ("CardChoice", "GetRanomCard"): return new(Fix.Auto, "harmony", what, "the typo was fixed: GetRandomCard");
            case ("TrickShot", "Awake"): return new(Fix.Manual, "harmony", what, "TrickShot has no Awake now; its setup moved to Start, and trail is an IScaleTrailFromDamage");
            case ("ChangeColor", "Start"): return new(Fix.Manual, "harmony", what, "ChangeColor is now an empty marker component; drop this patch");
            case ("CardBar", "OnHover"): return new(Fix.Manual, "harmony", what, problem + ". The usual fix: argumentTypes new[] { typeof(CardBarButton) }, and ___currentCard -> ___m_currentCard");
        }
        return new(Fix.Manual, "harmony", what, problem);
    }
}
