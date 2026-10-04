using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Rewrites a mod's IL for the 2025 build. These are the generic rewrites that ported the mods in this repo
// (docs/PATCHLOG-simple.md), plus Harmony/reflection renames. Anything it can't do safely is left alone and listed.
sealed class Fixer
{
    readonly ModuleDefinition M, Game;
    readonly Scanner scanner;
    readonly TypeDefinition HelperSrc;
    public readonly List<string> Notes = new();
    readonly SortedDictionary<string, int> counts = new();
    TypeDefinition? helper;
    readonly Dictionary<string, MethodDefinition> helperMethods = new();

    public Fixer(ModuleDefinition module, Game game, Scanner scanner)
    {
        M = module; Game = game.AssemblyCSharp; this.scanner = scanner;
        using var s = typeof(Fixer).Assembly.GetManifestResourceStream("compathelpers.dll")!;
        var ms = new MemoryStream(); s.CopyTo(ms); ms.Position = 0;
        HelperSrc = ModuleDefinition.ReadModule(ms, new ReaderParameters { AssemblyResolver = game.Resolver }).GetType("__RoundsCompat");
    }

    public IEnumerable<string> Changes => counts.Select(kv => kv.Value > 1 ? $"{kv.Key}  (x{kv.Value})" : kv.Key);
    public bool Changed => counts.Count > 0;

    public void Run()
    {
        Generic();
        RpcArgs();
        Renames();
        PluginLoad();
        SpawnObjectResults();
    }

    // Harmony patches on ObjectsToSpawn.SpawnObject that take its result as GameObject[] (LocalZoom): it returns
    // PoolableWrapper[] now, and HarmonyX refuses the patch. The parameter becomes PoolableWrapper[] and each read of it
    // goes through a helper that gives back the GameObjects inside, which is what the patch saw before.
    void SpawnObjectResults()
    {
        foreach (var md in Bodies(M).ToList())
        {
            var p = md.Parameters.FirstOrDefault(x => x.Name == "__result");
            if (p == null || !TargetsSpawnObject(md)) continue;
            bool byRef = p.ParameterType is ByReferenceType;
            var elem = byRef ? ((ByReferenceType)p.ParameterType).ElementType : p.ParameterType;
            if (elem.FullName != "UnityEngine.GameObject[]") continue;
            var wrappers = new ArrayType(M.ImportReference(GT("FriendlyFoe.PoolableWrapper")));
            var body = md.Body; var il = body.GetILProcessor();
            body.SimplifyMacros();
            var uses = body.Instructions.Where(i => i.Operand == p).ToList();
            bool readOnly = uses.All(i => i.OpCode == OpCodes.Ldarg && (!byRef || i.Next?.OpCode == OpCodes.Ldind_Ref));
            if (!readOnly)
            {
                body.OptimizeMacros();
                Notes.Add($"MANUAL {md.FullName}: writes SpawnObject's result (PoolableWrapper[] now); left as is");
                continue;
            }
            p.ParameterType = byRef ? new ByReferenceType(wrappers) : wrappers;
            foreach (var i in uses) il.InsertAfter(byRef ? i.Next : i, Instruction.Create(OpCodes.Call, Helper("PooledObjects")));
            body.OptimizeMacros();
            Count($"{md.DeclaringType.Name}.{md.Name}: SpawnObject result GameObject[] -> PoolableWrapper[] (reads get the objects)");
        }
    }

    static bool TargetsSpawnObject(MethodDefinition md) =>
        md.CustomAttributes.Concat(md.DeclaringType.CustomAttributes).Any(a => a.AttributeType.Name == "HarmonyPatch"
            && a.ConstructorArguments.Any(x => x.Value is TypeReference t && t.FullName == "ObjectsToSpawn")
            && a.ConstructorArguments.Any(x => x.Value is string s && s == "SpawnObject"));

    void Count(string what) { counts.TryGetValue(what, out var c); counts[what] = c + 1; }
    TypeDefinition GT(string full) => Game.GetType(full) ?? throw new Exception("game type missing " + full);
    MethodReference GM(string type, string name, int pc = -1) =>
        M.ImportReference(GT(type).Methods.Single(x => x.Name == name && (pc < 0 || x.Parameters.Count == pc)));

    static bool IsGameField(FieldReference f, string type, string name) =>
        f.Name == name && f.DeclaringType.FullName == type && f.DeclaringType.Scope.Name.StartsWith("Assembly-CSharp");

    static IEnumerable<MethodDefinition> Bodies(ModuleDefinition m) => m.GetTypes().SelectMany(t => t.Methods).Where(md => md.HasBody);

    // -------- helpers: methods of __RoundsCompat (tools/compathelpers) are cloned into the mod on demand --------
    MethodReference Helper(string name)
    {
        if (helperMethods.TryGetValue(name, out var have)) return have;
        if (helper == null)
        {
            helper = new TypeDefinition("", "__RoundsCompat", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class, M.TypeSystem.Object);
            M.Types.Add(helper);
            foreach (var f in HelperSrc.Fields)
                helper.Fields.Add(new FieldDefinition(f.Name, f.Attributes, M.ImportReference(f.FieldType)) { Constant = f.HasConstant ? f.Constant : null, HasConstant = f.HasConstant });
            Count("added internal class __RoundsCompat (helper methods, see tools/compathelpers)");
        }
        var s = HelperSrc.Methods.Single(x => x.Name == name);
        var md = new MethodDefinition(s.Name, s.Attributes, M.ImportReference(s.ReturnType));
        foreach (var p in s.Parameters) md.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, M.ImportReference(p.ParameterType)));
        helper.Methods.Add(md);
        helperMethods[name] = md;
        var b = md.Body; b.InitLocals = s.Body.InitLocals;
        foreach (var v in s.Body.Variables) b.Variables.Add(new VariableDefinition(M.ImportReference(v.VariableType)));
        var map = new Dictionary<Instruction, Instruction>();
        foreach (var i in s.Body.Instructions)
        {
            object? op = i.Operand switch
            {
                TypeReference t => M.ImportReference(t),
                MethodReference mr when mr.DeclaringType.FullName == "__RoundsCompat" => Helper(mr.Name),
                MethodReference mr => M.ImportReference(mr),
                FieldReference fr when fr.DeclaringType.FullName == "__RoundsCompat" => helper.Fields.Single(f => f.Name == fr.Name),
                FieldReference fr => M.ImportReference(fr),
                VariableDefinition v => b.Variables[v.Index],
                ParameterDefinition p => md.Parameters[p.Index],
                _ => i.Operand
            };
            var ni = Instruction.Create(OpCodes.Nop); ni.OpCode = i.OpCode; ni.Operand = op;
            map[i] = ni; b.Instructions.Add(ni);
        }
        foreach (var ni in b.Instructions)
        {
            if (ni.Operand is Instruction t) ni.Operand = map[t];
            else if (ni.Operand is Instruction[] ts) ni.Operand = ts.Select(x => map[x]).ToArray();
        }
        foreach (var h in s.Body.ExceptionHandlers)
            b.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
            {
                TryStart = map[h.TryStart], TryEnd = h.TryEnd == null ? null : map[h.TryEnd],
                HandlerStart = map[h.HandlerStart], HandlerEnd = h.HandlerEnd == null ? null : map[h.HandlerEnd],
                FilterStart = h.FilterStart == null ? null : map[h.FilterStart],
                CatchType = h.CatchType == null ? null : M.ImportReference(h.CatchType)
            });
        return md;
    }

    // replace `at` in place by `first`, then insert `rest` after it (keeps branch targets and handlers valid)
    static void ReplaceWith(ILProcessor il, Instruction at, Instruction first, params Instruction[] rest)
    {
        at.OpCode = first.OpCode; at.Operand = first.Operand;
        var prev = at;
        foreach (var r in rest) { il.InsertAfter(prev, r); prev = r; }
    }

    // ---------------------------------------------------------------- field/method rewrites
    void Generic()
    {
        RetargetScopes();
        var getPlayerID = GM("Player", "get_PlayerID");
        var setPlayerID = GM("Player", "SetPlayerID", 1);
        var getTeamID = GM("Player", "get_TeamID");
        var getMaxHealth = GM("CharacterData", "get_MaxHealth");

        // Setter helpers are cloned even for read-only uses, exactly as the tested patches were made.
        foreach (var md in Bodies(M).ToList())
        {
            if (md.DeclaringType.Name == "__RoundsCompat") continue;
            var body = md.Body; var il = body.GetILProcessor();
            body.SimplifyMacros();
            foreach (var ins in body.Instructions.ToList())
            {
                if (ins.Operand is FieldReference f)
                {
                    bool ld = ins.OpCode == OpCodes.Ldfld, lda = ins.OpCode == OpCodes.Ldflda, st = ins.OpCode == OpCodes.Stfld;
                    MethodReference? getter = null, setter = null; string? key = null;
                    if (IsGameField(f, "Player", "playerID")) { getter = getPlayerID; setter = setPlayerID; key = "Player.playerID"; }
                    else if (IsGameField(f, "Player", "teamID")) { getter = getTeamID; setter = Helper("SetTeamIDRaw"); key = "Player.teamID"; }
                    else if (IsGameField(f, "CharacterData", "maxHealth")) { getter = getMaxHealth; setter = Helper("SetMaxHealthRaw"); key = "CharacterData.maxHealth"; }
                    else if (IsGameField(f, "CardInfo", "cardName") && !st) { getter = Helper("CardName"); key = "CardInfo.cardName"; }
                    else if (IsGameField(f, "CardInfo", "cardDestription") && ld) { getter = Helper("CardDescription"); key = "CardInfo.cardDestription"; }
                    if (key != null)
                    {
                        var getOp = getter!.HasThis ? OpCodes.Callvirt : OpCodes.Call;
                        if (ld) { ReplaceWith(il, ins, Instruction.Create(getOp, getter)); Count($"read {key} -> {getter.Name}"); }
                        else if (lda)
                        {
                            var tmp = new VariableDefinition(getter.ReturnType); body.Variables.Add(tmp); body.InitLocals = true;
                            ReplaceWith(il, ins, Instruction.Create(getOp, getter), Instruction.Create(OpCodes.Stloc, tmp), Instruction.Create(OpCodes.Ldloca, tmp));
                            Count($"address of {key} -> {getter.Name}, copied to a local");
                            Notes.Add($"REVIEW {md.FullName}: took the address of {key}; it now points at a copy, so writes through it are lost");
                        }
                        else if (st)
                        {
                            var s = setter!;
                            ReplaceWith(il, ins, Instruction.Create(s.HasThis ? OpCodes.Callvirt : OpCodes.Call, s));
                            Count($"write {key} -> {s.DeclaringType.Name}.{s.Name}");
                        }
                        else Notes.Add($"MANUAL {md.FullName}: unexpected {ins.OpCode} on {key}; left as is");
                    }
                    else if (ins.OpCode == OpCodes.Ldsfld && f.DeclaringType.FullName == "Optionshandler" && (f.Name == "vol_Master" || f.Name == "vol_Sfx"))
                    {
                        var k = f.Name == "vol_Master" ? "OPTION_VOLUME_MASTER" : "OPTION_VOLUME_SFX";
                        ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldstr, k), Instruction.Create(OpCodes.Call, Helper("GetVolume")));
                        Count($"read Optionshandler.{f.Name} -> options slider \"{k}\"");
                    }
                    else if (ins.OpCode == OpCodes.Ldsfld && f.DeclaringType.FullName == "Optionshandler" && (f.Name == "lockMouse" || f.Name == "lockStick") && f.Resolve() == null)
                    {
                        var k = f.Name == "lockMouse" ? "OPTION_MOUSE_AIM8DIR" : "OPTION_CONTROLLER_AIM8DIR";
                        ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldstr, k), Instruction.Create(OpCodes.Call, Helper("GetToggle")));
                        Count($"read Optionshandler.{f.Name} -> options toggle \"{k}\"");
                    }
                    else if (IsGameField(f, "CardBarButton", "card") && f.Resolve() == null)
                    {
                        ins.Operand = M.ImportReference(GT("CardBarButton").Fields.Single(x => x.Name == "m_cardInfo"));
                        Count("CardBarButton.card -> m_cardInfo");
                    }
                    else if (IsGameField(f, "CardInfo", "cardName") && st)
                    {
                        ReplaceWith(il, ins, Instruction.Create(OpCodes.Call, Helper("SetCardNameRaw")));
                        Count("write CardInfo.cardName -> __RoundsCompat.SetCardNameRaw");
                    }
                    else if (IsGameField(f, "CardInfo", "cardName"))
                        Notes.Add($"MANUAL {md.FullName}: {ins.OpCode} on CardInfo.cardName; left as is");
                    else if (f.DeclaringType.FullName == "Photon.Realtime.RoomOptions" && f.Name == "MaxPlayers" && f.FieldType.FullName == "System.Byte" && f.Resolve() == null)
                    {
                        // byte -> int. A byte on the IL stack is already an int32, so writes only need the new field.
                        var nf = M.ImportReference(f.DeclaringType.Resolve().Fields.Single(x => x.Name == "MaxPlayers"));
                        if (st) { ins.Operand = nf; Count("write RoomOptions.MaxPlayers: byte -> int"); }
                        else if (ld) { ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldfld, nf), Instruction.Create(OpCodes.Conv_U1)); Count("read RoomOptions.MaxPlayers: int, converted to the old byte"); }
                        else Notes.Add($"MANUAL {md.FullName}: {ins.OpCode} on RoomOptions.MaxPlayers (now an int); left as is");
                    }
                    else if (f.DeclaringType.FullName == "UnityEngine.UIVertex" && f.FieldType.FullName == "UnityEngine.Vector2" && f.Resolve() == null
                             && f.DeclaringType.Resolve()?.Fields.FirstOrDefault(x => x.Name == f.Name && x.FieldType.FullName == "UnityEngine.Vector4") is FieldDefinition v4)
                    {
                        // uv0..uv3: Vector2 in Unity 2018, Vector4 now. Vector4's implicit conversions keep x and y (z, w = 0).
                        var nf = M.ImportReference(v4);
                        MethodReference Conv(string from, string to) => M.ImportReference(v4.FieldType.Resolve().Methods.Single(x =>
                            x.Name == "op_Implicit" && x.Parameters[0].ParameterType.FullName == from && x.ReturnType.FullName == to));
                        if (ld)
                        {
                            ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldfld, nf), Instruction.Create(OpCodes.Call, Conv("UnityEngine.Vector4", "UnityEngine.Vector2")));
                            Count($"read UIVertex.{f.Name}: Vector4 now, converted to Vector2");
                        }
                        else if (st)
                        {
                            ReplaceWith(il, ins, Instruction.Create(OpCodes.Call, Conv("UnityEngine.Vector2", "UnityEngine.Vector4")), Instruction.Create(OpCodes.Stfld, nf));
                            Count($"write UIVertex.{f.Name}: Vector2 converted to the new Vector4");
                        }
                        else Notes.Add($"MANUAL {md.FullName}: {ins.OpCode} on UIVertex.{f.Name} (Vector4 now); left as is");
                    }
                    else if (Scanner.NowPrivate(f) is FieldDefinition pf)
                    {
                        // FieldAccessException otherwise (Unity's Mono ignores IgnoresAccessChecksTo): go through reflection.
                        var tn = Instruction.Create(OpCodes.Ldstr, pf.DeclaringType.FullName.Replace('/', '+'));
                        var fn = Instruction.Create(OpCodes.Ldstr, pf.Name);
                        var code = ins.OpCode.Code;
                        if (code is Code.Ldfld or Code.Ldsfld)
                        {
                            var get = new List<Instruction>();
                            if (code == Code.Ldsfld) get.Add(Instruction.Create(OpCodes.Ldnull));
                            get.AddRange(new[] { tn, fn, Instruction.Create(OpCodes.Call, Helper("GetGameField")), Instruction.Create(OpCodes.Unbox_Any, f.FieldType) });
                            ReplaceWith(il, ins, get[0], get.Skip(1).ToArray());
                            Count($"read private {pf.DeclaringType.Name}.{pf.Name} -> reflection");
                        }
                        else if (code is Code.Stfld or Code.Stsfld)
                        {
                            var set = new List<Instruction>();
                            if (f.FieldType.IsValueType) set.Add(Instruction.Create(OpCodes.Box, f.FieldType));
                            set.AddRange(new[] { tn, fn, Instruction.Create(OpCodes.Call, Helper(code == Code.Stfld ? "SetGameField" : "SetStaticGameField")) });
                            ReplaceWith(il, ins, set[0], set.Skip(1).ToArray());
                            Count($"write private {pf.DeclaringType.Name}.{pf.Name} -> reflection");
                        }
                        else Notes.Add($"MANUAL {md.FullName}: {ins.OpCode} on {pf.DeclaringType.Name}.{pf.Name}, which is private now; left as is");
                    }
                }
                else if (Scanner.IsDontDestroyOnLoad(ins) && scanner.RunsAtPluginLoad(md))
                {
                    ReplaceWith(il, ins, Instruction.Create(OpCodes.Call, Helper("KeepAlive")));
                    Count($"{md.DeclaringType.Name}.{md.Name}: DontDestroyOnLoad -> __RoundsCompat.KeepAlive (survives the first scene load)");
                }
                else if (ins.Operand is MethodReference pm && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && OtherCall(il, ins, pm)) { }
                else if (ins.Operand is MethodReference um && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && UnboundCall(il, ins, um)) { }
                else if (ins.Operand is MethodReference mr && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && mr.DeclaringType.Scope.Name.StartsWith("Assembly-CSharp"))
                {
                    var dt = mr.DeclaringType.FullName;
                    if (dt == "Debug" && mr.Resolve() == null && UnityDebug(mr) is MethodReference ud)
                    {
                        ins.OpCode = OpCodes.Call; ins.Operand = ud;
                        Count($"Debug.{mr.Name} (the old game's own Debug class) -> UnityEngine.Debug.{mr.Name}");
                        continue;
                    }
                    if (dt == "UIHandler" && mr.Resolve() == null && UiTextHelper(mr) is string h)
                    {
                        ReplaceWith(il, ins, Instruction.Create(OpCodes.Call, Helper(h)));
                        Count($"UIHandler.{mr.Name}(string) -> __RoundsCompat.{h} (shows the text untranslated)");
                        continue;
                    }
                    if (dt == "PlayerManager" && mr.Name == "AddPlayerDiedAction" && mr.Parameters.Count == 1 && mr.Resolve() == null)
                    {
                        ReplaceWith(il, ins, Instruction.Create(OpCodes.Call, Helper("AddPlayerDiedAction")));
                        Count("PlayerManager.AddPlayerDiedAction(...) -> PlayerDiedAction += ...");
                        continue;
                    }
                    if (dt == "ObjectsToSpawn" && mr.Name == "SpawnObject" && mr.Resolve() == null && ins.Next?.OpCode == OpCodes.Pop
                        && GT(dt).Methods.SingleOrDefault(x => x.Name == mr.Name && x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(mr.Parameters.Select(p => p.ParameterType.FullName))) is MethodDefinition spawn)
                    {
                        ins.Operand = M.ImportReference(spawn);
                        Count("ObjectsToSpawn.SpawnObject(...) with its result dropped -> the pooled one (returns PoolableWrapper[])");
                        continue;
                    }
                    if ((dt is "Damagable" or "HealthHandler" or "DamageOverTime") && mr.Name is "CallTakeDamage" or "TakeDamage" or "DoDamage" or "TakeDamageOverTime" or "DoDamageOverTime" or "RPCA_SendTakeDamage"
                        && mr.Resolve() == null)
                    {
                        var target = GT(dt).Methods.SingleOrDefault(x => x.Name == mr.Name && x.Parameters.Count == mr.Parameters.Count + 1
                            && x.Parameters.Last().ParameterType.FullName == "HealthHandler/DamageSource"
                            && x.Parameters.Take(mr.Parameters.Count).Select(p => p.ParameterType.FullName).SequenceEqual(mr.Parameters.Select(p => p.ParameterType.FullName)));
                        if (target == null) { Notes.Add($"MANUAL {md.FullName}: no DamageSource overload for {mr.FullName}"); continue; }
                        ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(ins.OpCode, M.ImportReference(target)));
                        Count($"{dt}.{mr.Name}(...) -> + DamageSource.Player argument");
                    }
                }
            }
            body.OptimizeMacros();
        }
    }

    static string Sig(MethodReference m) => string.Join(",", m.Parameters.Select(p => p.ParameterType.Name));

    // Library calls whose signature changed in the 2025 build (Photon, TextMeshPro). True when `ins` was rewritten.
    bool OtherCall(ILProcessor il, Instruction ins, MethodReference mr)
    {
        var dt = mr.DeclaringType.FullName;
        if (dt is not ("Photon.Realtime.Room" or "TMPro.TMP_Text" or "TMPro.TextMeshProUGUI" or "TMPro.TextMeshPro" or "TMPro.TMP_FontAsset")) return false;
        MethodDefinition? Find(string name, string sig)
        {
            for (var t = mr.DeclaringType.Resolve(); t != null; t = t.BaseType?.Resolve())
                if (t.Methods.FirstOrDefault(x => x.Name == name && Sig(x) == sig) is MethodDefinition d) return d;
            return null;
        }
        if (mr.Resolve() != null) return false;
        switch (mr.Name, Sig(mr))
        {
            case ("GetPlayer", "Int32") when Find("GetPlayer", "Int32,Boolean") is MethodDefinition gp:
                ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(ins.OpCode, M.ImportReference(gp)));
                Count("Room.GetPlayer(id) -> GetPlayer(id, findMaster: false)");
                return true;
            case ("get_PlayerCount", "") when mr.ReturnType.FullName == "System.Byte" && Find("get_PlayerCount", "") is MethodDefinition pc:
                ReplaceWith(il, ins, Instruction.Create(ins.OpCode, M.ImportReference(pc)), Instruction.Create(OpCodes.Conv_U1));
                Count("Room.PlayerCount: int, converted to the old byte");
                return true;
            case ("HasCharacter", "Char,Boolean") when Find("HasCharacter", "Char,Boolean,Boolean") is MethodDefinition hc:
                ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(ins.OpCode, M.ImportReference(hc)));
                Count("TMP_FontAsset.HasCharacter(c, searchFallbacks) -> + tryAddCharacter: false");
                return true;
            case ("ForceMeshUpdate", "") when Find("ForceMeshUpdate", "Boolean,Boolean") is MethodDefinition fm:
                ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(ins.OpCode, M.ImportReference(fm)));
                Count("TMP_Text.ForceMeshUpdate() -> ForceMeshUpdate(false, false)");
                return true;
        }
        return false;
    }

    // UnboundLib 3's obsolete Unbound.RegisterMaps forwarders, gone in UnboundLib 4. Each called LevelManager.RegisterMaps
    // with the category "Modded" (the two-argument one ignored its categoryName), so fix makes that call. True when
    // `ins` was rewritten.
    bool UnboundCall(ILProcessor il, Instruction ins, MethodReference mr)
    {
        if (mr.Name != "RegisterMaps" || mr.DeclaringType.FullName != "UnboundLib.Unbound" || mr.DeclaringType.Scope.Name != "UnboundLib"
            || mr.HasThis || mr.Resolve() != null) return false;
        var sig = Sig(mr);
        if (sig is not ("AssetBundle" or "IEnumerable`1" or "IEnumerable`1,String")) return false;
        // Built on the mod's own UnboundLib reference: importing the resolved method would add a second reference to
        // UnboundLib 4.
        var lm = new TypeReference("UnboundLib.Utils", "LevelManager", M, mr.DeclaringType.Scope);
        var target = new MethodReference("RegisterMaps", M.TypeSystem.Void, lm);
        target.Parameters.Add(new ParameterDefinition(mr.Parameters[0].ParameterType));
        target.Parameters.Add(new ParameterDefinition(M.TypeSystem.String));
        if (target.Resolve() == null) return false;
        var call = Instruction.Create(OpCodes.Call, target);
        if (sig == "IEnumerable`1,String")
            ReplaceWith(il, ins, Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Ldstr, "Modded"), call);
        else
            ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldstr, "Modded"), call);
        Count($"Unbound.RegisterMaps({sig.Replace("`1", "<string>")}) -> LevelManager.RegisterMaps(..., \"Modded\"), as UnboundLib 3 forwarded it");
        return true;
    }

    // The old game had its own global Debug class (Log, LogError, LogWarning, DrawLine). UnityEngine.Debug has the
    // same methods; string parameters there are object.
    MethodReference? UnityDebug(MethodReference mr)
    {
        var core = M.AssemblyResolver.Resolve(new AssemblyNameReference("UnityEngine.CoreModule", new Version(0, 0, 0, 0)));
        var d = core.MainModule.GetType("UnityEngine.Debug");
        var hit = d?.Methods.FirstOrDefault(x => x.IsStatic && x.Name == mr.Name && x.Parameters.Count == mr.Parameters.Count
            && x.Parameters.Select(p => p.ParameterType).Zip(mr.Parameters.Select(p => p.ParameterType))
                .All(z => z.First.FullName == z.Second.FullName || z.First.FullName == "System.Object" && !z.Second.IsValueType));
        return hit == null ? null : M.ImportReference(hit);
    }

    // UIHandler's screen-text methods take a LocalizedString now; the old string overloads go through a helper.
    static string? UiTextHelper(MethodReference mr) => (mr.Name, Sig(mr)) switch
    {
        ("ShowJoinGameText", "String,Color") => "ShowJoinGameText",
        ("DisplayScreenText", "Color,String,Single") => "DisplayScreenText",
        ("DisplayScreenTextLoop", "Color,String") => "DisplayScreenTextLoop",
        ("DisplayScreenTextLoop", "String") => "DisplayScreenTextLoopNoColor",
        _ => null
    };

    void RetargetScopes()
    {
        AssemblyNameReference Ref(string name)
        {
            var existing = M.AssemblyReferences.FirstOrDefault(a => a.Name == name);
            if (existing != null) return existing;
            var def = M.AssemblyResolver.Resolve(new AssemblyNameReference(name, new Version(0, 0, 0, 0)));
            var r = new AssemblyNameReference(def.Name.Name, def.Name.Version) { PublicKeyToken = def.Name.PublicKeyToken, Culture = def.Name.Culture };
            M.AssemblyReferences.Add(r);
            return r;
        }
        void Retarget(TypeReference tr, string where)
        {
            if (tr.Scope is not AssemblyNameReference an) return;
            if (an.Name == "Assembly-CSharp-firstpass" && tr.Namespace == "Steamworks")
            { tr.Scope = Ref("com.rlabrecque.steamworks.net"); Count($"{where}{tr.FullName}: Assembly-CSharp-firstpass -> com.rlabrecque.steamworks.net"); }
            else if (an.Name == "UnityEngine.CoreModule" && tr.FullName == "UnityEngine.Input")
            { tr.Scope = Ref("UnityEngine.InputLegacyModule"); Count($"{where}UnityEngine.Input: UnityEngine.CoreModule -> UnityEngine.InputLegacyModule"); }
            else if (an.Name == "UnityEngine.TextCoreModule" && scanner.MovedTextCore(tr) is string to)
            { tr.Scope = Ref(to); Count($"{where}{tr.FullName}: UnityEngine.TextCoreModule -> {to}"); }
        }
        foreach (var tr in M.GetTypeReferences().ToList()) Retarget(tr, "type ");
        // typeof(...) in Harmony attributes is stored as an assembly-qualified name, so it needs the same change.
        foreach (var t in Scanner.AllTypes(M))
            foreach (var holder in new ICustomAttributeProvider[] { t }.Concat(t.Methods))
                foreach (var ca in holder.CustomAttributes.Where(a => a.AttributeType.Name == "HarmonyPatch"))
                    foreach (var arg in ca.ConstructorArguments)
                    {
                        if (arg.Value is TypeReference tr) Retarget(tr, "[HarmonyPatch] typeof ");
                        else if (arg.Value is CustomAttributeArgument[] arr)
                            foreach (var a in arr) if (a.Value is TypeReference tr2) Retarget(tr2, "[HarmonyPatch] typeof ");
                    }
    }

    // RPCs to game methods that gained a trailing DamageSource: append DamageSource.Player to the argument array.
    void RpcArgs()
    {
        var dmgSrc = M.ImportReference(GT("HealthHandler").NestedTypes.Single(t => t.Name == "DamageSource"));
        foreach (var md in Bodies(M).ToList())
        {
            var sites = scanner.RpcSites(md).Where(r => !r.Targets.Any(x => x.Parameters.Count == r.Args) && Scanner.OnlyMissingDamageSource(r)).ToList();
            if (sites.Count == 0) continue;
            var body = md.Body; var il = body.GetILProcessor(); body.SimplifyMacros();
            foreach (var r in sites)
            {
                r.Newarr.Previous.OpCode = OpCodes.Ldc_I4; r.Newarr.Previous.Operand = r.Args + 1;
                il.InsertBefore(r.Call, Instruction.Create(OpCodes.Dup));
                il.InsertBefore(r.Call, Instruction.Create(OpCodes.Ldc_I4, r.Args));
                il.InsertBefore(r.Call, Instruction.Create(OpCodes.Ldc_I4, 0));
                il.InsertBefore(r.Call, Instruction.Create(OpCodes.Box, dmgSrc));
                il.InsertBefore(r.Call, Instruction.Create(OpCodes.Stelem_Ref));
                Count($"{md.DeclaringType.Name}.{md.Name}: RPC(\"{r.Name}\") arguments {r.Args} -> {r.Args + 1} (+ DamageSource.Player)");
            }
            body.OptimizeMacros();
        }
    }

    // ---------------------------------------------------------------- Harmony and reflection renames
    // [HarmonyPatch] targets that only need different argumentTypes: a damage method that gained a trailing
    // DamageSource, and CardBar.OnHover, which now has two overloads (the hover one takes a CardBarButton).
    void HarmonyTargets(TypeDefinition t)
    {
        static IEnumerable<CustomAttribute> Patches(ICustomAttributeProvider p) => p.CustomAttributes.Where(a => a.AttributeType.Name == "HarmonyPatch");
        foreach (var (info, methods) in scanner.HarmonyPatches(t).ToList())
        {
            if (scanner.DamageSourceOverload(info) != null)
            {
                // the attribute holding argumentTypes: the patch method's own, else the class's
                foreach (var holder in methods.Cast<ICustomAttributeProvider>().Append(t))
                {
                    var hit = Patches(holder).SelectMany(a => a.ConstructorArguments.Select((arg, i) => (a, arg, i)))
                        .FirstOrDefault(x => x.arg.Value is CustomAttributeArgument[] arr && arr.Length == info.ArgTypes!.Count
                            && arr.Select(e => (e.Value as TypeReference)?.FullName).SequenceEqual(info.ArgTypes.Select(a => a.FullName)));
                    if (hit.a == null) continue;
                    var types = (CustomAttributeArgument[])hit.arg.Value;
                    var dmg = M.ImportReference(GT("HealthHandler").NestedTypes.Single(n => n.Name == "DamageSource"));
                    hit.a.ConstructorArguments[hit.i] = new CustomAttributeArgument(hit.arg.Type, types.Append(new CustomAttributeArgument(types[0].Type, dmg)).ToArray());
                    Count($"[HarmonyPatch] {t.Name}: {info.Method} argumentTypes + HealthHandler.DamageSource");
                    break;
                }
            }
            else if (scanner.OnHoverAmbiguous(info))
            {
                ICustomAttributeProvider holder = methods.Count == 1 && Patches(methods[0]).Any() ? methods[0] : t;
                var src = Patches(holder).FirstOrDefault() ?? Patches(t).First();
                if (!src.AttributeType.Resolve().Methods.Any(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1
                    && m.Parameters[0].ParameterType.FullName == "System.Type[]")) continue;
                // Built from the mod's own Harmony and corlib references: importing the resolved constructor would add
                // a second reference to whatever Harmony and mscorlib versions rounds-port read.
                var type = new TypeReference("System", "Type", M, M.TypeSystem.CoreLibrary);
                var ctor = new MethodReference(".ctor", M.TypeSystem.Void, src.AttributeType) { HasThis = true };
                ctor.Parameters.Add(new ParameterDefinition(new ArrayType(type)));
                var ca = new CustomAttribute(ctor);
                ca.ConstructorArguments.Add(new CustomAttributeArgument(new ArrayType(type), new[] { new CustomAttributeArgument(type, M.ImportReference(GT("CardBarButton"))) }));
                holder.CustomAttributes.Add(ca);
                Count($"[HarmonyPatch] {t.Name}: CardBar.OnHover -> argumentTypes {{ typeof(CardBarButton) }}");
            }
            else if (scanner.TargetProblem(info) is string problem
                     && Known.HarmonyTarget(info, problem, false, scanner.TargetInPortedCode(info)).Detail.Contains(Known.DisablesPatch))
                DisablePatch(t, info, methods);
        }
    }

    // A [HarmonyPatch] whose target the game no longer has: HarmonyX throws on it and PatchAll stops, so the mod's later
    // patches don't apply either. Drop the attribute naming the target and rename the patch methods it applied to
    // (HarmonyX also treats methods named Prefix, Postfix... as patches), so PatchAll skips them. The new name keeps the
    // target: __RoundsCompat_Disabled_<type>_<method>_<old name> (rounds-port Runtime runs CardBar.Update ones itself).
    void DisablePatch(TypeDefinition t, HarmonyInfo info, List<MethodDefinition> methods)
    {
        static bool IsPatchAttr(CustomAttribute a) => a.AttributeType.Name is "HarmonyPatch" or "HarmonyPrefix" or "HarmonyPostfix"
            or "HarmonyFinalizer" or "HarmonyTranspiler" or "HarmonyILManipulator";
        static string? MethodArg(CustomAttribute a) => a.ConstructorArguments.Select(x => x.Value).OfType<string>().FirstOrDefault();
        var target = $"{info.Type?.Name ?? info.TypeName?.Split('.', '+').Last()}_{info.Method}";
        if (!methods.Any(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPatch")))
            foreach (var a in t.CustomAttributes.Where(a => a.AttributeType.Name == "HarmonyPatch").ToList()) t.CustomAttributes.Remove(a);
        foreach (var m in methods)
        {
            var own = m.CustomAttributes.Where(a => a.AttributeType.Name == "HarmonyPatch").ToList();
            if (own.Count(a => MethodArg(a) != null) > 1)
            {
                // one of several targets: drop only the attribute naming this one
                foreach (var a in own.Where(a => MethodArg(a) == info.Method)) m.CustomAttributes.Remove(a);
                continue;
            }
            foreach (var a in m.CustomAttributes.Where(IsPatchAttr).ToList()) m.CustomAttributes.Remove(a);
            m.Name = Scanner.DisabledPatch + target + "_" + m.Name;
        }
        Count($"[HarmonyPatch] {t.Name}: {info} disabled, its target is gone");
    }

    // ---------------------------------------------------------------- plugin load
    // A plugin Awake that looks up scene objects: BepInEx now starts plugins before the game has loaded any scene. The
    // body moves to <Awake>__RoundsCompat, and Awake hands it to __RoundsCompat.AfterFirstScene.
    void PluginLoad()
    {
        foreach (var t in Scanner.AllTypes(M).ToList())
        {
            if (scanner.PluginAwake(t) is not MethodDefinition awake || scanner.SceneLookupAtLoad(awake) == null) continue;
            var later = Helper("AfterFirstScene");
            var action = later.Parameters[0].ParameterType;
            awake.Name = "Awake__RoundsCompat";
            var ctor = new MethodReference(".ctor", M.TypeSystem.Void, action) { HasThis = true };
            ctor.Parameters.Add(new ParameterDefinition(M.TypeSystem.Object));
            ctor.Parameters.Add(new ParameterDefinition(M.TypeSystem.IntPtr));
            var nw = new MethodDefinition("Awake", MethodAttributes.Private | MethodAttributes.HideBySig, M.TypeSystem.Void);
            var il = nw.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldftn, awake);
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Call, later);
            il.Emit(OpCodes.Ret);
            t.Methods.Add(nw);
            Count($"{t.Name}.Awake: looks up scene objects -> runs once the first scene has loaded (__RoundsCompat.AfterFirstScene)");
        }
    }

    // A Harmony patch on CardBar.OnHover with the old `CardInfo card` parameter: it takes the hovered CardBarButton now.
    // The parameter becomes `CardBarButton cardButton`, and each read of it reads cardButton.m_cardInfo.
    void HoverCardParam(TypeDefinition t, MethodDefinition pm, ParameterDefinition p)
    {
        if (!pm.HasBody || pm.Body.Instructions.Any(i => i.Operand == p && i.OpCode.Code is not (Code.Ldarg or Code.Ldarg_S)
            && i.OpCode.Code is Code.Starg or Code.Starg_S or Code.Ldarga or Code.Ldarga_S))
        { Notes.Add($"MANUAL {pm.FullName}: writes or takes the address of `card`; left as is"); return; }
        var button = GT("CardBarButton");
        var cardInfo = M.ImportReference(button.Fields.Single(f => f.Name == "m_cardInfo"));
        var body = pm.Body; var il = body.GetILProcessor();
        body.SimplifyMacros();
        foreach (var ins in body.Instructions.Where(i => i.OpCode == OpCodes.Ldarg && i.Operand == p).ToList())
            il.InsertAfter(ins, Instruction.Create(OpCodes.Ldfld, cardInfo));
        body.OptimizeMacros();
        p.ParameterType = M.ImportReference(button);
        p.Name = "cardButton";
        Count($"Harmony {t.Name}.{pm.Name}: CardBar.OnHover's CardInfo card -> CardBarButton cardButton (.m_cardInfo)");
    }

    void Renames()
    {
        foreach (var t in Scanner.AllTypes(M).ToList())
        {
            HarmonyTargets(t);

            foreach (var holder in new ICustomAttributeProvider[] { t }.Concat(t.Methods))
                foreach (var ca in holder.CustomAttributes.Where(a => a.AttributeType.Name == "HarmonyPatch"))
                    for (int i = 0; i < ca.ConstructorArguments.Count; i++)
                        if (ca.ConstructorArguments[i].Value is "GetRanomCard")
                        {
                            ca.ConstructorArguments[i] = new CustomAttributeArgument(ca.ConstructorArguments[i].Type, "GetRandomCard");
                            Count($"[HarmonyPatch] {t.Name}: \"GetRanomCard\" -> \"GetRandomCard\"");
                        }

            foreach (var (info, methods) in scanner.HarmonyPatches(t))
                foreach (var pm in methods)
                    foreach (var (param, _, fixTo) in scanner.PatchParamProblems(info, pm).ToList())
                        if (fixTo == "cardButton" && pm.Parameters.FirstOrDefault(p => p.Name == param) is ParameterDefinition cp)
                            HoverCardParam(t, pm, cp);
                        else if (fixTo != null && pm.Parameters.FirstOrDefault(p => p.Name == param) is ParameterDefinition pd)
                        {
                            pd.Name = fixTo;
                            Count($"Harmony {t.Name}.{pm.Name}: injected field {param} -> {fixTo}");
                        }

            foreach (var m in t.Methods.Where(m => m.HasBody))
            {
                foreach (var ins in m.Body.Instructions)
                    if (ins.OpCode.Code == Code.Ldstr && (string)ins.Operand == "GetRanomCard")
                    { ins.Operand = "GetRandomCard"; Count($"{t.Name}.{m.Name}: \"GetRanomCard\" -> \"GetRandomCard\""); }
                foreach (var s in scanner.ReflectSites(m).ToList())
                    if (s.FixTo != null)
                    { s.Ldstr.Operand = s.FixTo; Count($"{t.Name}.{m.Name}: {s.Call}(\"{s.Name}\") -> \"{s.FixTo}\""); }
            }
        }
    }
}
