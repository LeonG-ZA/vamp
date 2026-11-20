// Sanguine City - shared game rules and static data.
// This file has no UnityEngine dependency so it compiles into both the Unity
// client and the standalone dedicated server.
namespace Sanguine
{
    public enum Bloodline : byte { Vorlen = 0, Sylphine = 1, Ashborn = 2, Aurum = 3 }

    public enum EntityKind : byte { Player = 0, Mortal = 1, Hunter = 2 }

    public enum PowerTarget : byte { Self = 0, Other = 1 }

    public sealed class BloodlineDef
    {
        public Bloodline Id;
        public string Name;
        public string Epithet;
        public string Description;
        public string PowerName;
        public string PowerDescription;
        public PowerTarget PowerTarget;
        public float PowerCost;
        public float PowerCooldown;
        public float PowerRange;
        public float MaxBlood;
        public float MaxHealth;
        public float MeleeDamage;
        public int FavoredDistrict;
        public float R, G, B;
    }

    public sealed class DistrictDef
    {
        public int Index;
        public string Name;
        public string Influence;
        public string Flavor;
        // Inclusive bounds on the X/Z plane.
        public float MinX, MaxX, MinZ, MaxZ;
        public string[] MortalTitles;

        public bool Contains(float x, float z)
        {
            return x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
        }
    }

    public enum ZoneKind : byte { None = 0, Haven = 1, Sanctum = 2 }

    public sealed class ZoneDef
    {
        public ZoneKind Kind;
        public string Name;
        public int BlockX, BlockZ;           // index of the city block the zone occupies
        public Bloodline Owner;              // havens only
        public float CenterX, CenterZ, HalfSize;

        public bool Contains(float x, float z)
        {
            return x >= CenterX - HalfSize && x <= CenterX + HalfSize &&
                   z >= CenterZ - HalfSize && z <= CenterZ + HalfSize;
        }
    }

    public static class GameRules
    {
        public const int ProtocolVersion = 3;
        public const int DefaultPort = 7777;

        // Movement
        public const float WalkSpeed = 6f;
        public const float SurgeSpeedMultiplier = 1.6f;
        public const float SlowSpeedMultiplier = 0.5f;
        public const float MortalWalkSpeed = 1.4f;
        public const float MortalRunSpeed = 4.2f;
        public const float HunterSpeed = 5.2f;

        // Combat
        public const float MeleeRange = 2.4f;
        public const float MeleeCooldown = 1.0f;
        public const float SurgeDuration = 6f;
        public const float HunterDamage = 2f;
        public const float HunterAttackCooldown = 1.5f;
        public const float HunterMaxHealth = 8f;
        public const float MortalMaxHealth = 3f;
        public const float TorporSeconds = 10f;

        // Blood and feeding
        public const float MortalMaxBlood = 6f;
        public const float FeedRange = 2.4f;
        public const float FeedRatePerSecond = 1f;
        public const float BloodDecaySeconds = 90f;
        public const float StarvingDamageInterval = 5f;

        // The Hush (secrecy)
        public const float WitnessRange = 15f;
        public const float ExposureHunterThreshold = 50f;
        public const float ExposureMarkedThreshold = 80f;
        public const float ExposureDecayPerSecond = 1.5f;
        public const float ConscienceBeastThreshold = 3f;

        // Thralls
        public const int ThrallsPerPlayer = 2;
        public const float ThrallRecoverySeconds = 180f;
        public const int InfluencePerMission = 10;
        public const int InfluenceControlThreshold = 30;

        // Interest management
        public const float InterestRadius = 90f;
        public const float LocalChatRange = 40f;
    }

    public static class GameData
    {
        public static readonly BloodlineDef[] Bloodlines =
        {
            new BloodlineDef
            {
                Id = Bloodline.Vorlen, Name = "Vorlen", Epithet = "the Iron Kin",
                Description = "Dockside brawlers and street sovereigns. Hardy, direct, hard to put down.",
                PowerName = "Surge",
                PowerDescription = "Flood your limbs with blood: +60% speed and double melee damage for 6s.",
                PowerTarget = PowerTarget.Self, PowerCost = 2f, PowerCooldown = 15f, PowerRange = 0f,
                MaxBlood = 10f, MaxHealth = 13f, MeleeDamage = 2f, FavoredDistrict = 0,
                R = 0.75f, G = 0.25f, B = 0.18f
            },
            new BloodlineDef
            {
                Id = Bloodline.Sylphine, Name = "Sylphine", Epithet = "the Velvet Choir",
                Description = "Artists, muses and heartbreakers who feed on adoration as much as blood.",
                PowerName = "Allure",
                PowerDescription = "Mortals become entranced and follow you; rivals are slowed; hunters falter.",
                PowerTarget = PowerTarget.Other, PowerCost = 1f, PowerCooldown = 10f, PowerRange = 12f,
                MaxBlood = 11f, MaxHealth = 10f, MeleeDamage = 1f, FavoredDistrict = 1,
                R = 0.85f, G = 0.35f, B = 0.65f
            },
            new BloodlineDef
            {
                Id = Bloodline.Ashborn, Name = "Ashborn", Epithet = "the Grave Choir",
                Description = "Scholars of the occult who shape their own blood into weapons.",
                PowerName = "Blood Lash",
                PowerDescription = "Hurl a whip of vitae at a target up to 15m away for 3 damage.",
                PowerTarget = PowerTarget.Other, PowerCost = 2f, PowerCooldown = 6f, PowerRange = 15f,
                MaxBlood = 12f, MaxHealth = 9f, MeleeDamage = 1f, FavoredDistrict = 2,
                R = 0.45f, G = 0.3f, B = 0.8f
            },
            new BloodlineDef
            {
                Id = Bloodline.Aurum, Name = "Aurum", Epithet = "the Gilded Court",
                Description = "Old money and older blood. Their word is a chain upon lesser minds.",
                PowerName = "Command",
                PowerDescription = "A mortal forgets what it saw (-15 exposure); rivals are rooted, hunters stunned.",
                PowerTarget = PowerTarget.Other, PowerCost = 2f, PowerCooldown = 12f, PowerRange = 9f,
                MaxBlood = 10f, MaxHealth = 11f, MeleeDamage = 1f, FavoredDistrict = 3,
                R = 0.9f, G = 0.75f, B = 0.3f
            },
        };

        public static readonly DistrictDef[] Districts =
        {
            new DistrictDef
            {
                Index = 0, Name = "The Harbor", Influence = "Underworld",
                Flavor = "Cranes, container stacks and smugglers who never ask questions.",
                MinX = -205, MaxX = 0, MinZ = -205, MaxZ = 0,
                MortalTitles = new[] { "Dockworker", "Smuggler", "Night Watchman", "Fisher", "Bouncer" }
            },
            new DistrictDef
            {
                Index = 1, Name = "Midtown", Influence = "Media",
                Flavor = "Neon, nightclubs and the city's loudest newsroom.",
                MinX = 0, MaxX = 205, MinZ = 0, MaxZ = 205,
                MortalTitles = new[] { "Clubgoer", "Reporter", "DJ", "Influencer", "Cab Driver" }
            },
            new DistrictDef
            {
                Index = 2, Name = "Old Quarter", Influence = "Occult",
                Flavor = "Crooked streets, shuttered chapels and antique shops that open at midnight.",
                MinX = -205, MaxX = 0, MinZ = 0, MaxZ = 205,
                MortalTitles = new[] { "Antiquarian", "Seminarian", "Tarot Reader", "Night Nurse", "Student" }
            },
            new DistrictDef
            {
                Index = 3, Name = "Financial Row", Influence = "Finance",
                Flavor = "Glass towers where the lights never go off and neither do the deals.",
                MinX = 0, MaxX = 205, MinZ = -205, MaxZ = 0,
                MortalTitles = new[] { "Banker", "Analyst", "Security Guard", "Janitor", "Lawyer" }
            },
        };

        public static BloodlineDef GetBloodline(Bloodline b)
        {
            int i = (int)b;
            if (i < 0 || i >= Bloodlines.Length) i = 0;
            return Bloodlines[i];
        }

        public static int DistrictAt(float x, float z)
        {
            if (x < 0) return z < 0 ? 0 : 2;
            return z < 0 ? 3 : 1;
        }

        public static int ParseDistrict(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            int n;
            if (int.TryParse(s, out n) && n >= 1 && n <= Districts.Length) return n - 1;
            string lower = s.ToLowerInvariant();
            for (int i = 0; i < Districts.Length; i++)
            {
                if (Districts[i].Name.ToLowerInvariant().Contains(lower) ||
                    Districts[i].Influence.ToLowerInvariant() == lower) return i;
            }
            return -1;
        }
    }
}
