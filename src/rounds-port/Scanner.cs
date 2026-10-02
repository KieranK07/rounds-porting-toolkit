using Mono.Cecil;
using Mono.Cecil.Cil;

enum Fix { Auto, Review, Manual }

// Auto: `rounds-port fix` rewrites it. Review: fix rewrites it, but check the behaviour. Manual: you change the source.
sealed record Issue(Fix Fix, string Kind, string What, string Detail);

// Finds everything in a mod that no longer lines up with the current game: missing types and members, Harmony
// targets and injected parameters, reflection by name, broken overrides, and known behaviour changes.
sealed class Scanner(Game game)
{
    readonly MapResolver resolver = game.Resolver;
    readonly MetadataResolver md = new(game.Resolver);

    // Other mods this one uses that aren't installed (e.g. ModdingUtils on a plain game): what it uses from them can't
    // be checked. Not a porting problem, so it's reported once instead of as an issue per type.
    public readonly SortedSet<string> Unchecked = new(StringComparer.OrdinalIgnoreCase);

    bool MissingModDependency(TypeReference t)
    {
        if (TypeProblem(t) is not string why || !why.StartsWith("assembly ")) return false;
        var name = Scope(t);
        if (name.StartsWith("Sirenix.") || name is "UnboundLib" or "MMHOOK_Assembly-CSharp" or "Assembly-CSharp-firstpass"
            || File.Exists(Path.Combine(game.Managed, name + ".dll"))) return false;   // the game's or a known change
        Unchecked.Add(name);
        return true;
    }

    public List<Issue> Scan(ModuleDefinition module)
    {
        var issues = new List<Issue>();
        void Add(Issue i) { if (!issues.Contains(i)) issues.Add(i); }
        Unchecked.Clear();

        foreach (var tr in module.GetTypeReferences())
            if (!MissingModDependency(tr) && TypeProblem(tr) is string why) Add(Known.Type(tr, Scope(tr), why));

        foreach (var mr in module.GetMemberReferences())
        {
            if (mr.DeclaringType is ArrayType) continue;
            string? why = null;
            TypeDefinition? dt = null;
            try
            {
                dt = Resolve(mr.DeclaringType.GetElementType());
                if (dt == null) continue;   // already reported as a missing type
                IMemberDefinition? res = mr switch { MethodReference m => ResolveM(m), FieldReference f => ResolveF(f), _ => null };
                if (res == null) why = "not found" + Hints(dt, mr.Name);
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
            if (why != null) Add(Known.Member(mr, dt!, why));
        }

        foreach (var t in AllTypes(module))
        {
            Inheritance(module, t, Add);
            foreach (var (info, patch) in HarmonyPatches(t))
            {
                if (info.Type != null && MissingModDependency(info.Type)) continue;
                if (TargetProblem(info) is string p) { Add(Known.HarmonyTarget(info, p)); continue; }
                foreach (var pm in patch)
                    foreach (var (param, problem, fixTo) in PatchParamProblems(info, pm))
                        Add(new Issue(fixTo != null ? Fix.Review : Fix.Manual, "harmony param", $"{t.FullName}::{pm.Name} ({param}) -> {info}",
                            problem + (fixTo != null ? $"; fix renames it to {fixTo}" : "")));
            }
            foreach (var m in t.Methods.Where(m => m.HasBody))
            {
                foreach (var s in ReflectSites(m))
                    if (s.Problem != null)
                        Add(new Issue(s.FixTo != null || s.Unsure ? Fix.Review : Fix.Manual, "reflection", $"{t.FullName}::{m.Name} {s.Call}(\"{s.Name}\")",
                            s.Problem + (s.FixTo != null ? $"; fix changes it to \"{s.FixTo}\"" : "")));
                foreach (var r in RpcSites(m))
                    if (!r.Targets.Any(x => x.Parameters.Count == r.Args))
                        Add(new Issue(OnlyMissingDamageSource(r) ? Fix.Auto : Fix.Manual, "rpc", $"{t.FullName}::{m.Name} RPC(\"{r.Name}\") with {r.Args} argument{(r.Args == 1 ? "" : "s")}",
                            "the game's version takes " + string.Join(" | ", r.Targets.Select(x => "(" + string.Join(", ", x.Parameters.Select(p => p.ParameterType.Name)) + ")"))
                            + ", and PUN drops RPCs with the wrong argument count" + (OnlyMissingDamageSource(r) ? "; fix appends DamageSource.Player" : "")));
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.Operand is FieldReference f && f.Name == "cardName" && f.DeclaringType.FullName == "CardInfo" && IsGame(f.DeclaringType))
                        Add(ins.OpCode.Code == Code.Stfld
                            ? new Issue(Fix.Manual, "behaviour", "CardInfo.cardName (write)", "private now, and the game reads names from localization. Set the card's title through UnboundLib's CustomCard instead")
                            : new Issue(Fix.Auto, "behaviour", "CardInfo.cardName (read)", "private now, and empty for UnboundLib 4 cards (names moved to localization). fix reads it through a helper that falls back to the localized key, CardName, then the GameObject name"));
                    if (ins.OpCode.Code == Code.Ldstr && (string)ins.Operand == "GetRanomCard")
                        Add(new Issue(Fix.Auto, "reflection", $"{t.FullName}::{m.Name} \"GetRanomCard\"", "the typo was fixed: CardChoice.GetRandomCard"));
                }
            }
        }
        return issues;
    }

    // ---------------------------------------------------------------- inheritance
    void Inheritance(ModuleDefinition module, TypeDefinition t, Action<Issue> add)
    {
        if (t.BaseType == null || t.IsInterface) return;
        var chain = new List<TypeDefinition>();
        for (var b = Resolve(t.BaseType); b != null; b = b.BaseType == null ? null : Resolve(b.BaseType)) chain.Add(b);
        if (!chain.Any(b => b.Module.Assembly.Name.Name != module.Assembly.Name.Name && !b.Module.Assembly.Name.Name.StartsWith("UnityEngine") && b.Module.Assembly.Name.Name != "mscorlib")) return;
        var all = new List<TypeDefinition> { t }; all.AddRange(chain);
        if (!t.IsAbstract)
            foreach (var b in chain)
                foreach (var am in b.Methods.Where(x => x.IsAbstract))
                    if (!all.TakeWhile(x => x != b).Any(x => x.Methods.Any(m => m.Name == am.Name && !m.IsAbstract && SigEq(m, am))))
                        add(new Issue(Fix.Manual, "override", $"{t.FullName} doesn't implement {am.FullName}",
                            "TypeLoadException when the type loads: the base method changed (e.g. the damage methods gained a HealthHandler.DamageSource parameter). Update your override"));
        foreach (var m in t.Methods.Where(m => m.IsVirtual && !m.IsNewSlot && !m.IsAbstract))
        {
            bool overrides = chain.Any(b => b.Methods.Any(x => x.IsVirtual && x.Name == m.Name && SigEq(m, x)));
            if (!overrides && chain.Any(b => IsGame(b) && b.Methods.Any(x => x.Name == m.Name)))
                add(new Issue(Fix.Manual, "override", $"{t.FullName}::{m.Name}",
                    "no longer overrides anything; the game now has: " + string.Join(" | ", chain.SelectMany(b => b.Methods.Where(x => x.Name == m.Name)).Select(x => x.FullName))));
        }
    }

    // ---------------------------------------------------------------- Harmony
    // Every patch on t: the merged [HarmonyPatch] info, and the Prefix/Postfix/Finalizer methods it applies to.
    public IEnumerable<(HarmonyInfo info, List<MethodDefinition> methods)> HarmonyPatches(TypeDefinition t)
    {
        if (t.Methods.Any(m => m.Name is "TargetMethod" or "TargetMethods")) yield break;   // dynamic targets
        var classAttr = HarmonyInfo.From(t.CustomAttributes);
        var own = t.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPatch")).ToList();
        static bool IsPatchMethod(MethodDefinition m) => m.Name is "Prefix" or "Postfix" or "Finalizer"
            || m.CustomAttributes.Any(a => a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyFinalizer");
        if (classAttr != null)
        {
            if (own.Count == 0) yield return (classAttr, t.Methods.Where(IsPatchMethod).ToList());
            foreach (var pm in own) yield return (HarmonyInfo.Merge(classAttr, HarmonyInfo.From(pm.CustomAttributes)!), new() { pm });
        }
        else
            foreach (var pm in own) yield return (HarmonyInfo.From(pm.CustomAttributes)!, new() { pm });
    }

    public TypeDefinition? TargetType(HarmonyInfo h) => h.Type != null ? Resolve(h.Type) : h.TypeName != null ? FindType(h.TypeName) : null;

    string? TargetProblem(HarmonyInfo h)
    {
        if (h.Type == null && h.TypeName == null) return null;
        var td = TargetType(h);
        if (td == null) return "target type not found";
        var mt = h.MethodType ?? 0;   // 0 Normal, 1 Getter, 2 Setter, 3 Constructor, 4 StaticConstructor
        if (mt == 3) return td.Methods.Any(m => m.IsConstructor && !m.IsStatic && ArgsMatch(m, h.ArgTypes)) ? null : "constructor with those arguments not found";
        if (mt == 4) return td.Methods.Any(m => m.IsConstructor && m.IsStatic) ? null : "static constructor not found";
        if (h.Method == null) return null;
        if (mt == 1) return HasMember(td, h.Method, "Getter") ? null : "property getter not found" + Hints(td, h.Method);
        if (mt == 2) return HasMember(td, h.Method, "Setter") ? null : "property setter not found" + Hints(td, h.Method);
        for (var cur = td; cur != null; cur = cur.BaseType == null ? null : Resolve(cur.BaseType))
        {
            var cands = cur.Methods.Where(m => m.Name == h.Method).ToList();
            if (cands.Count == 0) continue;
            if (h.ArgTypes == null)
                return cands.Count > 1 && !cands.Any(c => c.Parameters.Count == 0)
                    ? "ambiguous: Harmony throws AmbiguousMatchException. Add argumentTypes, one of: " + string.Join(" | ", cands.Select(c => c.FullName))
                    : null;
            if (cands.Any(m => ArgsMatch(m, h.ArgTypes))) return null;
            return "no overload with those argumentTypes; the game has: " + string.Join(" | ", cands.Select(c => c.FullName));
        }
        return "method not found" + Hints(td, h.Method);
    }

    // (parameter, problem, rename-to or null)
    public IEnumerable<(string param, string problem, string? fixTo)> PatchParamProblems(HarmonyInfo h, MethodDefinition patch)
    {
        var td = TargetType(h);
        if (td == null || patch.Name == "Transpiler" || patch.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyTranspiler")) yield break;
        var mt = h.MethodType ?? 0;
        var cands = new List<MethodDefinition>();
        for (var cur = td; cur != null && cands.Count == 0; cur = cur.BaseType == null ? null : Resolve(cur.BaseType))
        {
            if (mt == 3) cands.AddRange(cur.Methods.Where(m => m.IsConstructor && !m.IsStatic && ArgsMatch(m, h.ArgTypes)));
            else if (mt == 1) cands.AddRange(cur.Properties.Where(p => p.Name == h.Method && p.GetMethod != null).Select(p => p.GetMethod));
            else if (mt == 2) cands.AddRange(cur.Properties.Where(p => p.Name == h.Method && p.SetMethod != null).Select(p => p.SetMethod));
            else if (h.Method != null) cands.AddRange(cur.Methods.Where(m => m.Name == h.Method && ArgsMatch(m, h.ArgTypes)));
        }
        if (cands.Count != 1) yield break;   // ambiguity is reported as a target problem
        var target = cands[0];
        bool passThrough = patch.ReturnType.FullName is not ("System.Void" or "System.Boolean") && patch.Parameters.Count > 0
                           && patch.Parameters[0].ParameterType.FullName == patch.ReturnType.FullName;
        foreach (var p in patch.Parameters.Skip(passThrough ? 1 : 0))
        {
            var n = p.Name;
            if (p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyArgument")) continue;
            if (n.StartsWith("___"))
            {
                var field = n[3..];
                if (FieldOn(td, field) != null) continue;
                yield return (n, $"field '{field}' isn't on {td.Name}" + Hints(td, field), FieldOn(td, "m_" + field) != null ? "___m_" + field : null);
                continue;
            }
            if (n is "__instance" or "__result" or "__state" or "__originalMethod" or "__args" or "__runOriginal" or "__exception") continue;
            if (n.StartsWith("__") && int.TryParse(n[2..], out _)) continue;
            var tp = target.Parameters.FirstOrDefault(x => x.Name == n);
            if (tp == null) { yield return (n, "the target has no parameter with that name; it has (" + string.Join(", ", target.Parameters.Select(x => x.ParameterType.Name + " " + x.Name)) + ")", null); continue; }
            var a = p.ParameterType is ByReferenceType br ? br.ElementType : p.ParameterType;
            var b = tp.ParameterType is ByReferenceType br2 ? br2.ElementType : tp.ParameterType;
            if (a.FullName != b.FullName && a.FullName != "System.Object") yield return (n, $"type {a.Name} doesn't match the target's {b.Name}", null);
        }
    }

    // ---------------------------------------------------------------- Photon RPCs to game methods
    // photonView.RPC("RPCA_X", target, a, b, ...): PUN drops the call when the argument count doesn't match RPCA_X.
    public sealed record RpcSite(Instruction Newarr, Instruction Call, string Name, int Args, List<MethodDefinition> Targets);

    public IEnumerable<RpcSite> RpcSites(MethodDefinition m)
    {
        foreach (var call in m.Body.Instructions.Where(i => i.OpCode.Code is Code.Call or Code.Callvirt && i.Operand is MethodReference r && r.Name == "RPC" && r.DeclaringType.Name == "PhotonView"))
        {
            Instruction? str = null, newarr = null;
            for (var x = call.Previous; x != null; x = x.Previous)
            {
                if (x.OpCode.Code == Code.Newarr) newarr = x;
                if (x.OpCode.Code == Code.Ldstr) { str = x; break; }
            }
            if (str == null || newarr == null || IntValue(newarr.Previous) is not int n) continue;
            var name = (string)str.Operand;
            var targets = AllTypes(game.AssemblyCSharp).SelectMany(t => t.Methods).Where(x => x.Name == name).ToList();
            if (targets.Count > 0) yield return new RpcSite(newarr, call, name, n, targets);
        }
    }

    public static int? IntValue(Instruction? i) => i?.OpCode.Code switch
    {
        Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4,
        Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
        Code.Ldc_I4_S => (sbyte)i.Operand, Code.Ldc_I4 => (int)i.Operand, _ => null
    };

    // The game added a trailing HealthHandler.DamageSource to some RPCs; those `fix` can extend with DamageSource.Player.
    public static bool OnlyMissingDamageSource(RpcSite s) =>
        s.Targets.Count == 1 && s.Targets[0].Parameters.Count == s.Args + 1 && s.Targets[0].Parameters[^1].ParameterType.FullName == "HealthHandler/DamageSource";

    // ---------------------------------------------------------------- reflection by name
    public sealed record ReflectSite(Instruction Ldstr, string Call, string Name, string? Problem, string? FixTo) { public bool Unsure { get; init; } }

    public IEnumerable<ReflectSite> ReflectSites(MethodDefinition m)
    {
        var ins = m.Body.Instructions;
        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].OpCode.Code is not (Code.Call or Code.Callvirt) || ins[i].Operand is not MethodReference call) continue;
            var dn = call.DeclaringType.Name; var n = call.Name;
            bool accessTools = dn == "AccessTools" && n is "Method" or "Field" or "Property" or "PropertyGetter" or "PropertySetter"
                or "DeclaredMethod" or "DeclaredField" or "DeclaredProperty" or "DeclaredPropertyGetter" or "DeclaredPropertySetter" or "FieldRefAccess" or "StaticFieldRefAccess";
            bool reflect = call.DeclaringType.FullName == "System.Type" && n is "GetMethod" or "GetField" or "GetProperty";
            bool traverse = dn == "Traverse" && n is "Field" or "Method" or "Property";
            if (!(accessTools || reflect || traverse)) continue;
            var ld = FindLdstr(ins, i, 8);
            if (ld == null) continue;
            var name = (string)ld.Operand;
            string kind = n.Contains("Field") ? "Field" : n.Contains("Propert") ? "Property" : "Method";
            string where = $"{dn}.{n}";
            if (dn == "AccessTools" && n == "Method" && name.Contains(':'))
            {
                var parts = name.Split(':');
                var td0 = FindType(parts[0]);
                yield return new ReflectSite(ld, where, name, td0 == null ? "type not found" : HasMember(td0, parts[1], "Method") ? null : "method not found" + Hints(td0, parts[1]), null);
                continue;
            }
            TypeReference? tref = call is GenericInstanceMethod gim && n.Contains("FieldRefAccess") ? gim.GenericArguments[0] : FindLdtoken(ins, ins.IndexOf(ld), 6);
            if (tref == null)
            {
                if (!AnyGameTypeHas(name, kind) && !AllTypes(m.Module).Any(t => Has(t, name, kind)))
                    yield return GameTypeWith("m_" + name, "Field") is string owner
                        ? new ReflectSite(ld, where, name, $"no {kind.ToLower()} named '{name}' anywhere; {owner} has m_{name} (renamed?)", null)
                        : new ReflectSite(ld, where, name, $"no game or mod type has a {kind.ToLower()} named '{name}'. The target type isn't known statically: if it's another mod's type this is fine, if it's the game's it was removed", null) { Unsure = true };
                continue;
            }
            if (tref.IsGenericParameter) continue;   // typeof(T): only known at runtime
            var td = Resolve(tref);
            if (td == null) { yield return new ReflectSite(ld, where, name, $"type {tref.FullName} not found", null); continue; }
            if (HasMember(td, name, kind)) continue;
            string? fixTo = kind == "Field" && HasMember(td, "m_" + name, "Field") ? "m_" + name : null;
            yield return new ReflectSite(ld, where, name, $"{kind.ToLower()} {td.Name}.{name} not found" + Hints(td, name), fixTo);
        }
    }

    // ---------------------------------------------------------------- helpers
    public TypeDefinition? Resolve(TypeReference t) { try { return t.Resolve(); } catch { return null; } }
    MethodDefinition? ResolveM(MethodReference m) { try { return md.Resolve(m); } catch { return null; } }
    FieldDefinition? ResolveF(FieldReference f) { try { return md.Resolve(f); } catch { return null; } }
    static bool IsGame(TypeReference t) { while (t.DeclaringType != null) t = t.DeclaringType; return t.Scope?.Name?.StartsWith("Assembly-CSharp") == true; }
    static bool IsGame(TypeDefinition t) => t.Module.Assembly.Name.Name == "Assembly-CSharp";

    static string Scope(TypeReference t) { while (t.DeclaringType != null) t = t.DeclaringType; return t.Scope?.Name ?? "?"; }

    string? TypeProblem(TypeReference t)
    {
        try { return t.Resolve() == null ? "not in " + Scope(t) : null; }
        catch (AssemblyResolutionException e) { return "assembly " + e.AssemblyReference.Name + " not found"; }
        catch (Exception e) { return e.Message; }
    }

    FieldDefinition? FieldOn(TypeDefinition td, string name)
    {
        for (var cur = td; cur != null; cur = cur.BaseType == null ? null : Resolve(cur.BaseType))
            if (cur.Fields.FirstOrDefault(f => f.Name == name) is FieldDefinition f) return f;
        return null;
    }

    // " (did you mean: ...)" from same-name / m_-prefixed / property members on the type and its game base types
    public string Hints(TypeDefinition td, string name)
    {
        var hits = new List<string>();
        for (var cur = td; cur != null; cur = cur.BaseType == null ? null : Resolve(cur.BaseType))
        {
            hits.AddRange(cur.Methods.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || x.Name == "get_" + name).Select(x => x.FullName));
            hits.AddRange(cur.Fields.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || x.Name.Equals("m_" + name, StringComparison.OrdinalIgnoreCase)).Select(x => "field " + x.FullName));
            hits.AddRange(cur.Properties.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => "property " + x.FullName));
            if (cur.Module.Assembly.Name.Name != td.Module.Assembly.Name.Name) break;
        }
        return hits.Count == 0 ? "" : " (the game has: " + string.Join("; ", hits.Distinct()) + ")";
    }

    bool HasMember(TypeDefinition td, string name, string kind)
    {
        for (var cur = td; cur != null; cur = cur.BaseType == null ? null : Resolve(cur.BaseType))
        {
            if (Has(cur, name, kind)) return true;
            if (kind == "Getter" && cur.Properties.Any(p => p.Name == name && p.GetMethod != null)) return true;
            if (kind == "Setter" && cur.Properties.Any(p => p.Name == name && p.SetMethod != null)) return true;
        }
        return false;
    }
    static bool Has(TypeDefinition t, string name, string kind) =>
        kind == "Field" && t.Fields.Any(f => f.Name == name) || kind == "Property" && t.Properties.Any(p => p.Name == name) || kind == "Method" && t.Methods.Any(p => p.Name == name);

    public static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        foreach (var t in m.Types) { yield return t; foreach (var n in Nested(t)) yield return n; }
        static IEnumerable<TypeDefinition> Nested(TypeDefinition t) { foreach (var n in t.NestedTypes) { yield return n; foreach (var x in Nested(n)) yield return x; } }
    }

    static Instruction? FindLdstr(Mono.Collections.Generic.Collection<Instruction> ins, int i, int back)
    {
        for (int j = i - 1; j >= 0 && j >= i - back; j--) if (ins[j].OpCode.Code == Code.Ldstr) return ins[j];
        return null;
    }
    static TypeReference? FindLdtoken(Mono.Collections.Generic.Collection<Instruction> ins, int i, int back)
    {
        for (int j = i - 1; j >= 0 && j >= i - back; j--)
        {
            if (ins[j].OpCode.Code == Code.Ldtoken && ins[j].Operand is TypeReference tr) return tr;
            if (ins[j].OpCode.Code is Code.Call or Code.Callvirt && ins[j].Operand is MethodReference mr && mr.Name is "GetMethod" or "GetField" or "GetProperty" or "Method" or "Field" or "Property") return null;
        }
        return null;
    }
    TypeDefinition? FindType(string n)
    {
        foreach (var a in resolver.Loaded()) if (a.MainModule.GetType(n) is TypeDefinition t) return t;
        return game.AssemblyCSharp.Types.FirstOrDefault(t => t.Name == n);
    }
    string? GameTypeWith(string name, string kind) => AllTypes(game.AssemblyCSharp).FirstOrDefault(t => Has(t, name, kind))?.FullName;
    bool AnyGameTypeHas(string name, string kind)
    {
        foreach (var an in new[] { "Assembly-CSharp", "UnityEngine.CoreModule", "UnityEngine.PhysicsModule", "UnityEngine.Physics2DModule", "UnityEngine.UI", "Unity.TextMeshPro", "PhotonUnityNetworking", "PhotonRealtime" })
            if (resolver.Get(an) is AssemblyDefinition a && AllTypes(a.MainModule).Any(t => Has(t, name, kind))) return true;
        return false;
    }
    // Types that mention a generic parameter (T, CardDetails<T>...) match anything: an override in a closed subclass
    // of a generic base has the substituted type.
    static bool SigEq(MethodDefinition a, MethodDefinition b)
    {
        static bool Same(TypeReference x, TypeReference y) => x.FullName == y.FullName || x.ContainsGenericParameter || y.ContainsGenericParameter;
        if (a.Parameters.Count != b.Parameters.Count) return false;
        for (int i = 0; i < a.Parameters.Count; i++) if (!Same(a.Parameters[i].ParameterType, b.Parameters[i].ParameterType)) return false;
        return Same(a.ReturnType, b.ReturnType);
    }
    static bool ArgsMatch(MethodDefinition m, List<TypeReference>? args)
    {
        if (args == null) return true;
        if (m.Parameters.Count != args.Count) return false;
        for (int i = 0; i < args.Count; i++)
        {
            var pt = m.Parameters[i].ParameterType;
            string pn = pt is ByReferenceType br ? br.ElementType.FullName : pt.FullName;
            if (pn != args[i].FullName && pt.Name.TrimEnd('&') != args[i].Name) return false;
        }
        return true;
    }
}

sealed class HarmonyInfo
{
    public TypeReference? Type; public string? TypeName; public string? Method; public int? MethodType; public List<TypeReference>? ArgTypes;
    public override string ToString() => $"{Type?.FullName ?? TypeName ?? "?"}::{Method ?? "?"}"
        + (MethodType is int mt && mt != 0 ? $" [MethodType {mt}]" : "") + (ArgTypes != null ? "(" + string.Join(",", ArgTypes.Select(a => a.Name)) + ")" : "");
    public static HarmonyInfo Merge(HarmonyInfo a, HarmonyInfo b) => new()
    { Type = b.Type ?? a.Type, TypeName = b.TypeName ?? a.TypeName, Method = b.Method ?? a.Method, MethodType = b.MethodType ?? a.MethodType, ArgTypes = b.ArgTypes ?? a.ArgTypes };
    public static HarmonyInfo? From(IEnumerable<CustomAttribute> attrs)
    {
        HarmonyInfo? h = null;
        foreach (var a in attrs.Where(a => a.AttributeType.Name == "HarmonyPatch"))
        {
            h ??= new HarmonyInfo();
            bool firstString = true;
            foreach (var arg in a.ConstructorArguments)
            {
                var v = arg.Value;
                if (v is TypeReference tr) h.Type = tr;
                else if (v is string s)
                {
                    if (a.ConstructorArguments.Count >= 2 && a.ConstructorArguments[0].Value is string && firstString && a.ConstructorArguments[1].Value is string) h.TypeName = s;
                    else h.Method = s;
                    firstString = false;
                }
                else if (arg.Type.Name == "MethodType") h.MethodType = Convert.ToInt32(v);
                else if (v is CustomAttributeArgument[] arr && arr.Length > 0 && arr[0].Value is TypeReference) h.ArgTypes = arr.Select(x => (TypeReference)x.Value).ToList();
                else if (v is CustomAttributeArgument[] arr2 && arr2.Length == 0 && arg.Type.GetElementType().FullName == "System.Type") h.ArgTypes = new();
            }
        }
        return h;
    }
}
