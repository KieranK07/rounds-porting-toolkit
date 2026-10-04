using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// compatfix: rewrites IL of old ROUNDS mods for the Dec-2025 (Unity 2022.3) game build.
// Reads originals from the game's BepInEx/plugins (read-only), writes to scratchpad/staging/patched/<pkg>/<rel>.
// See ../MAPPING.md and ../PATCHLOG-simple.md.

string home = Environment.GetEnvironmentVariable("HOME")!;
string gameDir = Path.Combine(home, "Library/Application Support/Steam/steamapps/common/ROUNDS");
string managed = Path.Combine(gameDir, "ROUNDS.app/Contents/Resources/Data/Managed");
string plugins = Path.Combine(gameDir, "BepInEx/plugins");
string scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
if (!Directory.Exists(Path.Combine(scratch, "staging"))) scratch = "<work>";
string outRoot = Path.Combine(scratch, "staging/patched");
string helperDll = Path.Combine(scratch, "compat/compathelpers/bin/Release/net472/compathelpers.dll");

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
resolver.AddSearchDirectory(Path.Combine(gameDir, "BepInEx/core"));
resolver.AddSearchDirectory(Path.Combine(scratch, "staging/UnboundLib"));
resolver.AddSearchDirectory(Path.Combine(scratch, "staging/RoundsWithFriends"));
foreach (var d in Directory.GetDirectories(plugins, "*", SearchOption.AllDirectories))
    if (!d.Contains("UnboundLib-3") && !d.Contains("MMHook-1.0.0") && !d.Contains("RoundsWithFriends-2.2.2")) resolver.AddSearchDirectory(d);
resolver.AddSearchDirectory(plugins);

var game = AssemblyDefinition.ReadAssembly(Path.Combine(managed, "Assembly-CSharp.dll"), new ReaderParameters { AssemblyResolver = resolver }).MainModule;
TypeDefinition G(string full) => game.GetType(full) ?? throw new Exception("game type missing: " + full);
var helperMod = ModuleDefinition.ReadModule(helperDll, new ReaderParameters { AssemblyResolver = resolver });
var helperType = helperMod.GetType("__RoundsCompat");

var mods = new (string pkg, string rel)[] {
    ("XAngelMoonX-CR-2.7.0", "CosmicRounds.dll"),
    ("willis81808-ModsPlus-1.6.2", "plugins/ModsPlus.dll"),
    ("Pykess-GunUnblockablePatch-0.0.0", "GunUnblockablePatch.dll"),
    ("Pykess-TemporaryStatsPatch-0.0.2", "TemporaryStatsPatch.dll"),
    ("Root-Classes_Manager_Reborn-1.5.5", "ClassesManagerReborn.dll"),
    ("Root-RarityLib-1.3.0", "RarityLib.dll"),
    ("BossSloth-CardBarPatch-2.1.1", "CardBarPatch.dll"),
    ("RoundsModding-Grow_Patch-0.0.0", "GrowPatch.dll"),
    ("RoundsModding-Performance_Improvements-0.2.0", "plugins/PerformanceImprovements.dll"),
};

var only = args.Length > 0 ? args.ToHashSet() : null;
var log = new List<string>();
int failures = 0;

foreach (var (pkg, rel) in mods)
{
    if (only != null && !only.Any(o => pkg.Contains(o))) continue;
    var src = Path.Combine(plugins, pkg, rel);
    var dst = Path.Combine(outRoot, pkg, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
    // copy every other package file (manifest.json, icon, README, LICENSE...)
    foreach (var f in Directory.GetFiles(Path.Combine(plugins, pkg), "*", SearchOption.AllDirectories))
    {
        var r = Path.GetRelativePath(Path.Combine(plugins, pkg), f);
        if (r == rel) continue;
        var d = Path.Combine(outRoot, pkg, r);
        Directory.CreateDirectory(Path.GetDirectoryName(d)!);
        File.Copy(f, d, true);
    }
    var asm = AssemblyDefinition.ReadAssembly(src, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });
    var ctx = new Ctx(asm.MainModule, game, helperType, pkg);
    log.Add($"## {pkg} :: {rel}");
    try
    {
        ctx.Generic();
        switch (pkg.Split('-')[1])
        {
            case "GunUnblockablePatch": ctx.GunUnblockable(); break;
            case "Grow_Patch": ctx.Grow(); break;
            case "Performance_Improvements": ctx.PerfImprovements(); break;
        }
        ctx.Finish();
        asm.Write(dst);
    }
    catch (Exception e) { ctx.Log.Add("!! FAILED: " + e); failures++; }
    log.AddRange(ctx.Log.Select(l => "- " + l));
    log.Add("");
}
File.WriteAllLines(Path.Combine(outRoot, "compatfix.log"), log);
Console.WriteLine(string.Join("\n", log));
return failures;

class Ctx
{
    public readonly ModuleDefinition M; readonly ModuleDefinition Game; readonly TypeDefinition HelperSrc; readonly string Pkg;
    public readonly List<string> Log = new();
    readonly SortedDictionary<string, int> counts = new();
    TypeDefinition? helper; readonly Dictionary<string, MethodDefinition> helperMethods = new();

    public Ctx(ModuleDefinition m, ModuleDefinition game, TypeDefinition helperSrc, string pkg) { M = m; Game = game; HelperSrc = helperSrc; Pkg = pkg; }

    void Count(string what) { counts.TryGetValue(what, out var c); counts[what] = c + 1; }
    TypeDefinition GT(string full) => Game.GetType(full) ?? throw new Exception("game type missing " + full);
    MethodReference GM(string type, string name, int pc = -1) =>
        M.ImportReference(GT(type).Methods.Single(x => x.Name == name && (pc < 0 || x.Parameters.Count == pc)));
    FieldReference GF(string type, string name) => M.ImportReference(GT(type).Fields.Single(x => x.Name == name));

    static bool IsGameField(FieldReference f, string type, string name) =>
        f.Name == name && f.DeclaringType.FullName == type && f.DeclaringType.Scope.Name.StartsWith("Assembly-CSharp");

    static IEnumerable<MethodDefinition> Bodies(ModuleDefinition m) =>
        m.GetTypes().SelectMany(t => t.Methods).Where(md => md.HasBody);

    // -------- helper injection: clone methods of compathelpers.__RoundsCompat on demand --------
    public MethodReference Helper(string name)
    {
        if (helperMethods.TryGetValue(name, out var have)) return have;
        if (helper == null)
        {
            helper = new TypeDefinition("", "__RoundsCompat", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class, M.TypeSystem.Object);
            M.Types.Add(helper);
            foreach (var f in HelperSrc.Fields)
                helper.Fields.Add(new FieldDefinition(f.Name, f.Attributes, M.ImportReference(f.FieldType)) { Constant = f.HasConstant ? f.Constant : null, HasConstant = f.HasConstant });
            Log.Add("injected internal static class `__RoundsCompat` (helpers cloned from compat/compathelpers/Helpers.cs)");
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

    // replace instruction `at` in place by `first`, then insert `rest` after it (keeps branch targets/handlers valid)
    static void ReplaceWith(ILProcessor il, Instruction at, Instruction first, params Instruction[] rest)
    {
        at.OpCode = first.OpCode; at.Operand = first.Operand;
        var prev = at;
        foreach (var r in rest) { il.InsertAfter(prev, r); prev = r; }
    }

    // ------------------------------- generic rewrites -------------------------------
    public void Generic()
    {
        RetargetScopes();
        var getPlayerID = GM("Player", "get_PlayerID");
        var setPlayerID = GM("Player", "SetPlayerID", 1);
        var getTeamID = GM("Player", "get_TeamID");
        var getMaxHealth = GM("CharacterData", "get_MaxHealth");
        var dmgSrc = M.ImportReference(GT("HealthHandler").NestedTypes.Single(t => t.Name == "DamageSource"));

        foreach (var md in Bodies(M).ToList())
        {
            if (md.DeclaringType.Name == "__RoundsCompat") continue;
            var body = md.Body; var il = body.GetILProcessor();
            bool touched = false;
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
                    else if (IsGameField(f, "CardInfo", "cardName")) { getter = Helper("CardName"); key = "CardInfo.cardName"; }
                    if (key != null)
                    {
                        var getOp = getter!.HasThis ? OpCodes.Callvirt : OpCodes.Call;
                        if (ld) { ReplaceWith(il, ins, Instruction.Create(getOp, getter)); Count($"ldfld {key} -> {getter.Name}"); }
                        else if (lda)
                        {
                            var tmp = new VariableDefinition(getter.ReturnType); body.Variables.Add(tmp); body.InitLocals = true;
                            ReplaceWith(il, ins, Instruction.Create(getOp, getter), Instruction.Create(OpCodes.Stloc, tmp), Instruction.Create(OpCodes.Ldloca, tmp));
                            Count($"ldflda {key} -> {getter.Name} spilled to local");
                            Log.Add($"ldflda {key} in {md.FullName}: spilled (address consumer: {ins.Next?.Next?.Next})");
                        }
                        else if (st)
                        {
                            if (setter == null) throw new Exception($"no setter mapping for stfld {key} in {md.FullName}");
                            ReplaceWith(il, ins, Instruction.Create(setter.HasThis ? OpCodes.Callvirt : OpCodes.Call, setter));
                            Count($"stfld {key} -> {setter.DeclaringType.Name}.{setter.Name}");
                        }
                        else throw new Exception($"unexpected {ins.OpCode} {key} in {md.FullName}");
                        touched = true;
                    }
                    else if (ins.OpCode == OpCodes.Ldsfld && f.DeclaringType.FullName == "Optionshandler" && (f.Name == "vol_Master" || f.Name == "vol_Sfx"))
                    {
                        var k = f.Name == "vol_Master" ? "OPTION_VOLUME_MASTER" : "OPTION_VOLUME_SFX";
                        ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldstr, k), Instruction.Create(OpCodes.Call, Helper("GetVolume")));
                        Count($"ldsfld Optionshandler.{f.Name} -> __RoundsCompat.GetVolume(\"{k}\")"); touched = true;
                    }
                    else if (f.Name is "vol_Master" or "vol_Sfx" or "vol_Music" && f.DeclaringType.FullName == "Optionshandler")
                        throw new Exception($"unhandled {ins.OpCode} Optionshandler.{f.Name} in {md.FullName}");
                }
                else if (ins.Operand is MethodReference mr && (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && mr.DeclaringType.Scope.Name.StartsWith("Assembly-CSharp"))
                {
                    var dt = mr.DeclaringType.FullName;
                    if ((dt is "Damagable" or "HealthHandler" or "DamageOverTime") && mr.Name is "CallTakeDamage" or "TakeDamage" or "DoDamage" or "TakeDamageOverTime"
                        && mr.Resolve() == null)
                    {
                        var target = GT(dt).Methods.SingleOrDefault(x => x.Name == mr.Name && x.Parameters.Count == mr.Parameters.Count + 1
                            && x.Parameters.Last().ParameterType.FullName == "HealthHandler/DamageSource"
                            && x.Parameters.Take(mr.Parameters.Count).Select(p => p.ParameterType.FullName).SequenceEqual(mr.Parameters.Select(p => p.ParameterType.FullName)))
                            ?? throw new Exception($"no DamageSource overload for {mr.FullName}");
                        var op = ins.OpCode;
                        ReplaceWith(il, ins, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(op, M.ImportReference(target)));
                        Count($"{dt}.{mr.Name}(...) -> +DamageSource.Player arg"); touched = true;
                    }
                }
            }
            body.OptimizeMacros();
            if (!touched) continue;
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
            {
                tr.Scope = Ref("com.rlabrecque.steamworks.net"); Count($"TypeRef {tr.FullName}: [Assembly-CSharp-firstpass] -> [com.rlabrecque.steamworks.net]");
            }
            else if (an.Name == "UnityEngine.CoreModule" && tr.FullName == "UnityEngine.Input")
            {
                tr.Scope = Ref("UnityEngine.InputLegacyModule"); Count("TypeRef UnityEngine.Input: [UnityEngine.CoreModule] -> [UnityEngine.InputLegacyModule]");
            }
        }
    }

    // ------------------------------- per-mod -------------------------------
    public void GunUnblockable()
    {
        // HealthHandler.RPCA_SendTakeDamage gained a 5th param (DamageSource). PUN matches RPC targets by exact param count,
        // so the 4-arg RPC the postfix sends would be rejected ("wrong parameters"). Append boxed DamageSource.Player.
        var md = M.GetType("GunUnblockablePatch.HealthHandlerPatchCallTakeDamage")?.Methods.Single(x => x.Name == "Postfix")
                 ?? M.GetTypes().Single(t => t.Name == "HealthHandlerPatchCallTakeDamage").Methods.Single(x => x.Name == "Postfix");
        var body = md.Body; var il = body.GetILProcessor(); body.SimplifyMacros();
        var rpc = body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "RPC" && m.DeclaringType.Name == "PhotonView");
        var str = body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "RPCA_SendTakeDamage");
        var newarr = body.Instructions.Single(i => i.OpCode == OpCodes.Newarr && i.Offset > str.Offset && i.Offset < rpc.Offset);
        if (newarr.Previous.OpCode != OpCodes.Ldc_I4 || (int)newarr.Previous.Operand != 4) throw new Exception("unexpected RPC arg array");
        newarr.Previous.Operand = 5;
        var dmgSrc = M.ImportReference(GT("HealthHandler").NestedTypes.Single(t => t.Name == "DamageSource"));
        il.InsertBefore(rpc, Instruction.Create(OpCodes.Dup));
        il.InsertBefore(rpc, Instruction.Create(OpCodes.Ldc_I4, 4));
        il.InsertBefore(rpc, Instruction.Create(OpCodes.Ldc_I4, 0));
        il.InsertBefore(rpc, Instruction.Create(OpCodes.Box, dmgSrc));
        il.InsertBefore(rpc, Instruction.Create(OpCodes.Stelem_Ref));
        body.OptimizeMacros();
        Count("HealthHandlerPatchCallTakeDamage.Postfix: RPC(\"RPCA_SendTakeDamage\") args 4 -> 5 (+ (DamageSource)0)");
    }

    public void Grow()
    {
        // Harmony target TrickShot.Awake -> TrickShot.Start (Awake removed; trail lookup moved to Start).
        var patch = M.GetTypes().Single(t => t.Name == "TrickShotPatchAwake");
        RetargetHarmonyName(patch, "Awake", "Start");
        // Destroy() is deferred to end of frame; since we now run in Start (not Awake), TrickShot.Update would still run
        // once this frame with null move/projectileHit (NRE). Disable the component first: `__instance.enabled = false;`
        {
            var pre = patch.Methods.Single(m => m.Name == "Prefix");
            var il = pre.Body.GetILProcessor(); var first = pre.Body.Instructions[0];
            var setEnabled = M.ImportReference(
                Game.AssemblyResolver.Resolve(new AssemblyNameReference("UnityEngine.CoreModule", new Version(0, 0, 0, 0)))
                    .MainModule.GetType("UnityEngine.Behaviour").Methods.Single(m => m.Name == "set_enabled"));
            il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_0));
            il.InsertBefore(first, Instruction.Create(OpCodes.Callvirt, setEnabled));
            Count("TrickShotPatchAwake.Prefix: added `__instance.enabled = false` before Destroy (Start-time replacement)");
        }
        // FixedTrickShot.trail: ScaleTrailFromDamage -> IScaleTrailFromDamage (pooled bullets use ScaleTrailFromDamagePooled)
        var fts = M.GetTypes().Single(t => t.Name == "FixedTrickShot");
        var trail = fts.Fields.Single(f => f.Name == "trail");
        var iface = M.ImportReference(GT("FriendlyFoe.IScaleTrailFromDamage"));
        trail.FieldType = iface;
        var rescale = M.ImportReference(GT("FriendlyFoe.IScaleTrailFromDamage").Methods.Single(m => m.Name == "Rescale"));
        foreach (var md in fts.Methods.Where(m => m.HasBody))
        {
            var body = md.Body; var il = body.GetILProcessor(); body.SimplifyMacros();
            foreach (var ins in body.Instructions.ToList())
            {
                if (ins.Operand is GenericInstanceMethod gim && gim.Name == "GetComponentInChildren" && gim.GenericArguments[0].FullName == "ScaleTrailFromDamage")
                {
                    var g = new GenericInstanceMethod(gim.ElementMethod); g.GenericArguments.Add(iface); ins.Operand = g;
                    Count("FixedTrickShot.Awake: GetComponentInChildren<ScaleTrailFromDamage> -> <IScaleTrailFromDamage>");
                }
                else if (ins.Operand is MethodReference mr && mr.Name == "Rescale" && mr.DeclaringType.Name == "ScaleTrailFromDamage")
                { ins.OpCode = OpCodes.Callvirt; ins.Operand = rescale; Count("FixedTrickShot.FixedUpdate: Rescale() via IScaleTrailFromDamage"); }
                else if (ins.Operand is MethodReference oi && oi.Name == "op_Implicit" && oi.DeclaringType.FullName == "UnityEngine.Object"
                         && ins.Previous?.Operand is FieldReference pf && pf.Name == "trail")
                {
                    // `(bool)trail` -> `(trail = FindTrail(this, trail)) != null`  (lazy lookup: pooled trail is attached in BulletPoolInstancer.Start)
                    var ldThis = ins.Previous.Previous; // ldarg.0
                    if (!(ldThis.OpCode == OpCodes.Ldarg_0 || ldThis.OpCode == OpCodes.Ldarg && ldThis.Operand == body.ThisParameter)) throw new Exception("unexpected trail check shape");
                    // stack before ins: [this.trail]; we rebuild: ldarg0; ldarg0; ldarg0; ldfld trail; call FindTrail; stfld trail; ldarg0; ldfld trail; ldnull; cgt.un
                    var pfRef = (FieldReference)ins.Previous.Operand;
                    ReplaceWith(il, ins, Instruction.Create(OpCodes.Pop),
                        Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_0),
                        Instruction.Create(OpCodes.Ldfld, pfRef), Instruction.Create(OpCodes.Call, Helper("FindTrail")),
                        Instruction.Create(OpCodes.Stfld, pfRef),
                        Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, pfRef),
                        Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Cgt_Un));
                    Count("FixedTrickShot.FixedUpdate: (bool)trail -> lazy FindTrail + null check");
                }
            }
            body.OptimizeMacros();
        }
    }

    public void PerfImprovements()
    {
        // 1) ChangeColor.Start no longer exists (ChangeColor is an empty marker) -> drop the patch.
        Neutralize(M.GetTypes().Single(t => t.Name == "ChangeColorPatchStart"), "ChangeColor.Start was removed from the game (ChangeColor is now an empty marker)");
        // 2) ObjectsToSpawn.SpawnObject(8 args) postfix takes `ref GameObject[] __result`, but the method now returns
        //    PoolableWrapper[] of pooled instances. Its only effect was tagging results with the RemoveAfterPoint marker
        //    (whose cleanup destroys just the marker component), so it is functionally a no-op -> drop it.
        Neutralize(M.GetTypes().Single(t => t.Name == "ObjectsToSpawnPatchSpawnObject"), "__result type is now PoolableWrapper[] (pooled); patch only added a no-op marker");
        // 3) DynamicParticles.PlayBulletHit prefix: retarget SpawnObject, use wrapper.Instance, and remove the
        //    FixBulletHitParticleEffects branch that adds RemoveAfterSeconds (which would Destroy() pooled instances).
        var md = M.GetTypes().Single(t => t.Name == "DynamicParticlesPatchPlayBulletHit").Methods.Single(m => m.Name == "Prefix");
        var body = md.Body; var il = body.GetILProcessor(); body.SimplifyMacros();
        var pwT = GT("FriendlyFoe.PoolableWrapper");
        var pw = M.ImportReference(pwT); var pwArr = new ArrayType(pw);
        var inst = M.ImportReference(pwT.Fields.Single(f => f.Name == "Instance"));
        var newSpawn = M.ImportReference(GT("ObjectsToSpawn").Methods.Single(m => m.Name == "SpawnObject" && m.Parameters.Count == 8));
        var call = body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "SpawnObject");
        call.Operand = newSpawn;
        var arrLocal = (VariableDefinition)call.Next.Operand; // stloc V_2
        arrLocal.VariableType = pwArr;
        // branch region: from `call get_FixBulletHitParticleEffects` to the target of its brfalse
        var fixGet = body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "get_FixBulletHitParticleEffects");
        var br = fixGet.Next; while (br.OpCode.FlowControl != FlowControl.Cond_Branch) br = br.Next;
        var end = (Instruction)br.Operand;
        var region = new List<Instruction>(); for (var i = fixGet; i != end; i = i.Next) region.Add(i);
        var regionSet = region.ToHashSet();
        foreach (var i in body.Instructions.Where(i => !regionSet.Contains(i)))
            if (i.Operand is Instruction t && regionSet.Contains(t) || i.Operand is Instruction[] ts && ts.Any(regionSet.Contains))
                throw new Exception("branch into removed region");
        foreach (var i in region) il.Remove(i);
        Count($"DynamicParticlesPatchPlayBulletHit.Prefix: removed FixBulletHitParticleEffects branch ({region.Count} instrs; it Destroy()ed pooled hit effects)");
        // element .transform -> .Instance.transform
        foreach (var i in body.Instructions.ToList())
            if (i.OpCode == OpCodes.Ldelem_Ref && i.Previous?.Previous?.Operand == arrLocal && i.Next.Operand is MethodReference g && g.Name == "get_transform" && g.DeclaringType.FullName == "UnityEngine.GameObject")
            { il.InsertAfter(i, Instruction.Create(OpCodes.Ldfld, inst)); Count("DynamicParticlesPatchPlayBulletHit.Prefix: array[k].transform -> array[k].Instance.transform"); }
        // drop now-unused GameObject[] locals' types (dead) -> retype to keep metadata consistent
        foreach (var v in body.Variables.Where(v => v.VariableType.FullName == "UnityEngine.GameObject[]")) v.VariableType = pwArr;
        body.OptimizeMacros();
        Log.Add("NOTE: PerformanceImprovements 'Fix persistence issues with BulletHit particle effects' toggle now only affects ProjectileCollision.Die sparks (non-pooled Instantiate); pooled bullet-hit effects are managed by the game's RemoveAfterSecondsPooled.");
    }

    void Neutralize(TypeDefinition t, string why)
    {
        int n = 0;
        foreach (var holder in new ICustomAttributeProvider[] { t }.Concat(t.Methods))
            foreach (var ca in holder.CustomAttributes.Where(a => a.AttributeType.Namespace == "HarmonyLib" && a.AttributeType.Name.StartsWith("Harmony")).ToList())
            { holder.CustomAttributes.Remove(ca); n++; }
        Count($"neutralized Harmony patch class {t.FullName} (removed {n} Harmony attributes; PatchAll skips it): {why}");
    }

    void RetargetHarmonyName(TypeDefinition t, string from, string to)
    {
        foreach (var ca in t.CustomAttributes.Where(a => a.AttributeType.Name == "HarmonyPatch"))
            for (int i = 0; i < ca.ConstructorArguments.Count; i++)
                if (ca.ConstructorArguments[i].Value is string s && s == from)
                {
                    ca.ConstructorArguments[i] = new CustomAttributeArgument(ca.ConstructorArguments[i].Type, to);
                    Count($"[HarmonyPatch] {t.Name}: \"{from}\" -> \"{to}\"");
                }
    }

    public void Finish()
    {
        foreach (var kv in counts) Log.Add($"{kv.Key}  x{kv.Value}");
        if (counts.Count == 0) Log.Add("(no changes)");
    }
}
