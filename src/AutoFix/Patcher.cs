using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mono.Cecil;

// rounds-port AutoFix, a BepInEx 5 preloader patcher (BepInEx/patchers). It patches none of the game's DLLs
// (TargetDLLs is empty): Initialize runs before the Chainloader reads BepInEx/plugins, and that's when the mods in it
// get fixed. Nothing here may stop the game from starting: any failure leaves the mods as they are.
public static class AutoFixPatcher
{
    public static IEnumerable<string> TargetDLLs { get; } = new string[0];

    public static void Patch(AssemblyDefinition assembly) { }

    public static void Initialize()
    {
        var log = Logger.CreateLogSource("rounds-port");
        try { Run(log); }
        catch (Exception e) { log.LogError($"AutoFix failed, mods are left as they are: {e}"); }
    }

    static void Run(ManualLogSource log)
    {
        var cfg = new ConfigFile(Path.Combine(Paths.ConfigPath, "rounds-port.autofix.cfg"), true);
        var enabled = cfg.Bind("General", "Enabled", true,
            "Fix mods made for the game before its 2025 update while the game starts (rounds-port's fix, applied in place).");
        var exclude = cfg.Bind("General", "Exclude", "",
            "Mods never to touch, comma-separated: DLL names (MapsExtended.dll) or folder names (olavim-MapsExtended). A mod fixed earlier gets its original back.");
        var restore = cfg.Bind("General", "RestoreOriginals", false,
            "Put back the original of every mod AutoFix changed, then turn AutoFix off (Enabled = false).");
        var whenManual = cfg.Bind("Manual", "FixWhenManualLeft", false,
            "Also rewrite mods that still have MANUAL items after fixing (problems only their authors can fix). They load, but those parts may still fail.");
        var fixAnyway = cfg.Bind("Manual", "FixAnyway", "",
            "Mods to rewrite even with MANUAL items left, comma-separated, as in Exclude.");

        var settings = new AutoFix.Settings(Paths.GameRootPath, Paths.ManagedPath, Paths.BepInExAssemblyDirectory, Paths.PluginPath,
            Path.Combine(Paths.CachePath, "rounds-port"), whenManual.Value, List(exclude.Value), List(fixAnyway.Value));
        var autofix = new AutoFix(settings, log);
        if (restore.Value)
        {
            autofix.RestoreOriginals();
            restore.Value = false;
            enabled.Value = false;
            return;
        }
        if (!enabled.Value) { log.LogInfo("AutoFix is off (Enabled in rounds-port.autofix.cfg)"); return; }
        autofix.Run();
    }

    static string[] List(string s) => s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
}
