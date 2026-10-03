using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RoundsPort.Runtime
{
    // Mod asset bundles were built for Windows only (D3D11 shader programs, no Metal), so on macOS anything
    // using their shaders renders magenta. The game ships Metal builds of the same TextMeshPro / particle
    // shaders, so materials are re-pointed at those *objects* (not by name: the bundle copies share names).
    // Shaders the game doesn't ship are rebuilt on top of Particles/Standard Unlit with the original blend state.
    internal static class ShaderFix
    {
        public static BepInEx.Logging.ManualLogSource Log;
        static readonly HashSet<AssetBundle> recordedBundles = new HashSet<AssetBundle>();
        static readonly HashSet<string> reported = new HashSet<string>();
        // Shaders that came out of mod asset bundles. Their isSupported is unreliable (Unity reports true until
        // the first draw fails), so anything in this set is replaced regardless.
        static readonly HashSet<Shader> foreign = new HashSet<Shader>();
        static Dictionary<string, Shader> native;
        static int nativeFrame = -1;

        public static bool IsForeignPublic(Shader sh) => sh != null && foreign.Contains(sh);
        static bool IsForeign(Shader sh) => sh != null && (foreign.Contains(sh) || !sh.isSupported);

        // The game's own copy of a shader: mod bundles add same-named duplicates (D3D-only) that may still
        // report isSupported=true. Game shaders load first (sharedassets0), so the lowest instance id wins.
        static Shader Native(string name)
        {
            if ((native == null || !native.ContainsKey(name)) && nativeFrame != Time.frameCount)
            {
                nativeFrame = Time.frameCount;   // rebuild at most once per frame
                native = new Dictionary<string, Shader>();
                foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>())
                {
                    if (sh == null || foreign.Contains(sh) || !sh.isSupported || sh.GetInstanceID() <= 0) continue;
                    if (!native.TryGetValue(sh.name, out var cur) || sh.GetInstanceID() < cur.GetInstanceID()) native[sh.name] = sh;
                }
            }
            return native != null && native.TryGetValue(name, out var s) ? s : null;
        }

        public static void RecordBundle(AssetBundle bundle)
        {
            if (bundle == null || bundle.isStreamedSceneAssetBundle || !recordedBundles.Add(bundle)) return;
            // The game's own Addressables bundles ("<hash>.bundle", e.g. CJK fonts) are built for Metal already and are
            // large; loading all their assets on every (hot) load caused frame hitches.
            if (bundle.name.EndsWith(".bundle")) return;
            var assets = bundle.LoadAllAssets();
            foreach (var o in assets) if (o is Shader sh) foreign.Add(sh);
            foreach (var o in assets)
            {
                if (o is Material mat) Fix(mat, bundle.name);
                // Particle/trail materials are often only prefab dependencies, not listed bundle assets.
                else if (o is GameObject go)
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        foreach (var mm in r.sharedMaterials) Fix(mm, bundle.name);
            }
        }

        static readonly HashSet<string> ModOnlyShaders = new HashSet<string>
            { "Legacy Shaders/Particles/Additive", "Custom/Add", "Custom/Opacity2", "Standard" };
        static bool changed = true;   // first sweep after (re)load always refreshes the UI

        public static void Sweep()
        {
            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles()) RecordBundle(bundle);
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) Fix(m, null);
            if (!changed) return;
            changed = false;
            // UI canvases cache their draw batches; without this, swapped text keeps drawing with the old shader.
            foreach (var g in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Graphic>())
                if (g != null && g.isActiveAndEnabled) { g.SetMaterialDirty(); g.SetVerticesDirty(); }
            Canvas.ForceUpdateCanvases();
        }

        public static void Fix(Material m, string where)
        {
            if (m == null || m.shader == null) return;
            var sh = m.shader;
            var from = sh.name;
            // The game ships none of these, so any material using them came from a (D3D-only) mod bundle.
            // Rebuild up front: their isSupported only turns false after the first failed draw (a pink frame).
            if (ModOnlyShaders.Contains(from))
            {
                if (!Rebuild(m, from)) { Report($"waiting for a Metal shader for {from} ({m.name})"); return; }
                changed = true;
                Report($"{(where ?? "runtime")}/{m.name}: {from}#{sh.GetInstanceID()} -> {m.shader.name}#{m.shader.GetInstanceID()} (rebuilt)");
                return;
            }
            var same = Native(from);
            if (same != null)
            {
                if (same == sh) return;
                m.shader = same;
            }
            else if (!IsForeign(sh)) return;
            else if (!Rebuild(m, from)) { Report($"waiting for a Metal shader for {from} ({m.name})"); return; }
            changed = true;
            Report($"{(where ?? "runtime")}/{m.name}: {from}#{sh.GetInstanceID()} -> {m.shader.name}#{m.shader.GetInstanceID()}");
        }

        static void Report(string msg) { if (reported.Add(msg)) Log?.LogInfo(msg); }

        // BlendMode values (UnityEngine.Rendering.BlendMode): Zero=0 One=1 SrcAlpha=5 OneMinusSrcAlpha=10
        static bool Rebuild(Material m, string name)
        {
            var unlit = Native("Particles/Standard Unlit");
            if (unlit == null) return false;
            Texture tex; Color color; int src, dst; bool zwrite;
            switch (name)
            {
                case "Legacy Shaders/Particles/Additive":   // Blend SrcAlpha One; col = 2 * tint * vertex * tex
                    tex = Tex(m, "_MainTex"); color = (m.HasProperty("_TintColor") ? m.GetColor("_TintColor") : new Color(.5f, .5f, .5f, .5f)) * 2f;
                    src = 5; dst = 1; zwrite = false; break;
                case "Custom/Add":                          // Blend One One
                    tex = Tex(m, "_MainTexture"); color = Color.white; src = 1; dst = 1; zwrite = false; break;
                case "Custom/Opacity2":                     // Blend One Zero, ZWrite On (opaque)
                    tex = Tex(m, "_MainTexture"); color = Color.white; src = 1; dst = 0; zwrite = true; break;
                case "Standard":                            // keep the material's own blend setup
                    tex = Tex(m, "_MainTex"); color = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                    src = m.HasProperty("_SrcBlend") ? (int)m.GetFloat("_SrcBlend") : 1;
                    dst = m.HasProperty("_DstBlend") ? (int)m.GetFloat("_DstBlend") : 0;
                    zwrite = !m.HasProperty("_ZWrite") || m.GetFloat("_ZWrite") > 0.5f; break;
                default:
                    var sprites = Native("Sprites/Default");
                    if (sprites == null) return false;
                    m.shader = sprites; return true;
            }
            int queue = m.renderQueue;
            m.shader = unlit;
            m.shaderKeywords = new string[0];
            if (tex != null) m.SetTexture("_MainTex", tex);
            m.SetColor("_Color", color);
            m.SetFloat("_BlendOp", 0);
            m.SetFloat("_SrcBlend", src);
            m.SetFloat("_DstBlend", dst);
            m.SetFloat("_ZWrite", zwrite ? 1 : 0);
            m.SetFloat("_Cull", 0);
            bool transparent = !(src == 1 && dst == 0);
            if (transparent)
            {
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_ALPHABLEND_ON");
                m.renderQueue = queue >= 2500 ? queue : 3000;
            }
            else
            {
                m.SetOverrideTag("RenderType", "Opaque");
                m.renderQueue = queue > 0 && queue < 2500 ? queue : 2000;
            }
            return true;
        }

        static Texture Tex(Material m, string prop) => m.HasProperty(prop) ? m.GetTexture(prop) : null;
    }

    // Fix bundle materials the moment a bundle loads, before anything using them is drawn.
    [HarmonyPatch]
    internal static class AssetBundleLoad_Hook
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var mi in typeof(AssetBundle).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (mi.ReturnType == typeof(AssetBundle) && mi.Name.StartsWith("LoadFrom") && !mi.Name.EndsWith("Async")
                    && mi.GetMethodBody() != null)   // skip extern (native) entry points
                    yield return mi;
        }
        static void Postfix(AssetBundle __result) => ShaderFix.RecordBundle(__result);
    }

    // Mods also build materials at runtime from bundle shaders (new Material(shader) / new Material(mat)).
    [HarmonyPatch(typeof(Material), MethodType.Constructor, typeof(Shader))]
    internal static class MaterialCtorShader_Hook { static void Postfix(Material __instance) => ShaderFix.Fix(__instance, "new"); }

    [HarmonyPatch(typeof(Material), MethodType.Constructor, typeof(Material))]
    internal static class MaterialCtorMaterial_Hook { static void Postfix(Material __instance) => ShaderFix.Fix(__instance, "copy"); }

    // Re-run the sweep regularly: mods instantiate prefabs / create material instances at runtime.
    internal class ShaderFixRunner : MonoBehaviour
    {
        float next;
        void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            ShaderFix.Sweep();
        }
    }
}
