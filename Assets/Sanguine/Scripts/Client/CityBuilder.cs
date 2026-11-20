using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>Builds the visual city from the shared deterministic <see cref="CityLayout"/>.</summary>
    public static class CityBuilder
    {
        private static readonly Color Asphalt = new Color(0.07f, 0.07f, 0.08f);
        private static readonly Color Pavement = new Color(0.22f, 0.21f, 0.22f);
        private static readonly Color Grass = new Color(0.08f, 0.16f, 0.07f);
        private static readonly Color Window = new Color(1f, 0.78f, 0.4f);
        private static readonly Color NeonPink = new Color(1f, 0.2f, 0.6f);
        private static readonly Color NeonCyan = new Color(0.2f, 0.9f, 1f);
        private static readonly Color LampColor = new Color(1f, 0.62f, 0.3f);

        public static GameObject Build(CityLayout city)
        {
            var root = new GameObject("City (seed " + city.Seed + ")");
            Transform t = root.transform;
            var rng = new Rng(city.Seed * 7 + 3); // visual-only randomness (windows etc.)

            // Ground and outer walls.
            float size = CityLayout.MapHalf * 2f + 20f;
            Mats.Prim(PrimitiveType.Cube, t, new Vector3(0, -0.5f, 0), new Vector3(size, 1f, size), Asphalt);
            float h = 30f, edge = CityLayout.MapHalf + 1f;
            Color wall = new Color(0.04f, 0.04f, 0.05f);
            Mats.Prim(PrimitiveType.Cube, t, new Vector3(0, h / 2, edge), new Vector3(size, h, 1), wall);
            Mats.Prim(PrimitiveType.Cube, t, new Vector3(0, h / 2, -edge), new Vector3(size, h, 1), wall);
            Mats.Prim(PrimitiveType.Cube, t, new Vector3(edge, h / 2, 0), new Vector3(1, h, size), wall);
            Mats.Prim(PrimitiveType.Cube, t, new Vector3(-edge, h / 2, 0), new Vector3(1, h, size), wall);

            // Block slabs (sidewalks, parks, plazas).
            for (int bx = 0; bx < CityLayout.Blocks; bx++)
            {
                for (int bz = 0; bz < CityLayout.Blocks; bz++)
                {
                    float cx = CityLayout.BlockCenter(bx), cz = CityLayout.BlockCenter(bz);
                    ZoneDef zone = city.ZoneAt(cx, cz);
                    Color c = Pavement;
                    if (city.Parks[bx, bz]) c = Grass;
                    if (zone != null) c = zone.Kind == ZoneKind.Haven ? new Color(0.13f, 0.1f, 0.12f) : new Color(0.32f, 0.3f, 0.28f);
                    Mats.Prim(PrimitiveType.Cube, t, new Vector3(cx, 0.06f, cz), new Vector3(CityLayout.BlockSize, 0.12f, CityLayout.BlockSize), c);
                    if (zone != null) BuildZoneMarkers(t, zone);
                }
            }

            // Buildings.
            foreach (BuildingDef b in city.Buildings)
            {
                if (b.Style == BuildingStyle.Tree) { BuildTree(t, b); continue; }
                Color c = StyleColor(b.Style, rng);
                GameObject go = Mats.Prim(PrimitiveType.Cube, t, new Vector3(b.CX, b.Height / 2f, b.CZ), new Vector3(b.SX, b.Height, b.SZ), c);
                go.name = b.Style.ToString();
                if (b.Style == BuildingStyle.HavenCrypt || b.Style == BuildingStyle.SanctumHall) DecorateSpecial(t, b);
                else AddWindows(t, b, rng);
            }

            // Street lamps at intersections; real lights on every other one.
            foreach (StreetNode n in city.Nodes)
            {
                Vector3 p = new Vector3(n.X + 4.2f, 0, n.Z + 4.2f);
                Mats.Prim(PrimitiveType.Cylinder, t, p + new Vector3(0, 2.5f, 0), new Vector3(0.15f, 2.5f, 0.15f), new Color(0.1f, 0.1f, 0.1f), false);
                Mats.Prim(PrimitiveType.Sphere, t, p + new Vector3(0, 5.1f, 0), new Vector3(0.5f, 0.5f, 0.5f), LampColor, false, true);
                if ((n.GX + n.GZ) % 2 == 0)
                {
                    var lamp = new GameObject("Lamp Light");
                    lamp.transform.SetParent(t, false);
                    lamp.transform.position = p + new Vector3(0, 4.8f, 0);
                    Light l = lamp.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.color = LampColor;
                    l.range = 16f;
                    l.intensity = 1.6f;
                }
            }

            // Combine static geometry for performance.
            StaticBatchingUtility.Combine(root);
            return root;
        }

        private static Color StyleColor(BuildingStyle s, Rng rng)
        {
            float v = rng.Range(-0.03f, 0.03f);
            switch (s)
            {
                case BuildingStyle.Warehouse: return new Color(0.2f + v, 0.17f + v, 0.15f + v);
                case BuildingStyle.Tower: return new Color(0.12f + v, 0.15f + v, 0.2f + v);
                case BuildingStyle.OldStone: return new Color(0.26f + v, 0.23f + v, 0.2f + v);
                case BuildingStyle.Club: return new Color(0.1f + v, 0.06f, 0.12f + v);
                case BuildingStyle.HavenCrypt: return new Color(0.16f, 0.13f, 0.15f);
                case BuildingStyle.SanctumHall: return new Color(0.55f, 0.52f, 0.47f);
                default: return new Color(0.18f + v, 0.18f + v, 0.2f + v);
            }
        }

        private static void AddWindows(Transform t, BuildingDef b, Rng rng)
        {
            int floors = Mathf.Max(1, Mathf.FloorToInt(b.Height / 3.5f));
            int count = Mathf.Min(14, 2 + floors / 2);
            Color lit = b.Style == BuildingStyle.Club ? (rng.NextFloat() < 0.5f ? NeonPink : NeonCyan) : Window;
            for (int i = 0; i < count; i++)
            {
                int face = rng.Range(0, 4);
                float y = 2f + rng.Range(0, floors) * 3.5f;
                if (y > b.Height - 1f) continue;
                float along = rng.Range(-0.4f, 0.4f);
                Vector3 pos, scale;
                if (face < 2)
                {
                    float z = b.CZ + (face == 0 ? b.SZ / 2f + 0.02f : -b.SZ / 2f - 0.02f);
                    pos = new Vector3(b.CX + along * b.SX, y, z);
                    scale = new Vector3(1.4f, 1.6f, 0.05f);
                }
                else
                {
                    float x = b.CX + (face == 2 ? b.SX / 2f + 0.02f : -b.SX / 2f - 0.02f);
                    pos = new Vector3(x, y, b.CZ + along * b.SZ);
                    scale = new Vector3(0.05f, 1.6f, 1.4f);
                }
                Mats.Prim(PrimitiveType.Cube, t, pos, scale, rng.NextFloat() < 0.15f ? new Color(0.5f, 0.7f, 1f) : lit, false, true);
            }
            if (b.Style == BuildingStyle.Club)
            {
                // Neon sign over the door.
                Mats.Prim(PrimitiveType.Cube, t, new Vector3(b.CX, 4f, b.CZ - b.SZ / 2f - 0.1f), new Vector3(b.SX * 0.6f, 0.4f, 0.1f), lit, false, true);
            }
        }

        private static void BuildTree(Transform t, BuildingDef b)
        {
            Mats.Prim(PrimitiveType.Cylinder, t, new Vector3(b.CX, b.Height / 2f, b.CZ), new Vector3(0.4f, b.Height / 2f, 0.4f), new Color(0.15f, 0.1f, 0.07f));
            Mats.Prim(PrimitiveType.Sphere, t, new Vector3(b.CX, b.Height + 1f, b.CZ), new Vector3(3.5f, 3f, 3.5f), new Color(0.06f, 0.13f, 0.06f), false);
        }

        private static void DecorateSpecial(Transform t, BuildingDef b)
        {
            bool haven = b.Style == BuildingStyle.HavenCrypt;
            if (haven)
            {
                // Pitched roof and a door.
                GameObject roof = Mats.Prim(PrimitiveType.Cube, t, new Vector3(b.CX, b.Height + 0.6f, b.CZ), new Vector3(b.SX * 0.75f, 1.2f, b.SZ * 0.75f), new Color(0.08f, 0.07f, 0.08f), false);
                roof.transform.rotation = Quaternion.Euler(0, 45, 0);
                Mats.Prim(PrimitiveType.Cube, t, new Vector3(b.CX, 1.4f, b.CZ - b.SZ / 2f - 0.05f), new Vector3(1.6f, 2.8f, 0.1f), new Color(0.35f, 0.05f, 0.05f), false, true);
            }
            else
            {
                // Columns along the front of the hall.
                for (int i = 0; i < 6; i++)
                {
                    float x = b.CX - b.SX / 2f + 2f + i * (b.SX - 4f) / 5f;
                    Mats.Prim(PrimitiveType.Cylinder, t, new Vector3(x, b.Height / 2f, b.CZ - b.SZ / 2f - 1.5f), new Vector3(1f, b.Height / 2f, 1f), new Color(0.7f, 0.67f, 0.6f));
                }
                Mats.Prim(PrimitiveType.Cube, t, new Vector3(b.CX, b.Height + 0.5f, b.CZ - 1.5f), new Vector3(b.SX + 1f, 1f, b.SZ + 3f), new Color(0.6f, 0.57f, 0.5f), false);
                Mats.Prim(PrimitiveType.Cube, t, new Vector3(b.CX, 3f, b.CZ - b.SZ / 2f - 0.05f), new Vector3(4f, 6f, 0.1f), new Color(1f, 0.85f, 0.5f), false, true);
            }
        }

        private static void BuildZoneMarkers(Transform t, ZoneDef z)
        {
            Color c = z.Kind == ZoneKind.Haven ? Mats.BloodlineColor(z.Owner) : new Color(0.95f, 0.85f, 0.55f);
            float hs = z.HalfSize;
            // Glowing border strips around the plaza.
            Mats.Prim(PrimitiveType.Cube, t, new Vector3(z.CenterX, 0.15f, z.CenterZ - hs), new Vector3(hs * 2, 0.08f, 0.4f), c, false, true);
            Mats.Prim(PrimitiveType.Cube, t, new Vector3(z.CenterX, 0.15f, z.CenterZ + hs), new Vector3(hs * 2, 0.08f, 0.4f), c, false, true);
            Mats.Prim(PrimitiveType.Cube, t, new Vector3(z.CenterX - hs, 0.15f, z.CenterZ), new Vector3(0.4f, 0.08f, hs * 2), c, false, true);
            Mats.Prim(PrimitiveType.Cube, t, new Vector3(z.CenterX + hs, 0.15f, z.CenterZ), new Vector3(0.4f, 0.08f, hs * 2), c, false, true);

            var go = new GameObject("Zone Light " + z.Name);
            go.transform.SetParent(t, false);
            go.transform.position = new Vector3(z.CenterX, 6f, z.CenterZ - 10f);
            Light l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = c;
            l.range = 26f;
            l.intensity = 2f;
        }
    }
}
