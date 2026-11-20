using System;
using System.Collections.Generic;

namespace Sanguine
{
    /// <summary>Dimensions of a 3D prop in Unity space (metres, +Z = front, Y up), plus light anchors
    /// (e.g. lamp bulbs) relative to the prop's pivot. The values are generated from the real art
    /// (PropCatalog.g.cs) so engine-free code can place props sensibly.</summary>
    public sealed class PropInfo
    {
        public readonly string Id;
        public readonly float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public readonly int Triangles;
        /// <summary>Flat list of light anchor positions: x0, y0, z0, x1, y1, z1, ...</summary>
        public readonly float[] Lights;

        public PropInfo(string id, float minX, float minY, float minZ, float maxX, float maxY, float maxZ, int triangles, float[] lights)
        {
            Id = id; MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
            Triangles = triangles; Lights = lights ?? new float[0];
        }

        public float Width { get { return MaxX - MinX; } }
        public float Height { get { return MaxY - MinY; } }
        public float Depth { get { return MaxZ - MinZ; } }
        public int LightCount { get { return Lights.Length / 3; } }
    }

    public static partial class PropCatalog
    {
        private static Dictionary<string, PropInfo> _byId;

        public static PropInfo Get(string id)
        {
            if (_byId == null)
            {
                var d = new Dictionary<string, PropInfo>(StringComparer.Ordinal);
                foreach (PropInfo p in Generated) d[p.Id] = p;
                _byId = d;
            }
            PropInfo info;
            return _byId.TryGetValue(id, out info) ? info : null;
        }

        public static IEnumerable<PropInfo> All { get { return Generated; } }
    }
}
