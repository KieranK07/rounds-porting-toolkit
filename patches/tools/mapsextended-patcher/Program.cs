using Mono.Cecil; using Mono.Cecil.Cil; using Mono.Cecil.Pdb; using Mono.Cecil.Mdb;
// patcher maps|wwmo <in.dll> <out.dll> [pdbIn]
var mode = args[0]; var inDll = args[1]; var outDll = args[2];
var pdbIn = args.Length > 3 ? args[3] : null;
var res = new DefaultAssemblyResolver();
var G = Path.Combine(Environment.GetEnvironmentVariable("HOME"), "Library/Application Support/Steam/steamapps/common/ROUNDS");
res.AddSearchDirectory(Path.Combine(G, "ROUNDS.app/Contents/Resources/Data/Managed"));
res.AddSearchDirectory(Path.Combine(G, "BepInEx/core"));
foreach (var d in (Environment.GetEnvironmentVariable("EXTRA_DIRS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)) res.AddSearchDirectory(d);
var rp = new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true, AssemblyResolver = res };
if (pdbIn != null) { rp.ReadSymbols = true; rp.SymbolReaderProvider = new NativePdbReaderProvider(); rp.SymbolStream = File.OpenRead(pdbIn); }
var mod = ModuleDefinition.ReadModule(inDll, rp);
int changes = 0;
void Log(string s) { Console.WriteLine("  " + s); changes++; }
var asmCs = mod.AssemblyReferences.First(a => a.Name == "Assembly-CSharp");

IEnumerable<MethodDefinition> AllMethods() => mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody);

if (mode == "maps")
{
    // 1. Player::teamID (field, removed) -> Player::get_TeamID()
    foreach (var m in AllMethods())
        foreach (var ins in m.Body.Instructions.ToList())
            if ((ins.OpCode == OpCodes.Ldfld) && ins.Operand is FieldReference fr && fr.Name == "teamID" && fr.DeclaringType.FullName == "Player")
            {
                var getter = new MethodReference("get_TeamID", mod.TypeSystem.Int32, fr.DeclaringType) { HasThis = true };
                ins.OpCode = OpCodes.Callvirt; ins.Operand = getter;
                Log($"{m.FullName}: ldfld Player::teamID -> callvirt Player::get_TeamID()");
            }
            else if (ins.Operand is FieldReference fr2 && fr2.Name == "teamID" && fr2.DeclaringType.FullName == "Player")
                throw new Exception($"unexpected {ins.OpCode} on Player::teamID in {m.FullName}");

    // 2. CardBarPatch_OnHover_Patch: disambiguate overload + rename injected field param
    var t = mod.GetType("MapsExt.CardBarPatch_OnHover_Patch") ?? throw new Exception("CardBarPatch_OnHover_Patch not found");
    var ca = t.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
    if (ca.ConstructorArguments.Count != 2) throw new Exception("unexpected HarmonyPatch ctor");
    var sysType = mod.ImportReference(typeof(Type)); // mscorlib System.Type
    sysType = new TypeReference("System", "Type", mod, mod.TypeSystem.CoreLibrary);
    var ctor = new MethodReference(".ctor", mod.TypeSystem.Void, ca.AttributeType) { HasThis = true };
    ctor.Parameters.Add(new ParameterDefinition(sysType));
    ctor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
    ctor.Parameters.Add(new ParameterDefinition(new ArrayType(sysType)));
    var cardBarButton = new TypeReference("", "CardBarButton", mod, asmCs);
    var nca = new CustomAttribute(ctor);
    nca.ConstructorArguments.Add(new CustomAttributeArgument(sysType, ca.ConstructorArguments[0].Value));
    nca.ConstructorArguments.Add(new CustomAttributeArgument(mod.TypeSystem.String, ca.ConstructorArguments[1].Value));
    nca.ConstructorArguments.Add(new CustomAttributeArgument(new ArrayType(sysType), new[] { new CustomAttributeArgument(sysType, cardBarButton) }));
    t.CustomAttributes.Remove(ca); t.CustomAttributes.Add(nca);
    Log("CardBarPatch_OnHover_Patch: [HarmonyPatch(typeof(CardBar), \"OnHover\")] -> [HarmonyPatch(typeof(CardBar), \"OnHover\", new[]{ typeof(CardBarButton) })]");
    var post = t.Methods.Single(m => m.Name == "Postfix");
    var p = post.Parameters.Single(x => x.Name == "___currentCard");
    p.Name = "___m_currentCard";
    Log("CardBarPatch_OnHover_Patch.Postfix: param ___currentCard -> ___m_currentCard");
}
else if (mode == "wwmo")
{
    // Insert trailing HealthHandler.DamageSource.Player (0) arg
    var healthHandler = new TypeReference("", "HealthHandler", mod, asmCs);
    var damageSource = new TypeReference("", "DamageSource", mod, asmCs) { DeclaringType = healthHandler, IsValueType = true };
    foreach (var m in AllMethods())
    {
        var il = m.Body.GetILProcessor();
        foreach (var ins in m.Body.Instructions.ToList())
        {
            if (!(ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) || ins.Operand is not MethodReference mr) continue;
            var dt = mr.DeclaringType.FullName;
            bool hit = (dt == "HealthHandler" && mr.Name == "TakeDamageOverTime" && mr.Parameters.Count == 8)
                    || (dt == "Damagable" && mr.Name == "CallTakeDamage" && mr.Parameters.Count == 5);
            if (!hit) continue;
            var nmr = new MethodReference(mr.Name, mr.ReturnType, mr.DeclaringType) { HasThis = mr.HasThis, ExplicitThis = mr.ExplicitThis, CallingConvention = mr.CallingConvention };
            foreach (var pp in mr.Parameters) nmr.Parameters.Add(new ParameterDefinition(pp.ParameterType));
            nmr.Parameters.Add(new ParameterDefinition(damageSource));
            var op = ins.OpCode;
            // turn the existing instruction into ldc.i4.0 (keeps any branch targets valid), then append the call
            ins.OpCode = OpCodes.Ldc_I4_0; ins.Operand = null;
            il.InsertAfter(ins, il.Create(op, nmr));
            Log($"{m.FullName}: {mr.FullName} -> +DamageSource.Player");
        }
    }
}
Console.WriteLine($"{changes} change(s)");
var wp = new WriterParameters();
if (pdbIn != null) { wp.WriteSymbols = true; wp.SymbolWriterProvider = new PortablePdbWriterProvider(); }
mod.Write(outDll, wp);
if (pdbIn != null)
{
    // also emit a matching .mdb next to it
    var tmp = Path.Combine(Path.GetTempPath(), "mdbgen-" + Guid.NewGuid()); Directory.CreateDirectory(tmp);
    var tmpDll = Path.Combine(tmp, Path.GetFileName(outDll));
    mod.Write(tmpDll, new WriterParameters { WriteSymbols = true, SymbolWriterProvider = new MdbWriterProvider() });
    File.Copy(tmpDll + ".mdb", outDll + ".mdb", true);
    if (!File.ReadAllBytes(tmpDll).SequenceEqual(File.ReadAllBytes(outDll))) Console.WriteLine("  note: mdb-pass dll differs (debug dir); using first");
    Directory.Delete(tmp, true);
}
