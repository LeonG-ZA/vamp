using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sanguine.Client
{
    /// <summary>
    /// Builds the visual city from the shared <see cref="CityDressing"/>: textured buildings with lit
    /// facades, sidewalks, wet roads with markings, Poly Haven street props and several hundred real lights.
    /// Geometry is chunked per city block so frustum culling stays effective.
    /// </summary>
    public static class CityBuilder
    {
        private static readonly Color White = Color.white;

        public static GameObject Build(CityLayout city)
        {
            ArtLibrary.Load();
            CityDressing dressing = CityDressing.Create(city);

            var root = new GameObject("City (seed " + city.Seed + ")");
            Transform chunksRoot = new GameObject("Blocks").transform;
            chunksRoot.SetParent(root.transform, false);
            Transform propsRoot = new GameObject("Props").transform;
            propsRoot.SetParent(root.transform, false);
            Transform lightsRoot = new GameObject("Lights").transform;
            lightsRoot.SetParent(root.transform, false);

            System.Func<string, float> tile = ArtLibrary.Tile;
            System.Func<string, Color, float, Material> material = (n, tint, e) => ArtLibrary.Surface(n, tint, e);

            // One mesh kit per city block, plus roads and the skyline.
            var kits = new MeshKit[CityLayout.Blocks, CityLayout.Blocks];
            var colliders = new List<Bounds>[CityLayout.Blocks, CityLayout.Blocks];
            for (int x = 0; x < CityLayout.Blocks; x++)
                for (int z = 0; z < CityLayout.Blocks; z++)
                {
                    kits[x, z] = new MeshKit(tile);
                    colliders[x, z] = new List<Bounds>();
                }
            var roads = new MeshKit(tile);
            var markings = new MeshKit(tile);
            var skyline = new MeshKit(tile);
            var skylineColliders = new List<Bounds>();

            // Roads and other ground surfaces.
            foreach (SurfacePiece s in dressing.Surfaces)
            {
                int bx, bz;
                if (s.Y > 0.01f && BlockOf((s.MinX + s.MaxX) * 0.5f, (s.MinZ + s.MaxZ) * 0.5f, out bx, out bz))
                    kits[bx, bz].Floor(s.Material, White, 0f, s.MinX, s.MinZ, s.MaxX, s.MaxZ, s.Y);
                else
                    roads.Floor(s.Material, White, 0f, s.MinX, s.MinZ, s.MaxX, s.MaxZ, s.Y);
            }
            foreach (Stripe st in dressing.Stripes)
                markings.Floor(SurfaceNames.Paint, White, 0f, st.CX - st.SX * 0.5f, st.CZ - st.SZ * 0.5f, st.CX + st.SX * 0.5f, st.CZ + st.SZ * 0.5f, 0.012f);

            // Raised sidewalks, plazas and parks.
            foreach (SlabPiece s in dressing.Slabs)
            {
                int bx, bz;
                BlockOf((s.MinX + s.MaxX) * 0.5f, (s.MinZ + s.MaxZ) * 0.5f, out bx, out bz);
                MeshKit k = kits[bx, bz];
                k.Floor(s.TopMaterial, White, 0f, s.MinX, s.MinZ, s.MaxX, s.MaxZ, s.Height);
                k.Ring(s.SideMaterial, White, 0f, s.MinX, s.MinZ, s.MaxX, s.MaxZ, 0f, s.Height, 0f);
                colliders[bx, bz].Add(new Bounds(new Vector3((s.MinX + s.MaxX) * 0.5f, s.Height * 0.5f, (s.MinZ + s.MaxZ) * 0.5f),
                                                 new Vector3(s.MaxX - s.MinX, s.Height, s.MaxZ - s.MinZ)));
            }

            // Buildings.
            foreach (DressedBuilding d in dressing.Buildings)
            {
                int bx, bz;
                BlockOf(d.Def.CX, d.Def.CZ, out bx, out bz);
                AddBuilding(kits[bx, bz], d);
                colliders[bx, bz].Add(new Bounds(new Vector3(d.Def.CX, d.BaseY + d.Def.Height * 0.5f, d.Def.CZ),
                                                 new Vector3(d.Def.SX, d.Def.Height, d.Def.SZ)));
            }

            // Boxes: containers, pillars, neon signs, lit shop windows, skyline towers.
            var rotatedColliders = new List<KeyValuePair<Bounds, float>>();
            foreach (BoxPiece p in dressing.Boxes)
            {
                var c = new Vector3(p.CX, p.CY, p.CZ);
                var size = new Vector3(p.SX, p.SY, p.SZ);
                var tint = new Color(p.R, p.G, p.B);
                int bx, bz;
                bool inBlock = BlockOf(p.CX, p.CZ, out bx, out bz);
                MeshKit k = inBlock ? kits[bx, bz] : skyline;
                k.Box(p.Material, tint, p.Emission, c, size, p.Yaw);
                if (p.Collider)
                {
                    if (Mathf.Abs(p.Yaw) < 0.01f) (inBlock ? colliders[bx, bz] : skylineColliders).Add(new Bounds(c, size));
                    else rotatedColliders.Add(new KeyValuePair<Bounds, float>(new Bounds(c, size), p.Yaw));
                }
            }

            for (int x = 0; x < CityLayout.Blocks; x++)
                for (int z = 0; z < CityLayout.Blocks; z++)
                {
                    GameObject chunk = kits[x, z].Build("Block " + x + "," + z, chunksRoot, material);
                    AddColliders(chunk, colliders[x, z]);
                }
            GameObject roadGo = roads.Build("Roads", root.transform, material, false);
            var ground = roadGo.AddComponent<BoxCollider>();
            ground.center = new Vector3(0f, -0.5f, 0f);
            ground.size = new Vector3(CityLayout.MapHalf * 2f + 60f, 1f, CityLayout.MapHalf * 2f + 60f);
            markings.Build("Road markings", root.transform, material, false);
            GameObject sky = skyline.Build("Skyline", root.transform, material);
            AddColliders(sky, skylineColliders);
            foreach (KeyValuePair<Bounds, float> rc in rotatedColliders)
            {
                var go = new GameObject("Collider");
                go.transform.SetParent(root.transform, false);
                go.transform.position = rc.Key.center;
                go.transform.rotation = Quaternion.Euler(0f, rc.Value, 0f);
                go.AddComponent<BoxCollider>().size = rc.Key.size;
            }

            // Props.
            foreach (PropPlacement p in dressing.Props) SpawnProp(p, propsRoot);

            // Lights.
            var director = root.AddComponent<CityLights>();
            foreach (LightPlacement l in dressing.Lights) director.Add(CreateLight(l, lightsRoot), l.Kind);

            // Static geometry batching (procedural meshes only; imported props stay instanced).
            StaticBatchingUtility.Combine(chunksRoot.gameObject);
            return root;
        }

        private static bool BlockOf(float x, float z, out int bx, out int bz)
        {
            bx = Mathf.FloorToInt((x - CityLayout.Origin) / CityLayout.Pitch);
            bz = Mathf.FloorToInt((z - CityLayout.Origin) / CityLayout.Pitch);
            bool inside = bx >= 0 && bz >= 0 && bx < CityLayout.Blocks && bz < CityLayout.Blocks;
            bx = Mathf.Clamp(bx, 0, CityLayout.Blocks - 1);
            bz = Mathf.Clamp(bz, 0, CityLayout.Blocks - 1);
            return inside;
        }

        private static void AddColliders(GameObject go, List<Bounds> boxes)
        {
            foreach (Bounds b in boxes)
            {
                var c = go.AddComponent<BoxCollider>();
                c.center = b.center;
                c.size = b.size;
            }
        }

        private static void AddBuilding(MeshKit k, DressedBuilding d)
        {
            BuildingDef b = d.Def;
            float minX = b.CX - b.SX * 0.5f, maxX = b.CX + b.SX * 0.5f, minZ = b.CZ - b.SZ * 0.5f, maxZ = b.CZ + b.SZ * 0.5f;
            float y0 = d.BaseY, top = d.Top;
            float band = d.GroundMaterial != null ? d.BandHeight : 0f;

            if (band > 0f) k.Ring(d.GroundMaterial, White, 0f, minX, minZ, maxX, maxZ, y0, y0 + band, y0, d.UOffset);
            float upper = y0 + band, wallTop = top + d.ParapetHeight;
            k.Ring(d.UpperMaterial, White, 0f, minX, minZ, maxX, maxZ, upper, wallTop, upper, d.UOffset, d.VOffset);

            if (d.ParapetHeight > 0f)
            {
                const float inset = 0.3f;
                k.Ring(d.TrimMaterial, White, 0f, minX + inset, minZ + inset, maxX - inset, maxZ - inset, top, wallTop, top, 0f, 0f, true);
                k.Floor(d.TrimMaterial, White, 0f, minX, minZ, maxX, minZ + inset, wallTop);
                k.Floor(d.TrimMaterial, White, 0f, minX, maxZ - inset, maxX, maxZ, wallTop);
                k.Floor(d.TrimMaterial, White, 0f, minX, minZ + inset, minX + inset, maxZ - inset, wallTop);
                k.Floor(d.TrimMaterial, White, 0f, maxX - inset, minZ + inset, maxX, maxZ - inset, wallTop);
                k.Floor(d.RoofMaterial, White, 0f, minX + inset, minZ + inset, maxX - inset, maxZ - inset, top);
            }
            else k.Floor(d.RoofMaterial, White, 0f, minX, minZ, maxX, maxZ, top);

            if (d.Fascia && band > 0f)
            {
                // A protruding sign band / cornice between the shop fronts and the upper floors.
                float fy0 = y0 + band - 0.55f, fy1 = y0 + band, o = 0.22f;
                k.Ring(d.TrimMaterial, White, 0f, minX - o, minZ - o, maxX + o, maxZ + o, fy0, fy1, fy0);
                k.Floor(d.TrimMaterial, White, 0f, minX - o, minZ - o, maxX + o, maxZ + o, fy1);
                k.Floor(d.TrimMaterial, White, 0f, minX - o, minZ - o, maxX + o, maxZ + o, fy0, true);
            }
        }

        private static void SpawnProp(PropPlacement p, Transform parent)
        {
            GameObject prefab = ArtLibrary.PropPrefab(p.Id);
            PropInfo info = PropCatalog.Get(p.Id);
            GameObject go;
            if (prefab != null)
            {
                go = Object.Instantiate(prefab, parent);
                go.name = p.Id;
            }
            else
            {
                go = FallbackProp(p.Id, parent);
                if (go == null) return;
            }
            go.transform.position = new Vector3(p.X, p.Y, p.Z);
            go.transform.rotation = Quaternion.Euler(0f, p.Yaw, 0f);
            go.transform.localScale = new Vector3(p.ScaleX, p.ScaleY, p.ScaleZ);

            float size = info != null ? Mathf.Max(info.Width * p.ScaleX, info.Height * p.ScaleY, info.Depth * p.ScaleZ) : 1f;
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                // Cull small props once they shrink to a few pixels.
                var lod = go.AddComponent<LODGroup>();
                float cull = size < 1.2f ? 0.018f : (size < 3f ? 0.007f : 0.002f);
                lod.SetLODs(new[] { new LOD(cull, renderers) });
                lod.RecalculateBounds();
            }
            if (p.Collider && info != null)
            {
                var col = go.AddComponent<BoxCollider>();
                col.center = new Vector3((info.MinX + info.MaxX) * 0.5f, (info.MinY + info.MaxY) * 0.5f, (info.MinZ + info.MaxZ) * 0.5f);
                col.size = new Vector3(info.Width, info.Height, info.Depth);
            }
        }

        /// <summary>Simple stand-ins when the imported props are unavailable (e.g. glTFast not installed).</summary>
        private static GameObject FallbackProp(string id, Transform parent)
        {
            var go = new GameObject(id + " (fallback)");
            go.transform.SetParent(parent, false);
            Transform t = go.transform;
            switch (id)
            {
                case "street_lamp":
                    Mats.Prim(PrimitiveType.Cylinder, t, new Vector3(0, 1.8f, 0), new Vector3(0.14f, 1.8f, 0.14f), new Color(0.08f, 0.08f, 0.08f), false);
                    Mats.Prim(PrimitiveType.Sphere, t, new Vector3(0, 3.3f, 0), Vector3.one * 0.45f, new Color(1f, 0.75f, 0.45f), false, true);
                    break;
                case "covered_car":
                    Mats.Prim(PrimitiveType.Cube, t, new Vector3(0, 0.7f, 0), new Vector3(1.8f, 1.2f, 4.3f), new Color(0.12f, 0.13f, 0.15f), false);
                    break;
                case "security_light":
                    Mats.Prim(PrimitiveType.Cube, t, new Vector3(0, 0.2f, 0.2f), new Vector3(0.3f, 0.3f, 0.4f), new Color(0.1f, 0.1f, 0.1f), false);
                    break;
                case "shutter_door":
                case "shutter_door_graffiti":
                    Mats.Prim(PrimitiveType.Cube, t, new Vector3(0, 1.2f, 0.05f), new Vector3(1.08f, 2.4f, 0.1f), new Color(0.35f, 0.36f, 0.38f), false);
                    break;
                case "fire_barrel":
                case "barrel":
                case "trash_can":
                case "trash_can_rusty":
                    Mats.Prim(PrimitiveType.Cylinder, t, new Vector3(0, 0.45f, 0), new Vector3(0.55f, 0.45f, 0.55f), new Color(0.25f, 0.2f, 0.17f), false);
                    break;
                default:
                    Object.Destroy(go);
                    return null;
            }
            return go;
        }

        private static Light CreateLight(LightPlacement p, Transform parent)
        {
            var go = new GameObject(p.Kind + " light");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(p.X, p.Y, p.Z);
            Light l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(p.R, p.G, p.B);
            l.range = p.Range;
            l.intensity = p.Intensity;
            l.shadows = LightShadows.None;
            l.bounceIntensity = 0f;
            if (p.Kind == LightKind.Fire) go.AddComponent<LightFlicker>();
            return l;
        }
    }

    /// <summary>Keeps hundreds of city lights affordable: distant lights are switched off and the few lamps
    /// nearest the camera cast soft shadows (characters and props throw real shadows under street lamps).</summary>
    public sealed class CityLights : MonoBehaviour
    {
        public int ShadowCasters = 4;
        public float ActiveRadius = 140f;

        private readonly List<Light> _lights = new List<Light>();
        private readonly List<LightKind> _kinds = new List<LightKind>();
        private float _next;

        public void Add(Light l, LightKind kind)
        {
            _lights.Add(l);
            _kinds.Add(kind);
        }

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.4f;
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 c = cam.transform.position;

            // Nearest shadow-casting candidates (street lamps and floodlights).
            var nearest = new List<KeyValuePair<float, Light>>();
            for (int i = 0; i < _lights.Count; i++)
            {
                Light l = _lights[i];
                if (l == null) continue;
                float d = (l.transform.position - c).sqrMagnitude;
                bool on = d < ActiveRadius * ActiveRadius;
                if (l.enabled != on) l.enabled = on;
                l.shadows = LightShadows.None;
                if (on && (_kinds[i] == LightKind.Lamp || _kinds[i] == LightKind.Security))
                    nearest.Add(new KeyValuePair<float, Light>(d, l));
            }
            nearest.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < nearest.Count && i < ShadowCasters; i++)
            {
                nearest[i].Value.shadows = LightShadows.Soft;
                nearest[i].Value.shadowStrength = 0.85f;
            }
        }
    }

    /// <summary>Fire-barrel flicker.</summary>
    public sealed class LightFlicker : MonoBehaviour
    {
        private Light _light;
        private float _base, _seed;

        private void Awake()
        {
            _light = GetComponent<Light>();
            _base = _light.intensity;
            _seed = Random.value * 100f;
        }

        private void Update()
        {
            float t = Time.time * 7f + _seed;
            _light.intensity = _base * (0.75f + 0.25f * Mathf.PerlinNoise(t, _seed) + 0.1f * Mathf.Sin(t * 3.3f));
        }
    }
}
