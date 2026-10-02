using Mono.Cecil;

// rounds-port: find and fix what breaks ROUNDS mods on the current (2025, Unity 2022.3) game build.
const string Usage = """
rounds-port: port ROUNDS mods to the current game build (the 2025 update)

  rounds-port scan <mod.dll | folder>...   list everything that no longer matches the game
  rounds-port fix  <mod.dll | folder>...   rewrite what it can, save fixed copies, list what's left
  rounds-port hot  <mod.dll | folder>...   port it and swap it into the running game (needs the Hot Reload plugin)

  rounds-port install-hotreload            add the optional Hot Reload plugin to the game (BepInEx/plugins/HotReload)
  rounds-port uninstall-hotreload          remove it again

  rounds-port sweep <list.tsv>             regression test: scan and fix a pinned list of Thunderstore mods
                                           (for working on rounds-port itself; see tests/README.md)

options
  --game <dir>     ROUNDS folder (found through Steam if you leave it out)
  --ref <dir>      another folder of DLLs your mod uses, e.g. Bknibb's UnboundLib 4 (repeatable)
  -o, --out <dir>  where fix saves mods (default: a "ported" folder next to each mod)
  --pdb            fix also writes a .pdb (line numbers in error stack traces)
  --watch          hot: keep watching the DLL and swap in every rebuild
  --version        print the version
  --top <n>        sweep: first make <list.tsv> from the n most-downloaded Thunderstore mods (current versions)
  --save <file>    sweep: write the results to <file>
  --compare <file> sweep: list what changed against results saved earlier (exit 1 if anything did)

Each problem is marked AUTO (fix handles it), REVIEW (fix handles it, check the result) or MANUAL (change your source).
Exit code: 0 nothing left to do, 1 only AUTO/REVIEW items, 2 MANUAL items remain, 3 error.
""";

if (args.Length > 0 && args[0] is "-v" or "--version") { Console.WriteLine("rounds-port " + typeof(Out).Assembly.GetName().Version?.ToString(3)); return 0; }
if (args.Length == 0 || args[0] is "-h" or "--help" || args[0] is not ("scan" or "fix" or "hot" or "sweep" or "install-hotreload" or "uninstall-hotreload")) { Console.Write(Usage); return args.Length == 0 || args[0] is "-h" or "--help" ? 0 : 3; }
bool fix = args[0] == "fix";
string? gameDir = null, outDir = null, save = null, compare = null; bool pdb = false, watch = false; int top = 0;
var refs = new List<string>(); var inputs = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new UserError($"{args[i]} needs a value");
    try
    {
        switch (args[i])
        {
            case "--game": gameDir = Next(); break;
            case "--ref": refs.Add(Next()); break;
            case "-o" or "--out": outDir = Next(); break;
            case "--pdb": pdb = true; break;
            case "--watch": watch = true; break;
            case "--top": top = int.TryParse(Next(), out var n) && n > 0 ? n : throw new UserError("--top needs a number"); break;
            case "--save": save = Next(); break;
            case "--compare": compare = Next(); break;
            default:
                if (args[i].StartsWith('-')) throw new UserError($"unknown option {args[i]}");
                inputs.Add(args[i]); break;
        }
    }
    catch (UserError e) { Out.Error(e.Message); return 3; }
}
if (args[0] is "install-hotreload" or "uninstall-hotreload")
{
    try { return HotReloadSetup.Run(gameDir, install: args[0] == "install-hotreload"); }
    catch (UserError e) { Out.Error(e.Message); return 3; }
}
if (args[0] == "sweep")
{
    if (inputs.Count != 1) { Out.Error("sweep needs one list file (tests/sweep-packages.tsv in the repo)"); return 3; }
    try { return Sweep.Run(gameDir, inputs[0], top, save, compare); }
    catch (UserError e) { Out.Error(e.Message); return 3; }
}
if (inputs.Count == 0) { Out.Error("give at least one mod DLL or folder"); return 3; }

try
{
    var game = new Game(gameDir, refs, inputs);
    Out.Line($"game: {game.Dir}");
    Out.Line($"UnboundLib: {game.UnboundLib ?? "not found (add --ref <folder with Bknibb's UnboundLib 4>)"}");
    if (args[0] == "hot") return Hot.Run(game, inputs, watch);
    var dlls = Program.Expand(inputs).ToList();
    game.EnsureDependencies(dlls);
    var scanner = new Scanner(game);
    int worst = 0;
    foreach (var dll in dlls)
    {
        Out.Line("");
        Ported p;
        try { p = Program.Port(dll, game, scanner, fix, outDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dll))!, "ported"), pdb); }
        catch (BadImageFormatException e) { Out.Error($"{dll}: can't read it ({e.Message})"); worst = 3; continue; }
        if (p.Dest == null) Out.Report(Path.GetFileName(dll) + (fix ? " (nothing to rewrite)" : ""), p.Before);
        else Out.Fixed(Path.GetFileName(dll), p.Dest, p.Fixer!.Changes, p.Fixer.Notes, p.Before.Count, p.Left);
        Out.Unchecked(scanner.Unchecked);
        worst = Math.Max(worst, Program.Grade(p.Left));
    }
    return worst;
}
catch (UserError e) { Out.Error(e.Message); return 3; }

sealed record Ported(List<Issue> Before, List<Issue> Left, Fixer? Fixer, string? Dest);

static partial class Program
{
public static int Grade(List<Issue> issues) => issues.Count == 0 ? 0 : issues.Any(i => i.Fix == Fix.Manual) ? 2 : 1;

// One mod: scan it; with fix, rewrite it into outDir and scan the result. Dest is null when nothing was written.
// An unreadable DLL throws BadImageFormatException.
public static Ported Port(string dll, Game game, Scanner scanner, bool fix, string outDir, bool pdb)
{
    var rp = new ReaderParameters { AssemblyResolver = game.Resolver, ReadingMode = ReadingMode.Immediate, InMemory = true };
    ModuleDefinition module;
    try { module = ModuleDefinition.ReadModule(dll, rp); }
    catch (Exception e) when (e is not BadImageFormatException) { throw new BadImageFormatException(e.Message, e); }
    var issues = scanner.Scan(module);
    if (!fix) return new(issues, issues, null, null);
    var fixer = new Fixer(module, game, scanner);
    fixer.Run();
    if (!fixer.Changed) return new(issues, issues, fixer, null);
    var dest = Path.Combine(outDir, Path.GetFileName(dll));
    Directory.CreateDirectory(outDir);
    if (pdb) module.Write(dest, new WriterParameters { WriteSymbols = true, SymbolWriterProvider = new Mono.Cecil.Cil.PortablePdbWriterProvider() });
    else module.Write(dest);
    return new(issues, scanner.Scan(ModuleDefinition.ReadModule(dest, rp)), fixer, dest);
}

// Files as given; folders: every DLL inside that references the game (skips libraries like Odin or MMHOOK).
public static IEnumerable<string> Expand(List<string> inputs)
{
    foreach (var i in inputs)
    {
        if (File.Exists(i)) { yield return i; continue; }
        if (!Directory.Exists(i)) { Out.Error($"{i}: not found"); continue; }
        foreach (var f in Directory.GetFiles(i, "*.dll", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "ported" + Path.DirectorySeparatorChar)).OrderBy(f => f))
        {
            bool game;
            try { using var m = ModuleDefinition.ReadModule(f); game = m.AssemblyReferences.Any(a => a.Name == "Assembly-CSharp") && !m.Name.StartsWith("MMHOOK"); }
            catch { continue; }
            if (game) yield return f;
        }
    }
}
}

static class Out
{
    static readonly bool Color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null && WindowsConsole.EnableColor();
    static string C(string code, string s) => Color ? $"\u001b[{code}m{s}\u001b[0m" : s;
    public static void Line(string s) => Console.WriteLine(s);
    public static void Error(string s) => Console.Error.WriteLine(C("31", "error: ") + s);
    public static void Warn(string s) => Console.Error.WriteLine(C("33", "warning: ") + s);
    public static void Note(string s) => Console.Error.WriteLine(C("36", "note: ") + s);

    public static void Unchecked(ICollection<string> missing)
    {
        if (missing.Count == 0) return;
        Warn($"not checked: what it uses from {string.Join(", ", missing)} (not installed). " +
             "Put those mods in BepInEx/plugins, or add --ref <folder with them>.");
    }

    static string Tag(Fix f) => f switch { Fix.Auto => C("32", "AUTO  "), Fix.Review => C("33", "REVIEW"), _ => C("31", "MANUAL") };

    static void Issues(IEnumerable<Issue> issues)
    {
        foreach (var i in issues.OrderBy(i => i.Fix).ThenBy(i => i.Kind).ThenBy(i => i.What))
            Line($"  {Tag(i.Fix)} {i.What}\n         {C("2", i.Detail)}");
    }

    static string Counts(List<Issue> issues)
    {
        var parts = new[] { Fix.Auto, Fix.Review, Fix.Manual }.Select(f => (f, n: issues.Count(i => i.Fix == f))).Where(x => x.n > 0)
            .Select(x => $"{x.n} {x.f.ToString().ToLower()}");
        return string.Join(", ", parts);
    }

    public static void Report(string name, List<Issue> issues)
    {
        Line(C("1", name) + (issues.Count == 0 ? C("32", ": no problems found") : $": {issues.Count} problem{(issues.Count == 1 ? "" : "s")} ({Counts(issues)})"));
        Issues(issues);
    }

    public static void Fixed(string name, string dest, IEnumerable<string> changes, List<string> notes, int before, List<Issue> left)
    {
        Line(C("1", name) + $": {before} problem{(before == 1 ? "" : "s")} before, {left.Count} left. Saved {dest}");
        foreach (var c in changes) Line("  " + C("32", "fixed ") + " " + c);
        foreach (var n in notes) Line("  " + C("33", "note  ") + " " + n);
        Issues(left);
    }
}

// The classic Windows console (PowerShell 5.1, cmd) only shows colours after virtual-terminal processing is switched
// on; otherwise the escape codes print as text. Off when that fails.
static class WindowsConsole
{
    public static bool EnableColor()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            bool ok = true;
            foreach (int std in new[] { -11, -12 })   // stdout, stderr
            {
                var h = GetStdHandle(std);
                ok &= GetConsoleMode(h, out uint mode) && SetConsoleMode(h, mode | 0x0004);
            }
            return ok;
        }
        catch { return false; }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);
}
