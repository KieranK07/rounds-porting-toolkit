// Source of the GunChargePatch 0.0.4 patch (docs/PATCHLOG-guncharge.md). Build: dotnet build -c Release
// GunChargePatch 0.0.4: its FireBurst transpiler copied three instructions by their index in the old game's
// Gun.<FireBurst>d__93.MoveNext (val[24] ldarg.0, val[25] ldfld charge, val[293] box float32). The current game
// added a loop at the top of that method, so the indexes point elsewhere and the patched IL is invalid.
// This builds those three instructions directly instead.   patcher <in.dll> <out.dll>
using Mono.Cecil; using Mono.Cecil.Cil;
var m = ModuleDefinition.ReadModule(args[0]);
var t = m.GetType("GunChargePatch.Patches.Gun_PatchTranspiler");
var tr = t.Methods.Single(x => x.Name == "Transpiler");
var nested = t.Methods.Single(x => x.Name == "GetNestedIDoBlockTransitionType");
var body = tr.Body; var il = body.GetILProcessor(); var ins = body.Instructions;

FieldReference OpCodeField(string name) => (FieldReference)ins.First(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f && f.DeclaringType.Name == "OpCodes" && f.Name == name).Operand;
MethodReference fieldMethod = (MethodReference)ins.First(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.DeclaringType.Name == "AccessTools" && r.Name == "Field" && r.Parameters.Count == 2).Operand;
MethodReference typeFromHandle = (MethodReference)ins.First(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "GetTypeFromHandle").Operand;
var ldarg0 = OpCodeField("Ldarg_0"); var ldfld = OpCodeField("Ldfld"); var box = OpCodeField("Box");

int ConstOf(Instruction i) => i.OpCode.Code switch { Code.Ldc_I4_S => (sbyte)i.Operand, Code.Ldc_I4 => (int)i.Operand, _ => -1 };
int replaced = 0;
for (int k = 0; k + 8 < ins.Count; k++)
{
    // ldloc.0 ldc N callvirt get_Item ldfld opcode ldloc.0 ldc N callvirt get_Item ldfld operand newobj CodeInstruction(OpCode, object)
    if (ins[k].OpCode != OpCodes.Ldloc_0 || ins[k + 4].OpCode != OpCodes.Ldloc_0 || ins[k + 8].OpCode != OpCodes.Newobj) continue;
    int n = ConstOf(ins[k + 1]);
    if (n is not (24 or 25 or 293) || ConstOf(ins[k + 5]) != n) continue;
    if (ins[k + 3].Operand is not FieldReference f1 || f1.Name != "opcode" || ins[k + 7].Operand is not FieldReference f2 || f2.Name != "operand") continue;
    var repl = n switch
    {
        24 => new[] { il.Create(OpCodes.Ldsfld, ldarg0), il.Create(OpCodes.Ldnull) },
        25 => new[] { il.Create(OpCodes.Ldsfld, ldfld), il.Create(OpCodes.Call, nested), il.Create(OpCodes.Ldstr, "charge"), il.Create(OpCodes.Call, fieldMethod) },
        _ => new[] { il.Create(OpCodes.Ldsfld, box), il.Create(OpCodes.Ldtoken, m.TypeSystem.Single), il.Create(OpCodes.Call, typeFromHandle) },
    };
    // keep the first instruction object (branch targets), drop the other 7
    ins[k].OpCode = repl[0].OpCode; ins[k].Operand = repl[0].Operand;
    for (int r = 0; r < 7; r++) il.Remove(ins[k + 1]);
    for (int r = repl.Length - 1; r >= 1; r--) il.InsertAfter(ins[k], repl[r]);
    replaced++;
}
Console.WriteLine($"replaced {replaced} index lookups");
if (replaced != 5) { Console.Error.WriteLine("expected 5"); return 1; }
m.Write(args[1], new WriterParameters { DeterministicMvid = true });
return 0;
