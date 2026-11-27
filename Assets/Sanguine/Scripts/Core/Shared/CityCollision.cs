using System;
using System.Collections.Generic;

namespace Sanguine
{
    /// <summary>
    /// Server-side knowledge of the city's solid geometry (building footprints, containers, pillars,
    /// the skyline) built from the same <see cref="CityDressing"/> the client renders. Provides point and
    /// line-of-sight queries and shortest routes along the street grid for NPCs.
    /// </summary>
    public sealed class CityCollision
    {
        private struct Rect
        {
            public float MinX, MinZ, MaxX, MaxZ;
        }

        private const float Cell = 10f;
        private const float Extent = CityLayout.MapHalf + 30f;
        private readonly int _cells;
        private readonly List<Rect> _rects = new List<Rect>();
        private readonly List<int>[] _grid;
        private readonly CityLayout _city;
        private readonly List<int> _scratch = new List<int>();

        public CityCollision(CityLayout city, CityDressing dressing)
        {
            _city = city;
            _cells = (int)Math.Ceiling(Extent * 2f / Cell);
            _grid = new List<int>[_cells * _cells];
            foreach (DressedBuilding b in dressing.Buildings)
                Add(b.Def.CX - b.Def.SX * 0.5f, b.Def.CZ - b.Def.SZ * 0.5f, b.Def.CX + b.Def.SX * 0.5f, b.Def.CZ + b.Def.SZ * 0.5f);
            foreach (BoxPiece p in dressing.Boxes)
            {
                if (!p.Collider || p.CY - p.SY * 0.5f > 1.5f) continue; // only obstacles at walking height
                float hx = p.SX * 0.5f, hz = p.SZ * 0.5f;
                if (Math.Abs(p.Yaw) > 0.01f) { float r = (float)Math.Sqrt(hx * hx + hz * hz); hx = hz = r; } // conservative
                Add(p.CX - hx, p.CZ - hz, p.CX + hx, p.CZ + hz);
            }
        }

        public int SolidCount { get { return _rects.Count; } }

        private int CellIndex(float v)
        {
            int c = (int)Math.Floor((v + Extent) / Cell);
            return c < 0 ? 0 : (c >= _cells ? _cells - 1 : c);
        }

        private void Add(float minX, float minZ, float maxX, float maxZ)
        {
            int id = _rects.Count;
            _rects.Add(new Rect { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ });
            for (int cx = CellIndex(minX); cx <= CellIndex(maxX); cx++)
                for (int cz = CellIndex(minZ); cz <= CellIndex(maxZ); cz++)
                {
                    int k = cx * _cells + cz;
                    if (_grid[k] == null) _grid[k] = new List<int>(4);
                    _grid[k].Add(id);
                }
        }

        /// <summary>True if the point lies inside solid geometry grown by <paramref name="margin"/>
        /// (a negative margin shrinks the solids, for lenient checks).</summary>
        public bool IsSolid(float x, float z, float margin = 0f)
        {
            int cx0 = CellIndex(x - Math.Abs(margin)), cx1 = CellIndex(x + Math.Abs(margin));
            int cz0 = CellIndex(z - Math.Abs(margin)), cz1 = CellIndex(z + Math.Abs(margin));
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    List<int> list = _grid[cx * _cells + cz];
                    if (list == null) continue;
                    foreach (int i in list)
                    {
                        Rect r = _rects[i];
                        if (x > r.MinX - margin && x < r.MaxX + margin && z > r.MinZ - margin && z < r.MaxZ + margin) return true;
                    }
                }
            return false;
        }

        /// <summary>True if a body of radius <paramref name="margin"/> can travel in a straight line from a to b.</summary>
        public bool LineOfSight(float ax, float az, float bx, float bz, float margin = 0.4f)
        {
            int cx0 = CellIndex(Math.Min(ax, bx) - margin), cx1 = CellIndex(Math.Max(ax, bx) + margin);
            int cz0 = CellIndex(Math.Min(az, bz) - margin), cz1 = CellIndex(Math.Max(az, bz) + margin);
            float dx = bx - ax, dz = bz - az;
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    List<int> list = _grid[cx * _cells + cz];
                    if (list == null) continue;
                    foreach (int i in list)
                    {
                        Rect r = _rects[i];
                        if (SegmentHitsRect(ax, az, dx, dz, r.MinX - margin, r.MinZ - margin, r.MaxX + margin, r.MaxZ + margin)) return false;
                    }
                }
            return true;
        }

        private static bool SegmentHitsRect(float ax, float az, float dx, float dz, float minX, float minZ, float maxX, float maxZ)
        {
            float t0 = 0f, t1 = 1f;
            if (!Clip(-dx, ax - minX, ref t0, ref t1)) return false;
            if (!Clip(dx, maxX - ax, ref t0, ref t1)) return false;
            if (!Clip(-dz, az - minZ, ref t0, ref t1)) return false;
            if (!Clip(dz, maxZ - az, ref t0, ref t1)) return false;
            return t0 < t1;
        }

        // Liang-Barsky clipping step.
        private static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (Math.Abs(p) < 1e-9f) return q > 0f;
            float t = q / p;
            if (p < 0f) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }

        /// <summary>The street node nearest to a point that is in line of sight of it (street corners of its cell).</summary>
        public int VisibleNode(float x, float z)
        {
            int best = _city.NearestNode(x, z);
            if (LineOfSight(x, z, _city.Nodes[best].X, _city.Nodes[best].Z, 0.3f)) return best;
            int gx = (int)Math.Floor((x - CityLayout.Origin) / CityLayout.Pitch), gz = (int)Math.Floor((z - CityLayout.Origin) / CityLayout.Pitch);
            float bestD = float.MaxValue;
            int found = best;
            for (int ox = 0; ox <= 1; ox++)
                for (int oz = 0; oz <= 1; oz++)
                {
                    int nx = gx + ox, nz = gz + oz;
                    if (nx < 0 || nz < 0 || nx >= CityLayout.StreetLines || nz >= CityLayout.StreetLines) continue;
                    StreetNode n = _city.Nodes[CityLayout.NodeIndex(nx, nz)];
                    float d = (n.X - x) * (n.X - x) + (n.Z - z) * (n.Z - z);
                    if (d < bestD && LineOfSight(x, z, n.X, n.Z, 0.3f)) { bestD = d; found = n.Index; }
                }
            return found;
        }

        /// <summary>
        /// Next point to walk towards to reach (tx, tz) from (x, z): the target itself when it is in
        /// line of sight, otherwise the farthest visible node of the shortest street route.
        /// </summary>
        public void NextWaypoint(float x, float z, float tx, float tz, out float wx, out float wz)
        {
            wx = tx; wz = tz;
            if (LineOfSight(x, z, tx, tz)) return;

            int start = VisibleNode(x, z), goal = VisibleNode(tx, tz);
            List<int> path = Route(start, goal);
            if (path.Count == 0) return;
            // String pulling: aim for the farthest node on the route we can already see.
            int pick = 0;
            for (int i = path.Count - 1; i >= 0; i--)
            {
                StreetNode n = _city.Nodes[path[i]];
                if (LineOfSight(x, z, n.X, n.Z)) { pick = i; break; }
            }
            if (pick == path.Count - 1 && Math.Abs(_city.Nodes[path[pick]].X - x) < 0.5f && Math.Abs(_city.Nodes[path[pick]].Z - z) < 0.5f)
                return; // standing on the final node: head straight for the target
            wx = _city.Nodes[path[pick]].X;
            wz = _city.Nodes[path[pick]].Z;
        }

        /// <summary>A* over the street grid (all street segments are clear).</summary>
        public List<int> Route(int start, int goal)
        {
            var result = new List<int>();
            int n = _city.Nodes.Length;
            var g = new float[n];
            var came = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = float.MaxValue; came[i] = -1; }
            g[start] = 0f;
            var open = new List<int> { start };
            StreetNode goalNode = _city.Nodes[goal];
            while (open.Count > 0)
            {
                int bi = 0;
                float bf = float.MaxValue;
                for (int i = 0; i < open.Count; i++)
                {
                    StreetNode on = _city.Nodes[open[i]];
                    float f = g[open[i]] + Math.Abs(on.X - goalNode.X) + Math.Abs(on.Z - goalNode.Z);
                    if (f < bf) { bf = f; bi = i; }
                }
                int cur = open[bi];
                open.RemoveAt(bi);
                if (cur == goal) break;
                closed[cur] = true;
                _city.Neighbors(cur, _scratch);
                foreach (int nb in _scratch)
                {
                    if (closed[nb]) continue;
                    float ng = g[cur] + CityLayout.Pitch;
                    if (ng < g[nb])
                    {
                        g[nb] = ng;
                        came[nb] = cur;
                        if (!open.Contains(nb)) open.Add(nb);
                    }
                }
            }
            if (start != goal && came[goal] < 0) return result;
            for (int at = goal; at >= 0; at = at == start ? -1 : came[at]) result.Add(at);
            result.Reverse();
            return result;
        }
    }
}
