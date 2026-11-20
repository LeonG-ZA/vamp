using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sanguine.Client
{
    /// <summary>
    /// Accumulates textured quads grouped by material and turns them into one mesh with a submesh per
    /// material. UVs are in real-world units divided by each texture set's tile size, so bricks, windows
    /// and paving stones appear at their true scale on any wall or floor.
    /// </summary>
    public sealed class MeshKit
    {
        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<string> _materialOrder = new List<string>();
        private readonly Dictionary<string, List<int>> _tris = new Dictionary<string, List<int>>();
        private readonly Dictionary<string, Color> _tints = new Dictionary<string, Color>();
        private readonly Dictionary<string, float> _emission = new Dictionary<string, float>();
        private readonly Func<string, float> _tile;

        public MeshKit(Func<string, float> tileSize)
        {
            _tile = tileSize;
        }

        public bool IsEmpty { get { return _verts.Count == 0; } }

        private static string Key(string material, Color tint, float emission)
        {
            return material + "#" + ColorUtility.ToHtmlStringRGB(tint) + "#" + Mathf.RoundToInt(emission * 100f);
        }

        private List<int> Bucket(string material, Color tint, float emission)
        {
            string key = Key(material, tint, emission);
            List<int> list;
            if (!_tris.TryGetValue(key, out list))
            {
                list = new List<int>();
                _tris[key] = list;
                _materialOrder.Add(key);
                _tints[key] = tint;
                _emission[key] = emission;
            }
            return list;
        }

        /// <summary>Adds a quad a-b-c-d given clockwise as seen from its front side.</summary>
        public void Quad(string material, Color tint, float emission, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                         Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            List<int> tris = Bucket(material, tint, emission);
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            int i = _verts.Count;
            _verts.Add(a); _verts.Add(b); _verts.Add(c); _verts.Add(d);
            _normals.Add(n); _normals.Add(n); _normals.Add(n); _normals.Add(n);
            _uvs.Add(ua); _uvs.Add(ub); _uvs.Add(uc); _uvs.Add(ud);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        /// <summary>A vertical wall from p0 to p1 (left to right seen from outside) between heights y0 and y1.
        /// <paramref name="u0"/> is the distance already travelled along the facade (for continuous texturing).</summary>
        public void Wall(string material, Color tint, float emission, Vector2 p0, Vector2 p1, float y0, float y1,
                         float u0, float vBase, float uOffset = 0f, float vOffset = 0f)
        {
            float t = _tile(material);
            float len = Vector2.Distance(p0, p1);
            float ua = u0 / t + uOffset, ub = (u0 + len) / t + uOffset;
            float va = (y0 - vBase) / t + vOffset, vb = (y1 - vBase) / t + vOffset;
            Quad(material, tint, emission,
                 new Vector3(p0.x, y0, p0.y), new Vector3(p0.x, y1, p0.y), new Vector3(p1.x, y1, p1.y), new Vector3(p1.x, y0, p1.y),
                 new Vector2(ua, va), new Vector2(ua, vb), new Vector2(ub, vb), new Vector2(ub, va));
        }

        /// <summary>A horizontal rectangle facing up (or down) with world-space UVs.</summary>
        public void Floor(string material, Color tint, float emission, float minX, float minZ, float maxX, float maxZ, float y, bool facingDown = false)
        {
            float t = _tile(material);
            var a = new Vector3(minX, y, minZ); var b = new Vector3(minX, y, maxZ);
            var c = new Vector3(maxX, y, maxZ); var d = new Vector3(maxX, y, minZ);
            Vector2 ua = new Vector2(minX / t, minZ / t), ub = new Vector2(minX / t, maxZ / t);
            Vector2 uc = new Vector2(maxX / t, maxZ / t), ud = new Vector2(maxX / t, minZ / t);
            if (facingDown) Quad(material, tint, emission, a, d, c, b, ua, ud, uc, ub);
            else Quad(material, tint, emission, a, b, c, d, ua, ub, uc, ud);
        }

        /// <summary>Walls of an axis-aligned rectangle in the order -Z, +X, +Z, -X (outward facing).</summary>
        public void Ring(string material, Color tint, float emission, float minX, float minZ, float maxX, float maxZ,
                         float y0, float y1, float vBase, float uOffset = 0f, float vOffset = 0f, bool inward = false)
        {
            var c0 = new Vector2(minX, minZ); var c1 = new Vector2(maxX, minZ);
            var c2 = new Vector2(maxX, maxZ); var c3 = new Vector2(minX, maxZ);
            Vector2[] pts = inward ? new[] { c1, c0, c3, c2, c1 } : new[] { c0, c1, c2, c3, c0 };
            float u = 0f;
            for (int i = 0; i < 4; i++)
            {
                Wall(material, tint, emission, pts[i], pts[i + 1], y0, y1, u, vBase, uOffset, vOffset);
                u += Vector2.Distance(pts[i], pts[i + 1]);
            }
        }

        /// <summary>A closed box rotated by <paramref name="yaw"/> around its centre.</summary>
        public void Box(string material, Color tint, float emission, Vector3 center, Vector3 size, float yaw)
        {
            Quaternion q = Quaternion.Euler(0f, yaw, 0f);
            Vector3 h = size * 0.5f;
            float t = _tile(material);
            Func<float, float, float, Vector3> P = (x, y, z) => center + q * new Vector3(x, y, z);
            // Sides: -Z, +X, +Z, -X (each clockwise from outside), u along the face, v up.
            Vector3[][] faces =
            {
                new[] { P(-h.x, -h.y, -h.z), P(-h.x, h.y, -h.z), P(h.x, h.y, -h.z), P(h.x, -h.y, -h.z) },
                new[] { P(h.x, -h.y, -h.z), P(h.x, h.y, -h.z), P(h.x, h.y, h.z), P(h.x, -h.y, h.z) },
                new[] { P(h.x, -h.y, h.z), P(h.x, h.y, h.z), P(-h.x, h.y, h.z), P(-h.x, -h.y, h.z) },
                new[] { P(-h.x, -h.y, h.z), P(-h.x, h.y, h.z), P(-h.x, h.y, -h.z), P(-h.x, -h.y, -h.z) },
            };
            float[] widths = { size.x, size.z, size.x, size.z };
            for (int i = 0; i < 4; i++)
            {
                float w = widths[i] / t, hh = size.y / t;
                Quad(material, tint, emission, faces[i][0], faces[i][1], faces[i][2], faces[i][3],
                     new Vector2(0, 0), new Vector2(0, hh), new Vector2(w, hh), new Vector2(w, 0));
            }
            float tw = size.x / t, td = size.z / t;
            Quad(material, tint, emission, P(-h.x, h.y, -h.z), P(-h.x, h.y, h.z), P(h.x, h.y, h.z), P(h.x, h.y, -h.z),
                 new Vector2(0, 0), new Vector2(0, td), new Vector2(tw, td), new Vector2(tw, 0));
            Quad(material, tint, emission, P(-h.x, -h.y, -h.z), P(h.x, -h.y, -h.z), P(h.x, -h.y, h.z), P(-h.x, -h.y, h.z),
                 new Vector2(0, 0), new Vector2(tw, 0), new Vector2(tw, td), new Vector2(0, td));
        }

        /// <summary>Creates a GameObject with the accumulated mesh and per-submesh materials.</summary>
        public GameObject Build(string name, Transform parent, Func<string, Color, float, Material> material, bool castShadows = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            if (IsEmpty) return go;

            var mesh = new Mesh { name = name };
            if (_verts.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(_verts);
            mesh.SetNormals(_normals);
            mesh.SetUVs(0, _uvs);
            mesh.subMeshCount = _materialOrder.Count;
            var mats = new Material[_materialOrder.Count];
            for (int i = 0; i < _materialOrder.Count; i++)
            {
                string key = _materialOrder[i];
                mesh.SetTriangles(_tris[key], i);
                string matName = key.Substring(0, key.IndexOf('#'));
                mats[i] = material(matName, _tints[key], _emission[key]);
            }
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return go;
        }
    }
}
