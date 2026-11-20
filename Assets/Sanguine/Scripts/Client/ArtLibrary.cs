using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>
    /// Access to the generated art in Resources (see Tools/assets/build_assets.py): texture sets for
    /// roads, sidewalks, walls and lit facades, Poly Haven props (GLB, imported by glTFast) and HDR skies.
    /// Everything degrades gracefully: missing art falls back to flat colours and simple shapes.
    /// </summary>
    public static class ArtLibrary
    {
        [Serializable]
        public sealed class TextureEntry
        {
            public string name;
            public float tile = 2f;
            public bool emission;
            public float gloss = 1f;
            public string use;
        }

        [Serializable]
        public sealed class SkyEntry
        {
            public string night;
            public string day;
            public float moonU = 0.5f;
            public float moonV = 0.85f;
        }

#pragma warning disable 0649 // populated by JsonUtility
        [Serializable]
        private sealed class Manifest
        {
            public int version;
            public TextureEntry[] textures;
            public SkyEntry sky;
        }
#pragma warning restore 0649

        private static bool _loaded;
        private static readonly Dictionary<string, TextureEntry> Sets = new Dictionary<string, TextureEntry>();
        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();
        private static readonly Dictionary<string, GameObject> Props = new Dictionary<string, GameObject>();

        /// <summary>Rain-slicked streets: roads are glossier than their dry scans.</summary>
        public const float WetStreetGloss = 2.4f;

        public static SkyEntry Sky { get; private set; }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            TextAsset ta = Resources.Load<TextAsset>("art_manifest");
            if (ta == null) return;
            try
            {
                Manifest m = JsonUtility.FromJson<Manifest>(ta.text);
                if (m.textures != null) foreach (TextureEntry t in m.textures) Sets[t.name] = t;
                Sky = m.sky;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Sanguine] Could not read art_manifest: " + e.Message);
            }
        }

        /// <summary>True when the generated texture sets are present.</summary>
        public static bool HasTextures
        {
            get { Load(); return Sets.Count > 0; }
        }

        /// <summary>Real-world size (metres) of one repeat of a texture set.</summary>
        public static float Tile(string name)
        {
            Load();
            TextureEntry e;
            if (Sets.TryGetValue(name, out e) && e.tile > 0.01f) return e.tile;
            return 2f;
        }

        /// <summary>Material for a surface name from <see cref="SurfaceNames"/>, tinted.</summary>
        public static Material Surface(string name, Color tint, float emission = 0f)
        {
            Load();
            if (name == SurfaceNames.Glow) return Mats.Glow(tint, emission);
            if (name == SurfaceNames.Paint) return Mats.Get(new Color(0.72f, 0.72f, 0.68f), false, 0.35f);

            string key = name + "|" + ColorUtility.ToHtmlStringRGB(tint) + "|" + Mathf.RoundToInt(emission * 10f);
            Material m;
            if (Materials.TryGetValue(key, out m) && m != null) return m;

            TextureEntry e;
            Texture2D albedo = null, normal = null, mask = null, glow = null;
            if (Sets.TryGetValue(name, out e))
            {
                string p = "Textures/" + name + "/" + name;
                albedo = Resources.Load<Texture2D>(p + "_albedo");
                normal = Resources.Load<Texture2D>(p + "_normal");
                mask = Resources.Load<Texture2D>(p + "_mask");
                if (e.emission) glow = Resources.Load<Texture2D>(p + "_emission");
            }
            if (albedo == null)
            {
                m = Mats.Get(FallbackColor(name) * tint);
            }
            else
            {
                float gloss = e.gloss;
                if (name == SurfaceNames.Asphalt || name == SurfaceNames.Cobblestone) gloss *= WetStreetGloss;
                float glowIntensity = glow != null ? (emission > 0f ? emission : 1.35f) : 0f;
                m = Mats.Pbr("pbr:" + key, albedo, normal, mask, glow, tint, gloss, glowIntensity);
            }
            Materials[key] = m;
            return m;
        }

        /// <summary>Approximate colours used when the texture sets are missing.</summary>
        public static Color FallbackColor(string name)
        {
            switch (name)
            {
                case SurfaceNames.Asphalt: return new Color(0.08f, 0.08f, 0.09f);
                case SurfaceNames.Cobblestone: return new Color(0.2f, 0.19f, 0.18f);
                case SurfaceNames.Pavement:
                case SurfaceNames.CobblePavement:
                case SurfaceNames.SanctumPlaza: return new Color(0.24f, 0.23f, 0.23f);
                case SurfaceNames.HavenPlaza: return new Color(0.16f, 0.13f, 0.14f);
                case SurfaceNames.Grass: return new Color(0.09f, 0.16f, 0.07f);
                case SurfaceNames.Roof: return new Color(0.1f, 0.1f, 0.1f);
                case SurfaceNames.Corrugated: return new Color(0.4f, 0.42f, 0.44f);
                case SurfaceNames.RustyCorrugated: return new Color(0.35f, 0.18f, 0.1f);
                case SurfaceNames.FactoryBrick:
                case SurfaceNames.RedBrick: return new Color(0.4f, 0.22f, 0.17f);
                case SurfaceNames.DarkBrick: return new Color(0.16f, 0.13f, 0.12f);
                case SurfaceNames.Container: return new Color(0.2f, 0.4f, 0.22f);
                case SurfaceNames.Sandstone: return new Color(0.55f, 0.5f, 0.42f);
                case SurfaceNames.Concrete: return new Color(0.42f, 0.42f, 0.4f);
                default: return new Color(0.2f, 0.2f, 0.23f);
            }
        }

        /// <summary>The imported prop (GLB via glTFast) or null when unavailable.</summary>
        public static GameObject PropPrefab(string id)
        {
            GameObject go;
            if (Props.TryGetValue(id, out go)) return go;
            go = Resources.Load<GameObject>("Props/" + id);
            Props[id] = go;
            return go;
        }

        public static Texture2D SkyTexture(bool night)
        {
            Load();
            if (Sky == null) return null;
            string path = night ? Sky.night : Sky.day;
            return string.IsNullOrEmpty(path) ? null : Resources.Load<Texture2D>(path);
        }

        /// <summary>World direction towards the moon for a sky rotated by <paramref name="rotationDeg"/>
        /// (inverse of the lat-long mapping in SanguineSky.shader).</summary>
        public static Vector3 SkyDirection(float u, float v, float rotationDeg)
        {
            float lon = (u - 0.5f) * 2f * Mathf.PI, lat = (v - 0.5f) * Mathf.PI;
            var d = new Vector3(Mathf.Sin(lon) * Mathf.Cos(lat), Mathf.Sin(lat), Mathf.Cos(lon) * Mathf.Cos(lat));
            float r = rotationDeg * Mathf.Deg2Rad, s = Mathf.Sin(r), c = Mathf.Cos(r);
            return new Vector3(d.x * c + d.z * s, d.y, -d.x * s + d.z * c).normalized;
        }
    }
}
