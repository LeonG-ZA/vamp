using System;
using System.Collections.Generic;

namespace Sanguine
{
    /// <summary>Names of the texture sets produced by Tools/assets/build_assets.py.</summary>
    public static class SurfaceNames
    {
        // Poly Haven surfaces
        public const string Asphalt = "asphalt_02";
        public const string Cobblestone = "cobblestone_floor_08";
        public const string Pavement = "concrete_pavement";
        public const string CobblePavement = "square_cobblestone";
        public const string HavenPlaza = "herringbone_pavement_03";
        public const string SanctumPlaza = "floor_pavement";
        public const string Grass = "leafy_grass";
        public const string Roof = "tarred_gravel";
        public const string Corrugated = "corrugated_iron";
        public const string RustyCorrugated = "rusty_corrugated_iron";
        public const string FactoryBrick = "factory_brick";
        public const string Container = "container_side";
        public const string Sandstone = "large_sandstone_blocks_01";
        public const string DarkBrick = "dark_brick_wall";
        public const string RedBrick = "medieval_red_brick";
        public const string Concrete = "concrete_wall_004";

        // ambientCG facades (with lit-window emission maps)
        public const string Apartments = "Facade018B";
        public const string Offices = "Facade019B";
        public const string BrickTower = "Facade020B";
        public const string OfficeBlock = "Facade017";
        public const string TowerWarm = "Facade013";
        public const string TowerSparse = "Facade014";
        public const string TowerDark = "Facade016";

        // Untextured special materials
        public const string Paint = "#paint";
        public const string Glow = "#glow";
    }

    public enum LightKind : byte { Lamp = 0, Security = 1, Fire = 2, Neon = 3, Zone = 4 }

    /// <summary>A building from <see cref="CityLayout"/> with its materials decided.</summary>
    public sealed class DressedBuilding
    {
        public BuildingDef Def;
        public int District;
        public float BaseY;
        /// <summary>Lower band (shop fronts / lobby); null when the whole wall uses <see cref="UpperMaterial"/>.</summary>
        public string GroundMaterial;
        public float BandHeight;
        public string UpperMaterial;
        public string RoofMaterial;
        public string TrimMaterial;
        public float ParapetHeight;
        public bool Fascia;
        public float UOffset, VOffset;
        /// <summary>Bit mask of faces that front a street: 1 = +Z, 2 = -Z, 4 = +X, 8 = -X.</summary>
        public int StreetFaces;

        public float Top { get { return BaseY + Def.Height; } }
    }

    /// <summary>A raised block of sidewalk (or plaza/grass) with curb sides.</summary>
    public struct SlabPiece
    {
        public float MinX, MinZ, MaxX, MaxZ, Height;
        public string TopMaterial, SideMaterial;
    }

    /// <summary>A horizontal textured rectangle (roads, park paths).</summary>
    public struct SurfacePiece
    {
        public float MinX, MinZ, MaxX, MaxZ, Y;
        public string Material;
    }

    /// <summary>A textured or glowing box (containers, pillars, neon signs, skyline walls...).</summary>
    public struct BoxPiece
    {
        public float CX, CY, CZ, SX, SY, SZ, Yaw;
        public string Material;
        public float R, G, B, Emission;
        public bool Collider;
    }

    public struct PropPlacement
    {
        public string Id;
        public float X, Y, Z, Yaw;
        public float ScaleX, ScaleY, ScaleZ;
        public bool Collider;
    }

    public struct LightPlacement
    {
        public LightKind Kind;
        public float X, Y, Z;
        public float R, G, B;
        public float Range, Intensity;
    }

    /// <summary>A road marking rectangle, axis aligned.</summary>
    public struct Stripe
    {
        public float CX, CZ, SX, SZ;
    }

    /// <summary>
    /// Decides how the city looks: materials of every building, where street lamps, parked cars,
    /// hydrants, doors, fire escapes and lights go. Deterministic for a given city seed and free of
    /// engine dependencies, so the Unity client and the offline preview renderer produce the same city.
    /// </summary>
    public sealed class CityDressing
    {
        public const float CurbHeight = 0.15f;
        /// <summary>Lateral distance from a street centreline to the curb.</summary>
        public const float CurbOffset = CityLayout.StreetWidth * 0.5f;
        /// <summary>Lateral distance of curb-side furniture (lamps, bins) from the street centreline.</summary>
        public const float FurnitureOffset = 5.38f;
        /// <summary>Lateral distance of parked cars from the street centreline.</summary>
        public const float ParkingOffset = 3.7f;

        public readonly List<DressedBuilding> Buildings = new List<DressedBuilding>();
        public readonly List<SlabPiece> Slabs = new List<SlabPiece>();
        public readonly List<SurfacePiece> Surfaces = new List<SurfacePiece>();
        public readonly List<BoxPiece> Boxes = new List<BoxPiece>();
        public readonly List<PropPlacement> Props = new List<PropPlacement>();
        public readonly List<LightPlacement> Lights = new List<LightPlacement>();
        public readonly List<Stripe> Stripes = new List<Stripe>();

        private readonly CityLayout _city;
        private readonly Rng _rng;

        private static readonly float[][] NeonPalette =
        {
            new[] { 1f, 0.15f, 0.55f }, new[] { 0.2f, 0.85f, 1f }, new[] { 0.65f, 0.3f, 1f },
            new[] { 1f, 0.35f, 0.15f }, new[] { 0.3f, 1f, 0.45f },
        };

        private CityDressing(CityLayout city)
        {
            _city = city;
            _rng = new Rng(city.Seed * 31 + 17);
        }

        public static CityDressing Create(CityLayout city)
        {
            var d = new CityDressing(city);
            d.Build();
            return d;
        }

        /// <summary>Height of the walkable ground at a point: sidewalks/plazas are raised by the curb.</summary>
        public static float GroundHeight(float x, float z)
        {
            float lx = x - CityLayout.Origin, lz = z - CityLayout.Origin;
            if (lx < 0 || lz < 0) return 0f;
            float fx = lx % CityLayout.Pitch, fz = lz % CityLayout.Pitch;
            int bx = (int)(lx / CityLayout.Pitch), bz = (int)(lz / CityLayout.Pitch);
            if (bx >= CityLayout.Blocks || bz >= CityLayout.Blocks) return 0f;
            float lo = CurbOffset, hi = CityLayout.Pitch - CurbOffset;
            return fx >= lo && fx <= hi && fz >= lo && fz <= hi ? CurbHeight : 0f;
        }

        // ------------------------------------------------------------------
        // Build
        // ------------------------------------------------------------------

        private void Build()
        {
            BuildRoads();
            for (int bx = 0; bx < CityLayout.Blocks; bx++)
                for (int bz = 0; bz < CityLayout.Blocks; bz++)
                    BuildBlock(bx, bz);
            foreach (BuildingDef b in _city.Buildings)
            {
                if (b.Style == BuildingStyle.Tree) { DressTreeSpot(b); continue; }
                DressBuilding(b);
            }
            BuildStreetFurniture();
            BuildSkyline();
        }

        private string Pick(params string[] options)
        {
            return options[_rng.Range(0, options.Length)];
        }

        private void AddProp(string id, float x, float y, float z, float yaw, bool collider = false, float sx = 1f, float sy = 1f, float sz = 1f)
        {
            Props.Add(new PropPlacement { Id = id, X = x, Y = y, Z = z, Yaw = yaw, ScaleX = sx, ScaleY = sy, ScaleZ = sz, Collider = collider });
        }

        private void AddLight(LightKind kind, float x, float y, float z, float r, float g, float b, float range, float intensity)
        {
            Lights.Add(new LightPlacement { Kind = kind, X = x, Y = y, Z = z, R = r, G = g, B = b, Range = range, Intensity = intensity });
        }

        private void AddBox(string material, float cx, float cy, float cz, float sx, float sy, float sz, float yaw = 0f,
                            float r = 1f, float g = 1f, float b = 1f, float emission = 0f, bool collider = false)
        {
            Boxes.Add(new BoxPiece { Material = material, CX = cx, CY = cy, CZ = cz, SX = sx, SY = sy, SZ = sz, Yaw = yaw,
                                     R = r, G = g, B = b, Emission = emission, Collider = collider });
        }

        /// <summary>Rotates a local offset (x right, z forward) by a yaw in degrees (Unity convention).</summary>
        public static void Rotate(float yaw, float lx, float lz, out float wx, out float wz)
        {
            double a = yaw * Math.PI / 180.0;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            wx = lx * c + lz * s;
            wz = -lx * s + lz * c;
        }

        /// <summary>Adds a prop and the real light sources it carries (lamp bulbs etc.).</summary>
        private void AddLitProp(string id, float x, float y, float z, float yaw, LightKind kind, float r, float g, float b, float range, float intensity)
        {
            AddProp(id, x, y, z, yaw);
            PropInfo info = PropCatalog.Get(id);
            if (info == null || info.LightCount == 0)
            {
                AddLight(kind, x, y + 3f, z, r, g, b, range, intensity);
                return;
            }
            for (int i = 0; i < info.LightCount; i++)
            {
                float wx, wz;
                Rotate(yaw, info.Lights[i * 3], info.Lights[i * 3 + 2], out wx, out wz);
                // Lights sit just below the bulb so the glass does not occlude them.
                AddLight(kind, x + wx, y + info.Lights[i * 3 + 1] - 0.15f, z + wz, r, g, b, range, intensity);
            }
        }

        // ------------------------------------------------------------------
        // Ground
        // ------------------------------------------------------------------

        private void BuildRoads()
        {
            foreach (DistrictDef d in GameData.Districts)
            {
                // Outer edges continue under the skyline beyond the playable area.
                const float ext = 7f;
                Surfaces.Add(new SurfacePiece
                {
                    MinX = d.MinX <= -CityLayout.MapHalf ? d.MinX - ext : d.MinX, MaxX = d.MaxX >= CityLayout.MapHalf ? d.MaxX + ext : d.MaxX,
                    MinZ = d.MinZ <= -CityLayout.MapHalf ? d.MinZ - ext : d.MinZ, MaxZ = d.MaxZ >= CityLayout.MapHalf ? d.MaxZ + ext : d.MaxZ, Y = 0f,
                    Material = d.Index == 2 ? SurfaceNames.Cobblestone : SurfaceNames.Asphalt
                });
            }
        }

        private void BuildBlock(int bx, int bz)
        {
            float cx = CityLayout.BlockCenter(bx), cz = CityLayout.BlockCenter(bz), h = CityLayout.BlockSize * 0.5f;
            int district = GameData.DistrictAt(cx, cz);
            ZoneDef zone = _city.ZoneAt(cx, cz);
            string side = SurfaceNames.Concrete;
            string top = district == 2 ? SurfaceNames.CobblePavement : SurfaceNames.Pavement;

            if (zone != null)
            {
                top = zone.Kind == ZoneKind.Haven ? SurfaceNames.HavenPlaza : SurfaceNames.SanctumPlaza;
                Slabs.Add(new SlabPiece { MinX = cx - h, MaxX = cx + h, MinZ = cz - h, MaxZ = cz + h, Height = CurbHeight, TopMaterial = top, SideMaterial = side });
                DressZone(zone, cx, cz);
                return;
            }

            if (_city.Parks[bx, bz])
            {
                if (district == 0) { BuildContainerYard(cx, cz); return; }
                // Grass with a paved promenade around it.
                Slabs.Add(new SlabPiece { MinX = cx - h, MaxX = cx + h, MinZ = cz - h, MaxZ = cz + h, Height = CurbHeight, TopMaterial = SurfaceNames.Grass, SideMaterial = side });
                float w = 3f, y = CurbHeight + 0.004f;
                Surfaces.Add(new SurfacePiece { MinX = cx - h, MaxX = cx + h, MinZ = cz + h - w, MaxZ = cz + h, Y = y, Material = top });
                Surfaces.Add(new SurfacePiece { MinX = cx - h, MaxX = cx + h, MinZ = cz - h, MaxZ = cz - h + w, Y = y, Material = top });
                Surfaces.Add(new SurfacePiece { MinX = cx - h, MaxX = cx - h + w, MinZ = cz - h + w, MaxZ = cz + h - w, Y = y, Material = top });
                Surfaces.Add(new SurfacePiece { MinX = cx + h - w, MaxX = cx + h, MinZ = cz - h + w, MaxZ = cz + h - w, Y = y, Material = top });
                // Cross paths through the middle.
                Surfaces.Add(new SurfacePiece { MinX = cx - 1.5f, MaxX = cx + 1.5f, MinZ = cz - h + w, MaxZ = cz + h - w, Y = y, Material = top });
                Surfaces.Add(new SurfacePiece { MinX = cx - h + w, MaxX = cx + h - w, MinZ = cz - 1.5f, MaxZ = cz + 1.5f, Y = y, Material = top });
                // Benches facing the paths and lamps at the path corners.
                AddProp("bench", cx - 6f, CurbHeight, cz + 2.4f, 180f);
                AddProp("bench", cx + 6f, CurbHeight, cz - 2.4f, 0f);
                AddProp("bench", cx + 2.4f, CurbHeight, cz + 7f, 270f);
                AddProp("bench", cx - 2.4f, CurbHeight, cz - 7f, 90f);
                AddLitProp("street_lamp", cx + 2.6f, CurbHeight, cz + 2.6f, 45f, LightKind.Lamp, 1f, 0.72f, 0.45f, 15f, 2.2f);
                AddLitProp("street_lamp", cx - 2.6f, CurbHeight, cz - 2.6f, 225f, LightKind.Lamp, 1f, 0.72f, 0.45f, 15f, 2.2f);
                AddProp("trash_can", cx + 2.6f, CurbHeight, cz - 2.6f, _rng.Range(0f, 360f));
                return;
            }

            Slabs.Add(new SlabPiece { MinX = cx - h, MaxX = cx + h, MinZ = cz - h, MaxZ = cz + h, Height = CurbHeight, TopMaterial = top, SideMaterial = side });

            // Alley clutter where the four lots meet (lot-based blocks only).
            bool lots = district != 0 || !HasWarehouseBlock(cx, cz);
            if (lots && (district == 0 || district == 2 || district == 1) && _rng.NextFloat() < 0.4f)
            {
                AddProp("fire_barrel", cx, CurbHeight, cz, _rng.Range(0f, 360f));
                AddLight(LightKind.Fire, cx, CurbHeight + 1.1f, cz, 1f, 0.5f, 0.18f, 9f, 2.6f);
            }
            if (lots && _rng.NextFloat() < 0.7f)
            {
                int n = _rng.Range(1, 4);
                for (int i = 0; i < n; i++)
                    AddProp(_rng.NextFloat() < 0.6f ? "trash_bag" : "cardboard_box", cx + _rng.Range(-0.6f, 0.6f), CurbHeight, cz + _rng.Range(1.0f, 3.5f) * (i % 2 == 0 ? 1 : -1), _rng.Range(0f, 360f));
            }
        }

        private bool HasWarehouseBlock(float cx, float cz)
        {
            foreach (BuildingDef b in _city.Buildings)
                if (b.Style == BuildingStyle.Warehouse && Math.Abs(b.CX - cx) < 0.01f && Math.Abs(b.CZ - cz) < 0.01f) return true;
            return false;
        }

        private void BuildContainerYard(float cx, float cz)
        {
            float h = CityLayout.BlockSize * 0.5f;
            Slabs.Add(new SlabPiece { MinX = cx - h, MaxX = cx + h, MinZ = cz - h, MaxZ = cz + h, Height = CurbHeight, TopMaterial = SurfaceNames.Pavement, SideMaterial = SurfaceNames.Concrete });
            const float L = 12.19f, W = 2.44f, H = 2.59f;
            for (int row = 0; row < 4; row++)
            {
                float z = cz - 12f + row * 8f;
                for (int col = 0; col < 2; col++)
                {
                    float x = cx + (col == 0 ? -7.5f : 7.5f);
                    int levels = _rng.Range(1, 4);
                    for (int lv = 0; lv < levels; lv++)
                    {
                        float roll = _rng.NextFloat();
                        string mat; float r = 1f, g = 1f, b = 1f;
                        if (roll < 0.3f) mat = SurfaceNames.Container;
                        else if (roll < 0.55f) mat = SurfaceNames.RustyCorrugated;
                        else if (roll < 0.8f) { mat = SurfaceNames.Corrugated; r = 0.35f; g = 0.48f; b = 0.75f; }
                        else { mat = SurfaceNames.Corrugated; r = 0.78f; g = 0.3f; b = 0.24f; }
                        AddBox(mat, x + _rng.Range(-0.3f, 0.3f), CurbHeight + H * lv + H * 0.5f, z, L, H, W, 0f, r, g, b, 0f, true);
                    }
                }
            }
            AddLitProp("street_lamp", cx - h + 3.2f, CurbHeight, cz - h + 3.2f, 45f, LightKind.Lamp, 0.85f, 0.9f, 1f, 16f, 2.2f);
            AddLitProp("street_lamp", cx + h - 3.2f, CurbHeight, cz + h - 3.2f, 225f, LightKind.Lamp, 0.85f, 0.9f, 1f, 16f, 2.2f);
        }

        private void DressTreeSpot(BuildingDef t)
        {
            // Parks keep their layout but use planters rather than (very heavy) photoscanned trees.
            if (_rng.NextFloat() < 0.5f)
                AddProp("planter", t.CX, CurbHeight, t.CZ, _rng.Range(0, 4) * 90f);
        }

        // ------------------------------------------------------------------
        // Buildings
        // ------------------------------------------------------------------

        private static int StreetFacesOf(BuildingDef b)
        {
            int bx = (int)Math.Floor((b.CX - CityLayout.Origin) / CityLayout.Pitch);
            int bz = (int)Math.Floor((b.CZ - CityLayout.Origin) / CityLayout.Pitch);
            float bcx = CityLayout.BlockCenter(bx), bcz = CityLayout.BlockCenter(bz);
            int faces = 0;
            if (b.CZ >= bcz - 0.5f) faces |= 1;
            if (b.CZ <= bcz + 0.5f) faces |= 2;
            if (b.CX >= bcx - 0.5f) faces |= 4;
            if (b.CX <= bcx + 0.5f) faces |= 8;
            return faces;
        }

        /// <summary>Wall frame of a building face: centre on the wall, yaw (prop front = outward) and width.</summary>
        public static void FaceFrame(BuildingDef b, int face, out float x, out float z, out float yaw, out float width)
        {
            switch (face)
            {
                case 0: x = b.CX; z = b.CZ + b.SZ * 0.5f; yaw = 0f; width = b.SX; break;
                case 1: x = b.CX; z = b.CZ - b.SZ * 0.5f; yaw = 180f; width = b.SX; break;
                case 2: x = b.CX + b.SX * 0.5f; z = b.CZ; yaw = 90f; width = b.SZ; break;
                default: x = b.CX - b.SX * 0.5f; z = b.CZ; yaw = 270f; width = b.SZ; break;
            }
        }

        /// <summary>Distance from a building face (given by its outward yaw) to the curb of its block.</summary>
        private static float SidewalkDepth(BuildingDef b, float yaw)
        {
            int bx = (int)Math.Floor((b.CX - CityLayout.Origin) / CityLayout.Pitch);
            int bz = (int)Math.Floor((b.CZ - CityLayout.Origin) / CityLayout.Pitch);
            float bcx = CityLayout.BlockCenter(bx), bcz = CityLayout.BlockCenter(bz), h = CityLayout.BlockSize * 0.5f;
            if (yaw < 45f) return bcz + h - (b.CZ + b.SZ * 0.5f);
            if (yaw < 135f) return bcx + h - (b.CX + b.SX * 0.5f);
            if (yaw < 225f) return (b.CZ - b.SZ * 0.5f) - (bcz - h);
            return (b.CX - b.SX * 0.5f) - (bcx - h);
        }

        private static void PointOnFace(float fx, float fz, float yaw, float along, float outward, out float x, out float z)
        {
            float ox, oz;
            Rotate(yaw, along, outward, out ox, out oz);
            x = fx + ox;
            z = fz + oz;
        }

        private void DressBuilding(BuildingDef b)
        {
            var d = new DressedBuilding
            {
                Def = b, District = GameData.DistrictAt(b.CX, b.CZ), BaseY = CurbHeight,
                RoofMaterial = SurfaceNames.Roof, TrimMaterial = SurfaceNames.Concrete,
                UOffset = _rng.NextFloat(), VOffset = _rng.NextFloat(), StreetFaces = StreetFacesOf(b)
            };
            switch (b.Style)
            {
                case BuildingStyle.Warehouse:
                    d.UpperMaterial = Pick(SurfaceNames.Corrugated, SurfaceNames.RustyCorrugated, SurfaceNames.FactoryBrick);
                    d.ParapetHeight = 0.5f;
                    break;
                case BuildingStyle.Club:
                    d.GroundMaterial = SurfaceNames.DarkBrick; d.BandHeight = 4.2f; d.Fascia = true; d.ParapetHeight = 0.9f;
                    d.UpperMaterial = Pick(SurfaceNames.Apartments, SurfaceNames.BrickTower);
                    break;
                case BuildingStyle.OldStone:
                    d.GroundMaterial = SurfaceNames.RedBrick; d.BandHeight = 3.8f; d.Fascia = true; d.ParapetHeight = 0.7f;
                    d.UpperMaterial = Pick(SurfaceNames.Apartments, SurfaceNames.BrickTower);
                    break;
                case BuildingStyle.Tower:
                    d.GroundMaterial = SurfaceNames.Concrete; d.BandHeight = 6f; d.Fascia = true; d.ParapetHeight = 1.2f;
                    d.UpperMaterial = b.Height > 45f ? Pick(SurfaceNames.TowerWarm, SurfaceNames.TowerSparse, SurfaceNames.TowerDark)
                                                     : Pick(SurfaceNames.OfficeBlock, SurfaceNames.Offices);
                    break;
                case BuildingStyle.HavenCrypt:
                    d.UpperMaterial = SurfaceNames.Sandstone; d.RoofMaterial = SurfaceNames.Sandstone; d.TrimMaterial = SurfaceNames.Sandstone;
                    d.ParapetHeight = 0.6f;
                    break;
                case BuildingStyle.SanctumHall:
                    d.UpperMaterial = SurfaceNames.Sandstone; d.TrimMaterial = SurfaceNames.Sandstone; d.ParapetHeight = 1.0f;
                    break;
                default:
                    d.GroundMaterial = SurfaceNames.DarkBrick; d.BandHeight = 4.2f; d.Fascia = true; d.ParapetHeight = 0.9f;
                    d.UpperMaterial = b.Height > 22f ? Pick(SurfaceNames.OfficeBlock, SurfaceNames.Offices, SurfaceNames.BrickTower)
                                                     : Pick(SurfaceNames.Apartments, SurfaceNames.BrickTower, SurfaceNames.Offices);
                    break;
            }
            Buildings.Add(d);

            if (b.Style == BuildingStyle.HavenCrypt || b.Style == BuildingStyle.SanctumHall) return;

            bool fireEscapeDone = !(b.Style == BuildingStyle.Normal || b.Style == BuildingStyle.OldStone || b.Style == BuildingStyle.Club) || b.Height < 13f;
            for (int face = 0; face < 4; face++)
            {
                if ((d.StreetFaces & (1 << face)) == 0) continue;
                float fx, fz, yaw, width;
                FaceFrame(b, face, out fx, out fz, out yaw, out width);
                if (b.Style == BuildingStyle.Warehouse) DressWarehouseFace(d, fx, fz, yaw, width);
                else DressShopFace(d, fx, fz, yaw, width, ref fireEscapeDone);
            }

            // Rooftop plant on mid-rise buildings.
            if (b.Style != BuildingStyle.Warehouse && b.Height < 45f && _rng.NextFloat() < 0.35f)
            {
                int n = 1;
                for (int i = 0; i < n; i++)
                    AddProp("aircon", b.CX + _rng.Range(-b.SX * 0.3f, b.SX * 0.3f), d.Top, b.CZ + _rng.Range(-b.SZ * 0.3f, b.SZ * 0.3f), _rng.Range(0, 4) * 90f);
            }
        }

        private void DressWarehouseFace(DressedBuilding d, float fx, float fz, float yaw, float width)
        {
            int doors = _rng.Range(1, 3);
            for (int i = 0; i < doors; i++)
            {
                float along = -width * 0.5f + (i + 1) * width / (doors + 1) + _rng.Range(-1f, 1f);
                float x, z;
                PointOnFace(fx, fz, yaw, along, 0.01f, out x, out z);
                AddProp(_rng.NextFloat() < 0.3f ? "shutter_door_graffiti" : "shutter_door", x, d.BaseY, z, yaw, false, 3.0f, 1.55f, 1f);
                // Floodlight over the loading door.
                float top = d.BaseY + 2.4f * 1.55f + 0.45f;
                bool sodium = _rng.NextFloat() < 0.6f;
                AddLitProp("security_light", x, top, z, yaw, LightKind.Security, sodium ? 1f : 0.82f, sodium ? 0.7f : 0.9f, sodium ? 0.38f : 1f, 13f, 2.6f);
                // Freight against the wall, only where the sidewalk leaves room for pedestrians.
                if (_rng.NextFloat() < 0.5f && SidewalkDepth(d.Def, yaw) >= 3f)
                {
                    int n = _rng.Range(2, 5);
                    for (int k = 0; k < n; k++)
                    {
                        float a = along + 2.4f + k * 0.9f;
                        if (Math.Abs(a) > width * 0.5f - 0.6f) break;
                        float cx, cz;
                        PointOnFace(fx, fz, yaw, a, 0.45f, out cx, out cz);
                        string id = _rng.NextFloat() < 0.5f ? "crate" : "barrel";
                        AddProp(id, cx, d.BaseY, cz, yaw + _rng.Range(-15f, 15f));
                        if (id == "crate" && _rng.NextFloat() < 0.4f) AddProp("crate", cx, d.BaseY + 0.34f, cz, yaw + _rng.Range(-10f, 10f));
                    }
                }
            }
        }

        private void DressShopFace(DressedBuilding d, float fx, float fz, float yaw, float width, ref bool fireEscapeDone)
        {
            BuildingDef b = d.Def;
            float x, z;

            // Ground floor: a shop shutter or a lit shop window / lobby.
            if (_rng.NextFloat() < 0.45f)
            {
                PointOnFace(fx, fz, yaw, _rng.Range(-width * 0.5f + 2f, width * 0.5f - 2f), 0.01f, out x, out z);
                AddProp(_rng.NextFloat() < 0.35f ? "shutter_door_graffiti" : "shutter_door", x, d.BaseY, z, yaw, false, 1.8f, 1f, 1f);
            }
            if (_rng.NextFloat() < (b.Style == BuildingStyle.Tower ? 0.9f : 0.5f))
            {
                bool warm = _rng.NextFloat() < 0.75f;
                float w = b.Style == BuildingStyle.Tower ? width * 0.45f : _rng.Range(2.2f, 3.6f);
                float hgt = b.Style == BuildingStyle.Tower ? 3.4f : 2.0f;
                PointOnFace(fx, fz, yaw, _rng.Range(-width * 0.25f, width * 0.25f), 0.02f, out x, out z);
                AddBox(SurfaceNames.Glow, x, d.BaseY + 0.7f + hgt * 0.5f, z, w, hgt, 0.04f, yaw,
                       warm ? 1f : 0.75f, warm ? 0.78f : 0.88f, warm ? 0.5f : 1f, b.Style == BuildingStyle.Tower ? 1.6f : 1.2f);
            }

            // Club neon.
            if (b.Style == BuildingStyle.Club && _rng.NextFloat() < 0.8f)
            {
                float[] c = NeonPalette[_rng.Range(0, NeonPalette.Length)];
                PointOnFace(fx, fz, yaw, _rng.Range(-width * 0.2f, width * 0.2f), 0.18f, out x, out z);
                AddBox(SurfaceNames.Glow, x, d.BaseY + d.BandHeight - 0.75f, z, Math.Min(width * 0.55f, 7f), 0.42f, 0.12f, yaw, c[0], c[1], c[2], 5f);
                float lx, lz;
                PointOnFace(fx, fz, yaw, 0f, 1.4f, out lx, out lz);
                AddLight(LightKind.Neon, x + (lx - fx) * 0.5f, d.BaseY + d.BandHeight - 0.9f, z + (lz - fz) * 0.5f, c[0], c[1], c[2], 10f, 2.4f);
            }

            // Air conditioners on upper floors.
            if (b.Height < 45f)
            {
                int floors = Math.Max(1, (int)((b.Height - d.BandHeight - 1.5f) / 3f));
                int n = _rng.NextFloat() < 0.45f ? _rng.Range(1, 3) : 0;
                for (int i = 0; i < n; i++)
                {
                    PointOnFace(fx, fz, yaw, _rng.Range(-width * 0.5f + 1f, width * 0.5f - 1f), 0f, out x, out z);
                    AddProp("aircon", x, d.BaseY + d.BandHeight + 0.9f + _rng.Range(0, floors) * 3f, z, yaw);
                }
            }

            // One fire escape per apartment block.
            if (!fireEscapeDone && width >= 8f && _rng.NextFloat() < 0.55f)
            {
                fireEscapeDone = true;
                PointOnFace(fx, fz, yaw, _rng.Range(-width * 0.5f + 3.6f, width * 0.5f - 3.6f), 0f, out x, out z);
                AddProp("fire_escape", x, d.BaseY + 1.2f, z, yaw);
            }
        }

        // ------------------------------------------------------------------
        // Havens and sanctums
        // ------------------------------------------------------------------

        private void DressZone(ZoneDef zone, float cx, float cz)
        {
            float h = zone.HalfSize;
            float r, g, b;
            if (zone.Kind == ZoneKind.Haven)
            {
                BloodlineDef def = GameData.GetBloodline(zone.Owner);
                r = def.R; g = def.G; b = def.B;
            }
            else { r = 1f; g = 0.85f; b = 0.55f; }

            // Subtle glowing inlay marks the protected ground.
            const float t = 0.25f, y = CurbHeight + 0.01f;
            AddBox(SurfaceNames.Glow, cx, y, cz - h + 0.4f, h * 2f - 0.8f, 0.02f, t, 0f, r, g, b, 1.1f);
            AddBox(SurfaceNames.Glow, cx, y, cz + h - 0.4f, h * 2f - 0.8f, 0.02f, t, 0f, r, g, b, 1.1f);
            AddBox(SurfaceNames.Glow, cx - h + 0.4f, y, cz, t, 0.02f, h * 2f - 0.8f, 0f, r, g, b, 1.1f);
            AddBox(SurfaceNames.Glow, cx + h - 0.4f, y, cz, t, 0.02f, h * 2f - 0.8f, 0f, r, g, b, 1.1f);

            // Lamps flank the path to the entrance.
            AddLitProp("street_lamp", cx - 4.5f, CurbHeight, cz - 9f, 90f, LightKind.Lamp, 1f, 0.75f, 0.5f, 15f, 2.2f);
            AddLitProp("street_lamp", cx + 4.5f, CurbHeight, cz - 9f, 270f, LightKind.Lamp, 1f, 0.75f, 0.5f, 15f, 2.2f);
            AddProp("bench", cx - 8f, CurbHeight, cz - 14f, 90f);
            AddProp("bench", cx + 8f, CurbHeight, cz - 14f, 270f);

            if (zone.Kind == ZoneKind.Haven)
            {
                // The crypt sits at cz + 4 (8 x 8 m); its door glows in the bloodline's colour.
                AddBox(SurfaceNames.Glow, cx, CurbHeight + 1.4f, cz - 0.04f, 1.6f, 2.8f, 0.06f, 0f, 0.35f + r * 0.4f, g * 0.35f, b * 0.35f, 2f);
                AddLight(LightKind.Zone, cx, CurbHeight + 2.2f, cz - 1.2f, r, g, b, 14f, 3f);
                AddProp("planter", cx - 3f, CurbHeight, cz - 1.2f, 0f);
                AddProp("planter", cx + 3f, CurbHeight, cz - 1.2f, 0f);
            }
            else
            {
                // Sanctum hall sits at cz + 9 (32 x 18 m): square pillars along its front and a lit doorway.
                float front = cz + 9f - 9f;
                for (int i = 0; i < 6; i++)
                {
                    float px = cx - 16f + 2f + i * (32f - 4f) / 5f;
                    AddBox(SurfaceNames.Sandstone, px, CurbHeight + 8f, front - 1.5f, 1.1f, 16f, 1.1f, 0f, 1f, 1f, 1f, 0f, true);
                }
                AddBox(SurfaceNames.Sandstone, cx, CurbHeight + 16.5f, front - 1.5f, 33f, 1f, 3.6f, 0f, 1f, 1f, 1f, 0f, false);
                AddBox(SurfaceNames.Glow, cx, CurbHeight + 3f, front - 0.03f, 4f, 6f, 0.06f, 0f, 1f, 0.8f, 0.5f, 2.2f);
                AddLight(LightKind.Zone, cx, CurbHeight + 4f, front - 3f, r, g, b, 18f, 3f);
                AddProp("planter", cx - 9f, CurbHeight, front - 4f, 0f);
                AddProp("planter", cx + 9f, CurbHeight, front - 4f, 0f);
            }
        }

        // ------------------------------------------------------------------
        // Street furniture
        // ------------------------------------------------------------------

        private static bool InsideMap(float x, float z)
        {
            return Math.Abs(x) < CityLayout.MapHalf - 1f && Math.Abs(z) < CityLayout.MapHalf - 1f;
        }

        private void AddStreetLamp(float x, float z, float yaw)
        {
            if (!InsideMap(x, z) || _city.ZoneAt(x, z) != null) return;
            AddLitProp("street_lamp", x, CurbHeight, z, yaw, LightKind.Lamp, 1f, 0.7f, 0.42f, 15f, 2.3f);
        }

        private void BuildStreetFurniture()
        {
            float o = FurnitureOffset;
            for (int gx = 0; gx < CityLayout.StreetLines; gx++)
            {
                for (int gz = 0; gz < CityLayout.StreetLines; gz++)
                {
                    float X = CityLayout.Origin + gx * CityLayout.Pitch, Z = CityLayout.Origin + gz * CityLayout.Pitch;
                    int district = GameData.DistrictAt(X + 1f, Z + 1f);

                    // Corner furniture.
                    AddStreetLamp(X + o, Z + o, 45f);
                    if (_rng.NextFloat() < 0.3f && InsideMap(X - o, Z + o) && _city.ZoneAt(X - o, Z + o) == null)
                        AddProp("fire_hydrant", X - o, CurbHeight, Z + o, _rng.Range(0f, 360f));
                    if (_rng.NextFloat() < 0.2f && InsideMap(X + o, Z - o) && _city.ZoneAt(X + o, Z - o) == null)
                        AddProp("utility_box", X + o, CurbHeight, Z - o, 180f);

                    // Crosswalks on every approach (not on the Old Quarter's cobbles).
                    if (district != 2)
                    {
                        if (gx < CityLayout.StreetLines - 1) AddCrosswalk(X + 7.6f, Z, true);
                        if (gx > 0) AddCrosswalk(X - 7.6f, Z, true);
                        if (gz < CityLayout.StreetLines - 1) AddCrosswalk(X, Z + 7.6f, false);
                        if (gz > 0) AddCrosswalk(X, Z - 7.6f, false);
                    }

                    // Street segments to the east (+X) and north (+Z).
                    if (gx < CityLayout.StreetLines - 1) DressSegment(X, Z, true, gx + gz);
                    if (gz < CityLayout.StreetLines - 1) DressSegment(X, Z, false, gx + gz);
                }
            }
        }

        private void AddCrosswalk(float cx, float cz, bool alongX)
        {
            // Zebra stripes parallel to traffic, spread across the carriageway.
            for (int k = -4; k <= 4; k++)
            {
                if (alongX) Stripes.Add(new Stripe { CX = cx, CZ = cz + k * 1.0f, SX = 2.6f, SZ = 0.5f });
                else Stripes.Add(new Stripe { CX = cx + k * 1.0f, CZ = cz, SX = 0.5f, SZ = 2.6f });
            }
        }

        private void DressSegment(float X, float Z, bool alongX, int parity)
        {
            float len = CityLayout.Pitch;
            float mx = alongX ? X + len * 0.5f : X, mz = alongX ? Z : Z + len * 0.5f;
            int district = GameData.DistrictAt(alongX ? mx : mx + 1f, alongX ? mz + 1f : mz);
            float o = FurnitureOffset;

            // Mid-block lamp, alternating sides.
            int side = parity % 2 == 0 ? 1 : -1;
            float lx = alongX ? mx : mx + side * o, lz = alongX ? mz + side * o : mz;
            if (!InsideMap(lx, lz)) { side = -side; lx = alongX ? mx : mx + side * o; lz = alongX ? mz + side * o : mz; }
            AddStreetLamp(lx, lz, alongX ? (side > 0 ? 180f : 0f) : (side > 0 ? 270f : 90f));

            // Bins on the opposite curb.
            if (_rng.NextFloat() < 0.6f)
            {
                float t = _rng.Range(-14f, 14f);
                float bx = alongX ? mx + t : mx - side * o, bz = alongX ? mz - side * o : mz + t;
                if (InsideMap(bx, bz) && _city.ZoneAt(bx, bz) == null)
                {
                    AddProp(_rng.NextFloat() < 0.5f ? "trash_can" : "trash_can_rusty", bx, CurbHeight, bz, _rng.Range(0f, 360f));
                    if (_rng.NextFloat() < 0.5f)
                        AddProp("trash_bag", bx + (alongX ? 0.8f : 0f), CurbHeight, bz + (alongX ? 0f : 0.8f), _rng.Range(0f, 360f));
                }
            }

            // Parked cars along both curbs.
            for (int s = -1; s <= 1; s += 2)
            {
                if (_rng.NextFloat() > 0.45f) continue;
                float t = _rng.Range(-13f, 13f);
                float cx = alongX ? mx + t : mx + s * ParkingOffset, cz = alongX ? mz + s * ParkingOffset : mz + t;
                if (!InsideMap(cx, cz)) continue;
                float yaw = alongX ? (s > 0 ? 90f : 270f) : (s > 0 ? 180f : 0f);
                AddProp("covered_car", cx, 0f, cz, yaw + _rng.Range(-2f, 2f), true);
            }

            // Manhole covers.
            if (_rng.NextFloat() < 0.4f)
            {
                float t = _rng.Range(-12f, 12f), l = _rng.Range(-1.5f, 1.5f);
                AddProp("manhole", alongX ? mx + t : mx + l, -0.035f, alongX ? mz + l : mz + t, _rng.Range(0f, 360f));
            }

            // Dashed centre line.
            if (district != 2)
            {
                for (float t = -15f; t <= 15.01f; t += 6f)
                {
                    if (alongX) Stripes.Add(new Stripe { CX = mx + t, CZ = mz, SX = 3f, SZ = 0.14f });
                    else Stripes.Add(new Stripe { CX = mx, CZ = mz + t, SX = 0.14f, SZ = 3f });
                }
            }
        }

        // ------------------------------------------------------------------
        // Skyline beyond the playable area
        // ------------------------------------------------------------------

        private void BuildSkyline()
        {
            float e = CityLayout.MapHalf + 5f, span = CityLayout.MapHalf * 2f + 20f;
            string[] mats = { SurfaceNames.TowerDark, SurfaceNames.TowerWarm, SurfaceNames.TowerSparse };
            for (int side = 0; side < 4; side++)
            {
                // Several towers of different heights per side so the skyline is not a flat wall.
                int n = 6;
                for (int i = 0; i < n; i++)
                {
                    float along = -span * 0.5f + (i + 0.5f) * span / n;
                    float hgt = _rng.Range(45f, 95f), w = span / n + 0.5f, depth = 10f;
                    string m = mats[_rng.Range(0, mats.Length)];
                    if (side == 0) AddBox(m, along, hgt * 0.5f, e + depth * 0.5f, w, hgt, depth, 0f, 1f, 1f, 1f, 0f, true);
                    else if (side == 1) AddBox(m, along, hgt * 0.5f, -e - depth * 0.5f, w, hgt, depth, 0f, 1f, 1f, 1f, 0f, true);
                    else if (side == 2) AddBox(m, e + depth * 0.5f, hgt * 0.5f, along, depth, hgt, w, 0f, 1f, 1f, 1f, 0f, true);
                    else AddBox(m, -e - depth * 0.5f, hgt * 0.5f, along, depth, hgt, w, 0f, 1f, 1f, 1f, 0f, true);
                }
            }
        }
    }
}
