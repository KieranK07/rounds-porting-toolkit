using Mono.Cecil; using Mono.Cecil.Cil;
// macfix IN OUT: replace every call to user32!GetAsyncKeyState with "pop; ldc.i4.0" (key never held).
var res = new DefaultAssemblyResolver();
foreach (var d in args.Skip(2)) res.AddSearchDirectory(d);
res.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { ReadSymbols = false, AssemblyResolver = res });
int n = 0;
foreach (var t in asm.MainModule.GetTypes())
foreach (var m in t.Methods.Where(m => m.HasBody))
{
    var il = m.Body.GetILProcessor();
    foreach (var ins in m.Body.Instructions.ToList())
        if ((ins.OpCode == OpCodes.Call) && ins.Operand is MethodReference r && r.Name == "GetAsyncKeyState")
        {
            var ld = il.Create(OpCodes.Ldc_I4_0);
            ins.OpCode = OpCodes.Pop; ins.Operand = null;   // keep the instruction object so branch targets stay valid
            il.InsertAfter(ins, ld); n++;
            Console.WriteLine($"patched {t.FullName}::{m.Name}");
        }
}
// drop the now-unused P/Invoke so the runtime never tries to bind user32.dll
foreach (var t in asm.MainModule.GetTypes())
    foreach (var m in t.Methods.Where(m => m.HasPInvokeInfo && m.PInvokeInfo.Module.Name == "user32.dll").ToList())
    { t.Methods.Remove(m); Console.WriteLine($"removed pinvoke {t.FullName}::{m.Name}"); }
var mr = asm.MainModule.ModuleReferences.FirstOrDefault(x => x.Name == "user32.dll");
if (mr != null) asm.MainModule.ModuleReferences.Remove(mr);
asm.Write(args[1]);
Console.WriteLine($"{n} call site(s) patched");
