// Sanguine City - shared game rules and static data.
// This file has no UnityEngine dependency so it compiles into both the Unity
// client and the standalone dedicated server.
namespace Sanguine
{
    public enum Bloodline : byte { Vorlen = 0, Sylphine = 1, Ashborn = 2, Aurum = 3, Umbren = 4, Varga = 5 }

    public enum EntityKind : byte { Player = 0, Mortal = 1, Hunter = 2 }

    public enum PowerTarget : byte { Self = 0, Other = 1 }

    /// <summary>Every bloodline gift. Three per bloodline: tier 1 is innate, tiers 2-3 are learned with standing.</summary>
    public enum GiftId : byte
    {
        Surge = 0, IronSkin = 1, Earthshaker = 2,
        Allure = 3, Glamour = 4, Rapture = 5,
        BloodLash = 6, Siphon = 7, CrimsonMist = 8,
        Command = 9, Majesty = 10, Dominate = 11,
        Shroud = 12, Whispers = 13, Undercroft = 14,
        Pounce = 15, PackHowl = 16, Unchained = 17,
    }

    public sealed class GiftDef
    {
        public GiftId Id;
        public Bloodline Line;
        public int Tier;                 // 1..3 (slot = Tier - 1)
        public string Name;
        public string Description;
        public PowerTarget Target;
        public float Cost;               // blood
        public float Cooldown;           // seconds
        public float Range;              // targeted range, or radius for area gifts
        public float Duration;           // seconds, for lasting effects
        /// <summary>Subtle gifts do not breach the Hush when mortals see them.</summary>
        public bool Subtle;

        public int Slot { get { return Tier - 1; } }
        public int StandingCost { get { return GameRules.GiftStandingCost[Tier - 1]; } }
    }

    public sealed class BloodlineDef
    {
        public Bloodline Id;
        public string Name;
        public string Epithet;
        public string Description;
        /// <summary>The bloodline's three gifts, by slot (filled from <see cref="GameData.Gifts"/>).</summary>
        public GiftDef[] Gifts = new GiftDef[GameRules.GiftSlots];
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
        public const int ProtocolVersion = 6;
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

        // Gifts
        public const int GiftSlots = 3;
        /// <summary>Standing spent to learn the gift in each slot (slot 0 is innate).</summary>
        public static readonly int[] GiftStandingCost = { 0, 5, 12 };

        // Bloodline gift tuning
        /// <summary>A shrouded vampire is visible to rivals only this close.</summary>
        public const float ShroudRevealRange = 6f;
        /// <summary>Hunters lose a shrouded quarry that is farther away than this.</summary>
        public const float ShroudHunterRange = 4f;
        public const float EmboldenedSpeedMultiplier = 1.3f;
        public const float UnchainedMeleeCooldown = 0.5f;
        public const float UnchainedHealPerStrike = 1f;

        // Lineage: the Embrace, sires and childer
        /// <summary>Standing a childe in tutelage pays to learn a gift its sire knows (instead of GiftStandingCost).</summary>
        public static readonly int[] TaughtGiftStandingCost = { 0, 3, 7 };
        public const float EmbraceRange = 3f;
        public const float EmbraceBloodCost = 3f;
        public const float EmbraceOfferSeconds = 60f;
        public const int MaxChilderInTutelage = 3;
        /// <summary>Sire and childe within this distance share the blood bond.</summary>
        public const float BondRange = 20f;
        public const float BondDamageTaken = 0.8f;
        public const float BondMendInterval = 1f;
        public const int TeachStanding = 1;
        public const int ReleaseStanding = 3;
        public const int ShameMortalKill = 1;
        public const int ShameBrokeHush = 1;
        public const int ShameOutlaw = 2;

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
                MaxBlood = 10f, MaxHealth = 13f, MeleeDamage = 2f, FavoredDistrict = 0,
                R = 0.75f, G = 0.25f, B = 0.18f
            },
            new BloodlineDef
            {
                Id = Bloodline.Sylphine, Name = "Sylphine", Epithet = "the Velvet Choir",
                Description = "Artists, muses and heartbreakers who feed on adoration as much as blood.",
                MaxBlood = 11f, MaxHealth = 10f, MeleeDamage = 1f, FavoredDistrict = 1,
                R = 0.85f, G = 0.35f, B = 0.65f
            },
            new BloodlineDef
            {
                Id = Bloodline.Ashborn, Name = "Ashborn", Epithet = "the Grave Choir",
                Description = "Scholars of the occult who shape their own blood into weapons.",
                MaxBlood = 12f, MaxHealth = 9f, MeleeDamage = 1f, FavoredDistrict = 2,
                R = 0.45f, G = 0.3f, B = 0.8f
            },
            new BloodlineDef
            {
                Id = Bloodline.Aurum, Name = "Aurum", Epithet = "the Gilded Court",
                Description = "Old money and older blood. Their word is a chain upon lesser minds.",
                MaxBlood = 10f, MaxHealth = 11f, MeleeDamage = 1f, FavoredDistrict = 3,
                R = 0.9f, G = 0.75f, B = 0.3f
            },
            new BloodlineDef
            {
                Id = Bloodline.Umbren, Name = "Umbren", Epithet = "the Silent Watch",
                Description = "Spies and information brokers of the tunnels under the city. Seldom seen, they always know.",
                MaxBlood = 11f, MaxHealth = 10f, MeleeDamage = 1f, FavoredDistrict = 1,
                R = 0.3f, G = 0.62f, B = 0.66f
            },
            new BloodlineDef
            {
                Id = Bloodline.Varga, Name = "Varga", Epithet = "the Hollow Pack",
                Description = "Feral hunters of the waterfront and the rail yards who run with the city's strays.",
                MaxBlood = 10f, MaxHealth = 12f, MeleeDamage = 2f, FavoredDistrict = 0,
                R = 0.47f, G = 0.68f, B = 0.3f
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

        public static readonly GiftDef[] Gifts =
        {
            // Vorlen, the Iron Kin
            new GiftDef { Id = GiftId.Surge, Line = Bloodline.Vorlen, Tier = 1, Name = "Surge", Target = PowerTarget.Self,
                Cost = 2f, Cooldown = 15f, Duration = 6f,
                Description = "Flood your limbs with blood: +60% speed and double melee damage for 6s." },
            new GiftDef { Id = GiftId.IronSkin, Line = Bloodline.Vorlen, Tier = 2, Name = "Iron Skin", Target = PowerTarget.Self,
                Cost = 2f, Cooldown = 25f, Duration = 8f,
                Description = "Flesh hardens like cast iron for 8s: half damage, immune to roots, slows and stuns." },
            new GiftDef { Id = GiftId.Earthshaker, Line = Bloodline.Vorlen, Tier = 3, Name = "Earthshaker", Target = PowerTarget.Self,
                Cost = 3f, Cooldown = 30f, Range = 6f,
                Description = "Slam the street: 3 damage and a 1.5s stun to rivals and hunters within 6m. Mortals flee." },

            // Sylphine, the Velvet Choir
            new GiftDef { Id = GiftId.Allure, Line = Bloodline.Sylphine, Tier = 1, Name = "Allure", Target = PowerTarget.Other,
                Cost = 1f, Cooldown = 10f, Range = 12f,
                Description = "Mortals become entranced and follow you; rivals are slowed; hunters falter." },
            new GiftDef { Id = GiftId.Glamour, Line = Bloodline.Sylphine, Tier = 2, Name = "Glamour", Target = PowerTarget.Self,
                Cost = 2f, Cooldown = 30f, Duration = 10f, Subtle = true,
                Description = "For 10s every eye slides off you: no exposure from witnesses, and the Beast in you frightens no one." },
            new GiftDef { Id = GiftId.Rapture, Line = Bloodline.Sylphine, Tier = 3, Name = "Rapture", Target = PowerTarget.Self,
                Cost = 3f, Cooldown = 40f, Range = 15f,
                Description = "Every mortal within 15m is enthralled, hunters falter for 3s and rivals within 10m are slowed." },

            // Ashborn, the Grave Choir
            new GiftDef { Id = GiftId.BloodLash, Line = Bloodline.Ashborn, Tier = 1, Name = "Blood Lash", Target = PowerTarget.Other,
                Cost = 2f, Cooldown = 6f, Range = 15f,
                Description = "Hurl a whip of vitae at a target up to 15m away for 3 damage." },
            new GiftDef { Id = GiftId.Siphon, Line = Bloodline.Ashborn, Tier = 2, Name = "Siphon", Target = PowerTarget.Other,
                Cost = 1f, Cooldown = 14f, Range = 10f,
                Description = "Draw up to 2 blood out of a target 10m away and into your veins. Drained mortals die." },
            new GiftDef { Id = GiftId.CrimsonMist, Line = Bloodline.Ashborn, Tier = 3, Name = "Crimson Mist", Target = PowerTarget.Self,
                Cost = 3f, Cooldown = 45f, Duration = 4f,
                Description = "Dissolve into red mist for 4s: untouchable and 30% faster; hunters lose your trail." },

            // Aurum, the Gilded Court
            new GiftDef { Id = GiftId.Command, Line = Bloodline.Aurum, Tier = 1, Name = "Command", Target = PowerTarget.Other,
                Cost = 2f, Cooldown = 12f, Range = 9f, Subtle = true,
                Description = "A mortal forgets what it saw (-15 exposure); rivals are rooted, hunters stunned." },
            new GiftDef { Id = GiftId.Majesty, Line = Bloodline.Aurum, Tier = 2, Name = "Majesty", Target = PowerTarget.Self,
                Cost = 2f, Cooldown = 30f, Duration = 8f, Range = 6f, Subtle = true,
                Description = "For 8s rivals cannot bring themselves to strike you, and hunters keep 6m away." },
            new GiftDef { Id = GiftId.Dominate, Line = Bloodline.Aurum, Tier = 3, Name = "Dominate", Target = PowerTarget.Other,
                Cost = 3f, Cooldown = 40f, Range = 9f, Duration = 6f, Subtle = true,
                Description = "A hunter abandons the hunt; a rival is rooted 3s and silenced 6s; a mortal makes every witness within 10m forget (-30 exposure)." },

            // Umbren, the Silent Watch
            new GiftDef { Id = GiftId.Shroud, Line = Bloodline.Umbren, Tier = 1, Name = "Shroud", Target = PowerTarget.Self,
                Cost = 2f, Cooldown = 20f, Duration = 10f, Subtle = true,
                Description = "Fold the shadows around you for 10s: rivals farther than 6m cannot see or target you, and hunters lose you beyond 4m. Striking, feeding or another gift ends it." },
            new GiftDef { Id = GiftId.Whispers, Line = Bloodline.Umbren, Tier = 2, Name = "Whispers", Target = PowerTarget.Self,
                Cost = 2f, Cooldown = 30f, Duration = 20f, Range = 150f, Subtle = true,
                Description = "The city's rats and gossips report: for 20s you sense every vampire within 150m, even shrouded ones, and learn where the nearest are." },
            new GiftDef { Id = GiftId.Undercroft, Line = Bloodline.Umbren, Tier = 3, Name = "Undercroft", Target = PowerTarget.Self,
                Cost = 3f, Cooldown = 60f, Duration = 2f,
                Description = "Slip into the tunnels under the city: stand still for 2s (a blow breaks it) and you emerge in your haven, even by day." },

            // Varga, the Hollow Pack
            new GiftDef { Id = GiftId.Pounce, Line = Bloodline.Varga, Tier = 1, Name = "Pounce", Target = PowerTarget.Other,
                Cost = 2f, Cooldown = 10f, Range = 12f,
                Description = "Leap onto a target up to 12m away: rivals and hunters take 2 damage and are stunned 1s; a mortal is pinned, calm and ready to feed, for 4s." },
            new GiftDef { Id = GiftId.PackHowl, Line = Bloodline.Varga, Tier = 2, Name = "Pack Howl", Target = PowerTarget.Self,
                Cost = 2f, Cooldown = 35f, Range = 25f, Duration = 6f,
                Description = "Every stray answers: hunters within 25m cower for 4s, mortals scatter, rivals within 10m are slowed; you, your circle and your kin run 30% faster for 6s." },
            new GiftDef { Id = GiftId.Unchained, Line = Bloodline.Varga, Tier = 3, Name = "Unchained", Target = PowerTarget.Self,
                Cost = 3f, Cooldown = 40f, Duration = 10f,
                Description = "Let the beast out for 10s: strike twice as fast, every blow heals you 1, immune to roots, slows and stuns. You cannot feed, and mortals flee from you." },
        };

        static GameData()
        {
            foreach (GiftDef g in Gifts) Bloodlines[(int)g.Line].Gifts[g.Slot] = g;
        }

        public static GiftDef GetGift(GiftId id)
        {
            return Gifts[(int)id];
        }

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
