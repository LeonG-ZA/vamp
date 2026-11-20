using System;
using System.Collections.Generic;

namespace Sanguine
{
    /// <summary>Small deterministic PRNG (xorshift32) so server and client
    /// generate an identical city regardless of .NET runtime.</summary>
    public sealed class Rng
    {
        private uint _state;

        public Rng(int seed)
        {
            _state = (uint)seed ^ 0x9E3779B9u;
            if (_state == 0) _state = 0xDEADBEEF;
            for (int i = 0; i < 4; i++) NextUInt();
        }

        public uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>Uniform float in [0,1).</summary>
        public float NextFloat()
        {
            return (NextUInt() >> 8) * (1f / 16777216f);
        }

        public float Range(float min, float max)
        {
            return min + (max - min) * NextFloat();
        }

        /// <summary>Uniform int in [min,max).</summary>
        public int Range(int min, int max)
        {
            if (max <= min) return min;
            return min + (int)(NextUInt() % (uint)(max - min));
        }
    }

    public enum BuildingStyle : byte
    {
        Normal = 0, Warehouse = 1, Tower = 2, OldStone = 3, Club = 4,
        HavenCrypt = 5, SanctumHall = 6, Tree = 7
    }

    public struct BuildingDef
    {
        public float CX, CZ, SX, SZ, Height;
        public BuildingStyle Style;
    }

    public struct StreetNode
    {
        public int Index;
        public int GX, GZ;
        public float X, Z;
        public int District;
    }

    /// <summary>The city is a 9x9 grid of streets with 8x8 blocks between them.
    /// Generated identically on server (for AI/zones) and client (for visuals).</summary>
    public sealed class CityLayout
    {
        public const float MapHalf = 205f;
        public const float Origin = -200f;   // first street centreline
        public const float Pitch = 50f;      // street-to-street distance
        public const float StreetWidth = 10f;
        public const float BlockSize = 40f;
        public const int StreetLines = 9;
        public const int Blocks = 8;

        public readonly int Seed;
        public readonly List<BuildingDef> Buildings = new List<BuildingDef>();
        public readonly List<ZoneDef> Zones = new List<ZoneDef>();
        public readonly StreetNode[] Nodes;
        public readonly bool[,] Parks = new bool[Blocks, Blocks];

        public CityLayout(int seed)
        {
            Seed = seed;
            Nodes = new StreetNode[StreetLines * StreetLines];
            for (int gx = 0; gx < StreetLines; gx++)
            {
                for (int gz = 0; gz < StreetLines; gz++)
                {
                    int idx = NodeIndex(gx, gz);
                    float x = Origin + gx * Pitch, z = Origin + gz * Pitch;
                    Nodes[idx] = new StreetNode { Index = idx, GX = gx, GZ = gz, X = x, Z = z, District = GameData.DistrictAt(x, z) };
                }
            }

            AddZone(ZoneKind.Haven, "Vorlen Haven: The Drydock", 1, 1, Bloodline.Vorlen);
            AddZone(ZoneKind.Haven, "Sylphine Haven: Velvet Loft", 6, 6, Bloodline.Sylphine);
            AddZone(ZoneKind.Haven, "Ashborn Haven: The Ossuary", 1, 6, Bloodline.Ashborn);
            AddZone(ZoneKind.Haven, "Aurum Haven: Gilt Vault", 6, 1, Bloodline.Aurum);
            AddZone(ZoneKind.Sanctum, "Sanctum: The Gilded Opera", 5, 4, Bloodline.Vorlen);
            AddZone(ZoneKind.Sanctum, "Sanctum: Chapel of Saint Veran", 2, 5, Bloodline.Vorlen);

            Generate();
        }

        public static int NodeIndex(int gx, int gz) { return gx * StreetLines + gz; }

        public static float BlockCenter(int b) { return Origin + Pitch * b + Pitch * 0.5f; }

        private void AddZone(ZoneKind kind, string name, int bx, int bz, Bloodline owner)
        {
            Zones.Add(new ZoneDef
            {
                Kind = kind, Name = name, BlockX = bx, BlockZ = bz, Owner = owner,
                CenterX = BlockCenter(bx), CenterZ = BlockCenter(bz), HalfSize = BlockSize * 0.5f
            });
        }

        public ZoneDef ZoneAt(float x, float z)
        {
            for (int i = 0; i < Zones.Count; i++)
                if (Zones[i].Contains(x, z)) return Zones[i];
            return null;
        }

        public ZoneDef HavenFor(Bloodline b)
        {
            for (int i = 0; i < Zones.Count; i++)
                if (Zones[i].Kind == ZoneKind.Haven && Zones[i].Owner == b) return Zones[i];
            return Zones[0];
        }

        /// <summary>Spawn point in the open front part of a haven plaza.</summary>
        public static void SpawnPoint(ZoneDef z, out float x, out float zz)
        {
            x = z.CenterX;
            zz = z.CenterZ - 12f;
        }

        private ZoneDef ZoneForBlock(int bx, int bz)
        {
            for (int i = 0; i < Zones.Count; i++)
                if (Zones[i].BlockX == bx && Zones[i].BlockZ == bz) return Zones[i];
            return null;
        }

        private void Generate()
        {
            var rng = new Rng(Seed);
            for (int bx = 0; bx < Blocks; bx++)
            {
                for (int bz = 0; bz < Blocks; bz++)
                {
                    float cx = BlockCenter(bx), cz = BlockCenter(bz);
                    ZoneDef zone = ZoneForBlock(bx, bz);
                    if (zone != null)
                    {
                        if (zone.Kind == ZoneKind.Haven)
                            Add(cx, cz + 4f, 8f, 8f, 6f, BuildingStyle.HavenCrypt);
                        else
                            Add(cx, cz + 9f, 32f, 18f, 16f, BuildingStyle.SanctumHall);
                        continue;
                    }

                    int district = GameData.DistrictAt(cx, cz);
                    if (rng.NextFloat() < 0.12f)
                    {
                        Parks[bx, bz] = true;
                        int trees = rng.Range(5, 10);
                        for (int t = 0; t < trees; t++)
                            Add(cx + rng.Range(-16f, 16f), cz + rng.Range(-16f, 16f), 1f, 1f, rng.Range(4f, 7f), BuildingStyle.Tree);
                        continue;
                    }

                    if (district == 0 && rng.NextFloat() < 0.5f)
                    {
                        // Harbor warehouse occupying the whole block.
                        Add(cx, cz, rng.Range(30f, 36f), rng.Range(24f, 34f), rng.Range(7f, 12f), BuildingStyle.Warehouse);
                        continue;
                    }

                    for (int lx = 0; lx < 2; lx++)
                    {
                        for (int lz = 0; lz < 2; lz++)
                        {
                            float lcx = cx - 10f + lx * 20f, lcz = cz - 10f + lz * 20f;
                            // Lots are 20 m; buildings leave at least 1.75 m of sidewalk to the curb.
                            float sx = rng.Range(13f, 16.5f), sz = rng.Range(13f, 16.5f);
                            float h;
                            BuildingStyle style;
                            switch (district)
                            {
                                case 0: h = rng.Range(6f, 14f); style = BuildingStyle.Warehouse; break;
                                case 1: h = rng.Range(10f, 30f); style = rng.NextFloat() < 0.3f ? BuildingStyle.Club : BuildingStyle.Normal; break;
                                case 2: h = rng.Range(6f, 16f); style = BuildingStyle.OldStone; break;
                                default: h = rng.Range(25f, 70f); style = BuildingStyle.Tower; break;
                            }
                            Add(lcx, lcz, sx, sz, h, style);
                        }
                    }
                }
            }
        }

        private void Add(float cx, float cz, float sx, float sz, float h, BuildingStyle style)
        {
            Buildings.Add(new BuildingDef { CX = cx, CZ = cz, SX = sx, SZ = sz, Height = h, Style = style });
        }

        /// <summary>Street neighbours of a node (4-connected grid).</summary>
        public void Neighbors(int node, List<int> result)
        {
            result.Clear();
            int gx = node / StreetLines, gz = node % StreetLines;
            if (gx > 0) result.Add(NodeIndex(gx - 1, gz));
            if (gx < StreetLines - 1) result.Add(NodeIndex(gx + 1, gz));
            if (gz > 0) result.Add(NodeIndex(gx, gz - 1));
            if (gz < StreetLines - 1) result.Add(NodeIndex(gx, gz + 1));
        }

        public int NearestNode(float x, float z)
        {
            int gx = (int)Math.Round((x - Origin) / Pitch);
            int gz = (int)Math.Round((z - Origin) / Pitch);
            if (gx < 0) gx = 0; if (gx >= StreetLines) gx = StreetLines - 1;
            if (gz < 0) gz = 0; if (gz >= StreetLines) gz = StreetLines - 1;
            return NodeIndex(gx, gz);
        }
    }
}
