using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sanguine.Client
{
    /// <summary>Creates and caches simple coloured materials at runtime so the project needs no art assets.
    /// Works with the Built-in pipeline (Standard shader) and URP (Lit shader).</summary>
    public static class Mats
    {
        private static readonly Dictionary<int, Material> Cache = new Dictionary<int, Material>();
        private static Material _base;

        private static Material Base
        {
            get
            {
                if (_base != null) return _base;
                if (GraphicsSettings.defaultRenderPipeline != null)
                {
                    Shader urp = Shader.Find("Universal Render Pipeline/Lit");
                    if (urp != null) _base = new Material(urp);
                }
                if (_base == null)
                {
                    // A material asset in Resources guarantees the Standard shader is included in player builds.
                    Material res = Resources.Load<Material>("SanguineBase");
                    _base = res != null ? new Material(res) : new Material(Shader.Find("Standard"));
                }
                return _base;
            }
        }

        public static Material Get(Color c, bool emissive = false, float smoothness = 0.15f)
        {
            int key = (Mathf.RoundToInt(c.r * 255) << 24) ^ (Mathf.RoundToInt(c.g * 255) << 16) ^
                      (Mathf.RoundToInt(c.b * 255) << 8) ^ Mathf.RoundToInt(smoothness * 100) ^ (emissive ? 1 << 30 : 0);
            Material m;
            if (Cache.TryGetValue(key, out m) && m != null) return m;

            m = new Material(Base);
            m.color = c;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (emissive)
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", c * 1.6f);
            }
            Cache[key] = m;
            return m;
        }

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

        public static Color BloodlineColor(Bloodline b)
        {
            BloodlineDef d = GameData.GetBloodline(b);
            return new Color(d.R, d.G, d.B);
        }
    }
}
