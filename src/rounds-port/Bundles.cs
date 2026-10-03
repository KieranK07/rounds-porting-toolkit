using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Mono.Cecil;

// Unity asset bundles a mod ships (embedded resources, or files next to the DLL). Their components are saved against
// the game's scripts by assembly + class name and their fields by name, so a script or field the 2025 build dropped
// silently loses data when the bundle loads. Checked per script type (the bundle's type tree), not per object.
static class Bundles
{
    sealed record Result(List<Issue> Issues, List<string> Unchecked);

    // per module (MVID): fix scans the rewritten copy again, and its bundles are the same
    static readonly Dictionary<Guid, Result> Done = new();

    static readonly HashSet<string> OldGame = LoadOldGame();
    static HashSet<string> LoadOldGame()
    {
        using var s = typeof(Bundles).Assembly.GetManifestResourceStream("old-game-types.txt")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
    }

    // Saved by every component; not fields of the script.
    static readonly HashSet<string> UnityKeys = new()
    {
        "m_GameObject", "m_Enabled", "m_Script", "m_Name", "m_EditorHideFlags", "m_EditorClassIdentifier", "m_ObjectHideFlags",
        "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_PrefabParentObject", "m_PrefabInternal",
    };
    // TextMeshPro internals a newer TMP dropped; harmless.
    static readonly HashSet<string> Ignore = new() { "m_isLinkedTextComponent", "m_ignoreRectMaskCulling", "m_canvasRenderer" };

    public static IEnumerable<Issue> Check(ModuleDefinition module, Game game, ISet<string> uncheckedDeps)
    {
        if (!Done.TryGetValue(module.Mvid, out var res))
        {
            res = Run(module, game);
            Done[module.Mvid] = res;
        }
        foreach (var u in res.Unchecked) uncheckedDeps.Add(u);
        return res.Issues;
    }

    static Result Run(ModuleDefinition module, Game game)
    {
        var bundles = new List<(string name, byte[] data)>();
        foreach (var r in module.Resources.OfType<EmbeddedResource>())
        {
            var d = r.GetResourceData();
            if (IsBundle(d)) bundles.Add((r.Name, d));
        }
        bundles.AddRange(Loose(module));
        var c = new Checker(module, game);
        foreach (var (name, data) in bundles)
        {
            try { c.Bundle(name, data); }
            catch (Exception e) { Out.Warn($"{module.Name}: couldn't read asset bundle {name} ({e.GetType().Name}: {e.Message})"); }
        }
        return new Result(c.Finish(), c.UncheckedDeps.ToList());
    }

    static bool IsBundle(byte[] d) => d.Length > 16 && d[0] == 'U' && d[1] == 'n' && d[2] == 'i' && d[3] == 't' && d[4] == 'y' && d[5] == 'F' && d[6] == 'S';

    // Bundle files next to the DLL that belong to it: its code names the file, or it's the only DLL there.
    static IEnumerable<(string, byte[])> Loose(ModuleDefinition module)
    {
        var dir = string.IsNullOrEmpty(module.FileName) ? null : Path.GetDirectoryName(module.FileName);
        if (dir == null || !Directory.Exists(dir)) yield break;
        var files = Directory.GetFiles(dir).Where(f => Path.GetExtension(f).ToLowerInvariant() is not (".dll" or ".pdb" or ".mdb" or ".xml" or ".json" or ".md" or ".txt" or ".png" or ".jpg" or ".cfg")).ToList();
        if (files.Count == 0) yield break;
        bool alone = Directory.GetFiles(dir, "*.dll").Length == 1;
        HashSet<string>? strings = null;
        foreach (var f in files)
        {
            var head = new byte[17];
            using (var fs = File.OpenRead(f)) if (fs.Read(head, 0, head.Length) < head.Length || !IsBundle(head)) continue;
            strings ??= module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions).Where(i => i.Operand is string).Select(i => ((string)i.Operand).ToLowerInvariant()).ToHashSet();
            var n = Path.GetFileName(f).ToLowerInvariant();
            if (alone || strings.Any(s => s.Contains(n))) yield return (Path.GetFileName(f), File.ReadAllBytes(f));
        }
    }

    sealed class Checker(ModuleDefinition module, Game game)
    {
        readonly string own = module.Assembly.Name.Name;
        public readonly SortedSet<string> UncheckedDeps = new(StringComparer.OrdinalIgnoreCase);
        readonly SortedDictionary<string, int> preexisting = new(), dropped = new(), removed = new(), otherMissing = new();
        readonly Dictionary<string, string> droppedWhy = new();
        int bundles, unresolved, checkedObjects, cards, cardsNoText, frames, framesNoText, shaders, shadersNoMetal, noTypeTree;
        readonly List<string> versions = new();

        public void Bundle(string name, byte[] data)
        {
            var am = new AssetsManager();
            try
            {
                var bun = am.LoadBundleFile(new MemoryStream(data), name, true);
                bundles++;
                // load every assets file first: scripts are often saved in a sibling file of the same bundle
                var files = bun.file.GetAllFileNames();
                var loaded = Enumerable.Range(0, files.Count).Where(bun.file.IsAssetsFile).Select(i => am.LoadAssetsFileFromBundle(bun, i, false)).ToList();
                foreach (var inst in loaded) AssetsFile(am, inst);
            }
            finally { am.UnloadAll(true); }
        }

        void AssetsFile(AssetsManager am, AssetsFileInstance inst)
        {
            var file = inst.file; var meta = file.Metadata;
            if (!versions.Contains(meta.UnityVersion)) versions.Add(meta.UnityVersion);
            if (!meta.TypeTreeEnabled) { noTypeTree++; return; }

            // components per script type
            var perScript = new Dictionary<ushort, List<AssetFileInfo>>();
            foreach (var info in file.AssetInfos)
                if (info.TypeId == (int)AssetClassID.MonoBehaviour)
                {
                    var idx = info.GetScriptIndex(file);
                    if (idx == 0xffff) continue;
                    if (!perScript.TryGetValue(idx, out var l)) perScript[idx] = l = new();
                    l.Add(info);
                }

            foreach (var (idx, objs) in perScript)
            {
                if (idx >= meta.ScriptTypes.Count) continue;
                var ptr = meta.ScriptTypes[idx];
                AssetTypeValueField? ms = null;
                try { ms = am.GetExtAsset(inst, ptr.FileId, ptr.PathId).baseField; } catch { }
                if (ms == null || ms.IsDummy) { unresolved += objs.Count; continue; }   // script saved in another bundle
                string cls = ms["m_ClassName"].AsString, ns = ms["m_Namespace"].AsString;
                string asm = ms["m_AssemblyName"].AsString;
                if (asm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) asm = asm[..^4];
                if (asm.Equals(own, StringComparison.OrdinalIgnoreCase)) continue;   // the mod's own scripts
                var full = ns.Length > 0 ? ns + "." + cls : cls;

                var a = game.Resolver.Get(asm);
                if (a == null) { UncheckedDeps.Add(asm); continue; }
                var td = a.MainModule.GetType(full);
                if (td == null)
                {
                    var key = $"{full} ({asm})";
                    var into = asm != "Assembly-CSharp" ? otherMissing : OldGame.Contains(full) ? removed : preexisting;
                    into[key] = into.GetValueOrDefault(key) + objs.Count;
                    continue;
                }
                checkedObjects += objs.Count;
                if (UnityOwned(asm)) continue;   // TextMeshPro, UnityEngine.UI...: Unity migrates its own saved data
                var tt = meta.FindTypeTreeTypeByScriptIndex(idx);
                if (tt == null) continue;
                var tf = new AssetTypeTemplateField();
                tf.FromTypeTree(tt);
                foreach (var child in tf.Children)
                {
                    if (UnityKeys.Contains(child.Name) || Ignore.Contains(child.Name)) continue;
                    var fd = Field(td, child.Name, out bool unknown);
                    if (fd == null) { if (!unknown) Drop($"{full}.{child.Name}", objs.Count, $"{full} has no field {child.Name} now"); }
                    else if (UnityOwned(fd.DeclaringType.Module.Assembly.Name.Name)) continue;
                    else if (!Serialized(fd)) Drop($"{full}.{child.Name}", objs.Count, $"{full}.{child.Name} isn't saved by Unity any more");
                    else Compare(child, fd.FieldType, $"{full}.{child.Name}", objs.Count, 0);
                }

                if (asm == "Assembly-CSharp" && full == "CardInfo")
                {
                    cards += objs.Count;
                    if (!tf.Children.Any(x => x.Name == "m_localizedCardName")) cardsNoText += objs.Count;
                    else foreach (var o in objs) if (EmptyLocalized(am.GetBaseField(inst, o)["m_localizedCardName"])) cardsNoText++;
                }
                if (asm == "Assembly-CSharp" && full == "CardInfoDisplayer")
                {
                    frames += objs.Count;
                    if (!tf.Children.Any(x => x.Name == "m_localizedNameText")) framesNoText += objs.Count;
                    else foreach (var o in objs) if (am.GetBaseField(inst, o)["m_localizedNameText"]["m_PathID"].AsLong == 0) framesNoText++;
                }
            }

            foreach (var info in file.GetAssetsOfType(AssetClassID.Shader))
            {
                shaders++;
                var bf = am.GetBaseField(inst, info);
                var pl = bf["platforms"];
                if (pl.IsDummy) continue;
                if (!pl["Array"].Children.Any(p => p.AsUInt == Metal)) shadersNoMetal++;
            }
        }

        const uint Metal = 14;   // ShaderCompilerPlatform.Metal

        static bool EmptyLocalized(AssetTypeValueField ls)
        {
            if (ls.IsDummy) return true;
            var table = ls["m_TableReference"]["m_TableCollectionName"];
            var key = ls["m_TableEntryReference"]["m_Key"];
            var id = ls["m_TableEntryReference"]["m_KeyId"];
            return (table.IsDummy || table.AsString.Length == 0) && (key.IsDummy || key.AsString.Length == 0) && (id.IsDummy || id.AsLong == 0);
        }

        void Drop(string what, int count, string why)
        {
            dropped[what] = dropped.GetValueOrDefault(what) + count;
            droppedWhy[what] = why;
        }

        // ---- the saved field's type (from the bundle) against the current C# field type

        static readonly Dictionary<string, string[]> Prim = new()
        {
            ["float"] = new[] { "System.Single" }, ["double"] = new[] { "System.Double" }, ["bool"] = new[] { "System.Boolean" },
            ["int"] = new[] { "System.Int32" }, ["SInt32"] = new[] { "System.Int32" }, ["unsigned int"] = new[] { "System.UInt32" }, ["UInt32"] = new[] { "System.UInt32" },
            ["SInt64"] = new[] { "System.Int64" }, ["long long"] = new[] { "System.Int64" }, ["UInt64"] = new[] { "System.UInt64" }, ["unsigned long long"] = new[] { "System.UInt64" },
            ["SInt16"] = new[] { "System.Int16" }, ["short"] = new[] { "System.Int16" }, ["UInt16"] = new[] { "System.UInt16", "System.Char" }, ["unsigned short"] = new[] { "System.UInt16", "System.Char" },
            ["SInt8"] = new[] { "System.SByte" }, ["UInt8"] = new[] { "System.Byte", "System.Boolean" }, ["char"] = new[] { "System.Char", "System.Byte" }, ["string"] = new[] { "System.String" },
        };
        static readonly Dictionary<string, string[]> UnityStructs = new()
        {
            ["Vector2f"] = new[] { "UnityEngine.Vector2" }, ["Vector3f"] = new[] { "UnityEngine.Vector3" }, ["Vector4f"] = new[] { "UnityEngine.Vector4" },
            ["Quaternionf"] = new[] { "UnityEngine.Quaternion" }, ["ColorRGBA"] = new[] { "UnityEngine.Color", "UnityEngine.Color32" },
            ["Rectf"] = new[] { "UnityEngine.Rect" }, ["Matrix4x4f"] = new[] { "UnityEngine.Matrix4x4" }, ["AABB"] = new[] { "UnityEngine.Bounds" },
            ["BitField"] = new[] { "UnityEngine.LayerMask" }, ["int2_storage"] = new[] { "UnityEngine.Vector2Int" }, ["int3_storage"] = new[] { "UnityEngine.Vector3Int" },
            ["Vector2Int"] = new[] { "UnityEngine.Vector2Int" }, ["Vector3Int"] = new[] { "UnityEngine.Vector3Int" },
        };
        static readonly HashSet<string> UnityStructNames = UnityStructs.Values.SelectMany(v => v).ToHashSet();

        void Compare(AssetTypeTemplateField t, TypeReference cs, string path, int count, int depth)
        {
            if (cs.ContainsGenericParameter || depth > 4) return;
            // a string is saved as an array of chars
            bool ttArray = t.Type != "string" && (t.IsArray || t.Children.Count == 1 && t.Children[0].IsArray);
            TypeReference? elem = cs is ArrayType at ? at.ElementType
                : cs is GenericInstanceType gi && gi.ElementType.FullName == "System.Collections.Generic.List`1" ? gi.GenericArguments[0] : null;
            if (ttArray != (elem != null)) { Drop(path, count, $"{path}: saved as {(ttArray ? "an array" : t.Type)}, the field is {cs.Name} now"); return; }
            if (ttArray)
            {
                var arr = t.IsArray ? t : t.Children[0];
                if (arr.Children.Count == 2) Compare(arr.Children[1], elem!, path + "[]", count, depth + 1);
                return;
            }
            var def = Resolve(cs);
            string name = def?.FullName ?? cs.FullName;
            if (Prim.TryGetValue(t.Type, out var p))
            {
                if (def?.IsEnum == true && t.Type is not ("string" or "float" or "double" or "bool")) return;
                if (!p.Contains(name)) Drop(path, count, $"{path}: saved as {t.Type}, the field is {cs.Name} now");
                return;
            }
            if (t.Type.StartsWith("PPtr<"))
            {
                if (def != null && !IsUnityObject(def)) Drop(path, count, $"{path}: saved as a reference ({t.Type}), the field is {cs.Name} now");
                return;
            }
            if (UnityStructs.TryGetValue(t.Type, out var u))
            {
                if (def != null && !u.Contains(name)) Drop(path, count, $"{path}: saved as {t.Type}, the field is {cs.Name} now");
                return;
            }
            if (def == null) return;
            if (name.StartsWith("System.") && def.IsPrimitive || name == "System.String" || UnityStructNames.Contains(name) || IsUnityObject(def))
            { Drop(path, count, $"{path}: saved as {t.Type}, the field is {cs.Name} now"); return; }
            // a serializable class or struct: its fields, by name. Unity's own types are left alone.
            if (UnityOwned(def.Module.Assembly.Name.Name) || def.Module.Assembly.Name.Name == "mscorlib") return;
            foreach (var child in t.Children)
            {
                var fd = Field(def, child.Name, out bool unknown);
                if (fd == null) { if (!unknown) Drop($"{path}.{child.Name}", count, $"{def.FullName} has no field {child.Name} now"); }
                else if (!Serialized(fd)) Drop($"{path}.{child.Name}", count, $"{def.FullName}.{child.Name} isn't saved by Unity any more");
                else Compare(child, Substitute(fd.FieldType, cs), $"{path}.{child.Name}", count, depth + 1);
            }
        }

        // a field of a generic struct (Foo<int>.value is T): use the closed type
        static TypeReference Substitute(TypeReference ft, TypeReference owner) =>
            ft is GenericParameter gp && owner is GenericInstanceType git && gp.Position < git.GenericArguments.Count ? git.GenericArguments[gp.Position] : ft;

        TypeDefinition? Resolve(TypeReference t) { try { return t.Resolve(); } catch { return null; } }

        bool IsUnityObject(TypeDefinition t)
        {
            for (TypeDefinition? c = t; c != null; c = c.BaseType == null ? null : Resolve(c.BaseType))
                if (c.FullName == "UnityEngine.Object") return true;
            return false;
        }

        // the field Unity fills from a saved name: declared on the type or a base type, or renamed with [FormerlySerializedAs]
        // `unknown`: a base type couldn't be read (Odin's SerializedMonoBehaviour, a mod that isn't installed), so a field
        // that isn't found may still be there.
        FieldDefinition? Field(TypeDefinition td, string name, out bool unknown)
        {
            unknown = false;
            for (TypeDefinition? c = td; c != null; c = c.BaseType == null ? null : Resolve(c.BaseType))
            {
                if (c.BaseType != null && Resolve(c.BaseType) == null) unknown = true;
                foreach (var f in c.Fields)
                {
                    if (f.IsStatic) continue;
                    if (f.Name == name) return f;
                    if (f.CustomAttributes.Any(a => a.AttributeType.Name == "FormerlySerializedAsAttribute" && a.ConstructorArguments.Count > 0 && (string?)a.ConstructorArguments[0].Value == name)) return f;
                }
            }
            return null;
        }

        static bool UnityOwned(string asm) => asm.StartsWith("Unity.") || asm.StartsWith("UnityEngine");

        static bool Serialized(FieldDefinition f) =>
            !f.IsStatic && !f.IsInitOnly && !f.IsNotSerialized
            && (f.IsPublic || f.CustomAttributes.Any(a => a.AttributeType.Name is "SerializeField" or "SerializeReference"));

        public List<Issue> Finish()
        {
            var issues = new List<Issue>();
            if (bundles == 0) return issues;
            string Top(SortedDictionary<string, int> d) => string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key.Replace(" (Assembly-CSharp)", ""))) + (d.Count > 4 ? ", ..." : "");
            var where = $"{bundles} asset bundle{(bundles == 1 ? "" : "s")} (Unity {string.Join(", ", versions)})";

            foreach (var (k, n) in removed)
                issues.Add(new Issue(Fix.Manual, "bundle", $"asset bundle: script {k} ({n} component{(n == 1 ? "" : "s")})",
                    "the old game had this script and the current one doesn't: these components come up missing when the bundle loads. Rebuild the bundle without them"));
            foreach (var (k, n) in otherMissing)
                issues.Add(new Issue(Fix.Review, "bundle", $"asset bundle: script {k} ({n} component{(n == 1 ? "" : "s")})",
                    "not in that assembly now, so these components come up missing when the bundle loads (can't tell whether it was there before the update)"));
            foreach (var (k, n) in dropped)
                issues.Add(new Issue(Fix.Manual, "bundle", $"asset bundle: {k} ({n} object{(n == 1 ? "" : "s")})",
                    droppedWhy[k] + ": the saved value is lost when the bundle loads. Set it in code, or rebuild the bundle against the current game"));
            if (framesNoText > 0)
                issues.Add(new Issue(Fix.Review, "bundle", $"asset bundle: {framesNoText} card frame{(framesNoText == 1 ? "" : "s")} (CardInfoDisplayer) without localized text fields",
                    "saved before the 2025 build added m_localizedNameText / m_localizedEffectText. The game's CardInfoDisplayer.DrawCard sets them without a null check, so a card drawn with this frame throws a NullReferenceException and shows no text. Rebuild the frame with UILocalizedString text (stat rows too), or don't use it as a cardBase"));
            var notes = new List<string>();
            // UnboundLib 4's CustomCard.BuildUnityCard sets both from GetTitle()/GetDescription() when a card registers
            bool viaCustomCard = module.GetMemberReferences().Any(m => m.Name == "BuildUnityCard" && m.DeclaringType.FullName == "UnboundLib.Cards.CustomCard");
            if (cardsNoText > 0 && viaCustomCard)
                notes.Add($"{cardsNoText} of {cards} cards have no localized text; the mod registers cards with CustomCard.BuildUnityCard, which fills it on UnboundLib 4");
            else if (cardsNoText > 0)
                issues.Add(new Issue(Fix.Review, "bundle", $"asset bundle: {cardsNoText} card{(cardsNoText == 1 ? "" : "s")} without localized text",
                    "the 2025 build shows card names and descriptions from localization (m_localizedCardName / m_localizedCardDescription), which these cards were saved without, and this mod doesn't register cards through CustomCard.BuildUnityCard (UnboundLib 4 fills them there). Check the cards show their name and text in game"));
            if (preexisting.Count > 0)
                notes.Add($"{preexisting.Values.Sum()} components use scripts the game doesn't have ({Top(preexisting)}), but the old game didn't have them either: not from the update");
            if (shadersNoMetal > 0)
                notes.Add($"{shadersNoMetal} of {shaders} shaders have no Metal version, so on macOS they draw pink. Build the bundle for macOS too");
            if (unresolved > 0)
                notes.Add($"{unresolved} components use scripts saved in another bundle: not checked");
            if (noTypeTree > 0)
                notes.Add($"{noTypeTree} file{(noTypeTree == 1 ? "" : "s")} saved without type trees: not checked");
            var summary = $"{module.Name}: {where}, {checkedObjects} game components checked";
            Out.Note(notes.Count == 0 && issues.Count == 0 ? summary + ": nothing the update changed" : summary + (notes.Count > 0 ? ". " + string.Join(". ", notes) : ""));
            return issues;
        }
    }
}

sealed partial class Scanner
{
    partial void MoreChecks(ModuleDefinition module, Game game, Action<Issue> add)
    {
        foreach (var i in Bundles.Check(module, game, Unchecked)) add(i);
    }
}
