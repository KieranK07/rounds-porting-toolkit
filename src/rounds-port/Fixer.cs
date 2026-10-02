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
    }

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
                    else if (IsGameField(f, "CardInfo", "cardName"))
                        Notes.Add($"MANUAL {md.FullName}: writes CardInfo.cardName (private now; names come from localization)");
                }
                else if (ins.Operand is MethodReference mr && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && mr.DeclaringType.Scope.Name.StartsWith("Assembly-CSharp"))
                {
                    var dt = mr.DeclaringType.FullName;
                    if (dt == "PlayerManager" && mr.Name == "AddPlayerDiedAction" && mr.Parameters.Count == 1 && mr.Resolve() == null)
                    {
                        ReplaceWith(il, ins, Instruction.Create(OpCodes.Call, Helper("AddPlayerDiedAction")));
                        Count("PlayerManager.AddPlayerDiedAction(...) -> PlayerDiedAction += ...");
                        continue;
                    }
                    if ((dt is "Damagable" or "HealthHandler" or "DamageOverTime") && mr.Name is "CallTakeDamage" or "TakeDamage" or "DoDamage" or "TakeDamageOverTime"
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
        foreach (var tr in M.GetTypeReferences().ToList())
        {
            if (tr.Scope is not AssemblyNameReference an) continue;
            if (an.Name == "Assembly-CSharp-firstpass" && tr.Namespace == "Steamworks")
            { tr.Scope = Ref("com.rlabrecque.steamworks.net"); Count($"type {tr.FullName}: Assembly-CSharp-firstpass -> com.rlabrecque.steamworks.net"); }
            else if (an.Name == "UnityEngine.CoreModule" && tr.FullName == "UnityEngine.Input")
            { tr.Scope = Ref("UnityEngine.InputLegacyModule"); Count("type UnityEngine.Input: UnityEngine.CoreModule -> UnityEngine.InputLegacyModule"); }
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
    void Renames()
    {
        foreach (var t in Scanner.AllTypes(M).ToList())
        {
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
                        if (fixTo != null && pm.Parameters.FirstOrDefault(p => p.Name == param) is ParameterDefinition pd)
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
