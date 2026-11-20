using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sanguine.Client
{
    /// <summary>Creates and caches materials at runtime. Physically based materials are built from the
    /// generated texture sets (albedo, normal, mask, emission); template materials in Resources keep the
    /// needed shader variants in player builds. Works with the Built-in pipeline (Standard) and URP (Lit).</summary>
    public static class Mats
    {
        private static readonly Dictionary<string, Material> Cache = new Dictionary<string, Material>();

        public static bool IsUrp { get { return GraphicsSettings.defaultRenderPipeline != null; } }

        private static Material FromTemplate(bool pbr, bool emissive)
        {
            if (!IsUrp)
            {
                RenderAssets ra = RenderAssets.Instance;
                if (ra != null)
                {
                    Material t = pbr ? (emissive ? ra.pbrEmissiveTemplate : ra.pbrTemplate)
                                     : (emissive ? ra.emissiveTemplate : ra.plainTemplate);
                    if (t != null) return new Material(t);
                }
            }
            Shader s = IsUrp ? Shader.Find("Universal Render Pipeline/Lit") : null;
            if (s == null) s = Shader.Find("Standard");
            return new Material(s);
        }

        private static void SetColor(Material m, Color c)
        {
            m.color = c;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        }

        private static void SetSmoothness(Material m, float smoothness)
        {
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        }

        private static void SetEmission(Material m, Texture map, Color color)
        {
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            if (map != null && m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", map);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", color);
        }

        /// <summary>A flat-coloured material (optionally glowing).</summary>
        public static Material Get(Color c, bool emissive = false, float smoothness = 0.15f)
        {
            string key = "c" + ColorUtility.ToHtmlStringRGBA(c) + (emissive ? "e" : "") + Mathf.RoundToInt(smoothness * 100);
            Material m;
            if (Cache.TryGetValue(key, out m) && m != null) return m;
            m = FromTemplate(false, emissive);
            SetColor(m, c);
            SetSmoothness(m, smoothness);
            if (emissive) SetEmission(m, null, c * 1.6f);
            Cache[key] = m;
            return m;
        }

        /// <summary>A glowing surface (neon, lit windows) with an HDR intensity for bloom.</summary>
        public static Material Glow(Color c, float intensity)
        {
            string key = "g" + ColorUtility.ToHtmlStringRGB(c) + Mathf.RoundToInt(intensity * 100);
            Material m;
            if (Cache.TryGetValue(key, out m) && m != null) return m;
            m = FromTemplate(false, true);
            SetColor(m, c * 0.2f);
            SetSmoothness(m, 0.6f);
            SetEmission(m, null, c * intensity);
            Cache[key] = m;
            return m;
        }

        /// <summary>A physically based material. <paramref name="mask"/> packs metallic (R),
        /// occlusion (G) and smoothness (A), the Standard shader's layout.</summary>
        public static Material Pbr(string key, Texture2D albedo, Texture2D normal, Texture2D mask, Texture2D emission,
                                   Color tint, float glossScale, float emissionIntensity)
        {
            Material m;
            if (Cache.TryGetValue(key, out m) && m != null) return m;
            bool emissive = emission != null && emissionIntensity > 0f;
            m = FromTemplate(true, emissive);
            m.name = key;
            SetColor(m, tint);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", albedo);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", albedo);
            if (normal != null && m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", normal);
                m.EnableKeyword("_NORMALMAP");
            }
            if (mask != null && m.HasProperty("_MetallicGlossMap"))
            {
                m.SetTexture("_MetallicGlossMap", mask);
                m.EnableKeyword(IsUrp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP");
                if (m.HasProperty("_OcclusionMap"))
                {
                    m.SetTexture("_OcclusionMap", mask);
                    if (IsUrp) m.EnableKeyword("_OCCLUSIONMAP");
                }
            }
            if (m.HasProperty("_GlossMapScale")) m.SetFloat("_GlossMapScale", glossScale);
            if (IsUrp) SetSmoothness(m, Mathf.Clamp01(0.5f * glossScale));
            if (emissive) SetEmission(m, emission, Color.white * emissionIntensity);
            Cache[key] = m;
            return m;
        }

        public static Color BloodlineColor(Bloodline b)
        {
            BloodlineDef d = GameData.GetBloodline(b);
            return new Color(d.R, d.G, d.B);
        }

        /// <summary>A Unity primitive with a material; used for fallbacks and simple details.</summary>
        public static GameObject Prim(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Color color,
                                      bool collider = true, bool emissive = false)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = Get(color, emissive);
            if (!collider)
            {
                Collider c = go.GetComponent<Collider>();
                if (c != null) Object.Destroy(c);
            }
            return go;
        }
    }
}
