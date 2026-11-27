using System.Collections.Generic;

namespace Sanguine.Server
{
    public abstract class Entity
    {
        public int Id;
        public EntityKind Kind;
        public string Name = "";
        public float X, Z, Rot;
        public float Health, MaxHealth;
        public double StunnedUntil;

        public bool IsStunned(double now) { return now < StunnedUntil; }

        public float DistanceTo(Entity o) { return Dist(X, Z, o.X, o.Z); }

        public static float Dist(float ax, float az, float bx, float bz)
        {
            float dx = ax - bx, dz = az - bz;
            return (float)System.Math.Sqrt(dx * dx + dz * dz);
        }

        public abstract bool IsDown { get; }
    }

    public enum MortalMode { Walking, Fleeing, Charmed, Entranced, Dead }

    public sealed class Mortal : Entity
    {
        public int HomeDistrict;
        public float Blood = GameRules.MortalMaxBlood;
        public MortalMode Mode = MortalMode.Walking;
        public int TargetNode;
        public float OffsetX, OffsetZ;          // lateral offset so mortals use the "sidewalks"
        public double ModeUntil;                // flee / charm end time
        public float FleeFromX, FleeFromZ;
        public int CharmedBy;                   // player entity id
        public int FedUponBy;                   // player entity id or 0
        public double RespawnAt;
        public double NextBloodRegen;

        public override bool IsDown { get { return Mode == MortalMode.Dead; } }
    }

    public sealed class Hunter : Entity
    {
        public int TargetPlayer;               // entity id
        public double NextAttack;
        public double LostTargetSince = -1;
        public float WayX, WayZ;               // current street-route waypoint
        public double NextRepath;

        public override bool IsDown { get { return Health <= 0f; } }
    }

    public sealed class Thrall
    {
        public ThrallState State;
        public int District;
        public double Until;
    }

    public sealed class PlayerEntity : Entity
    {
        public Session Session;
        public PlayerProfile Profile;
        public BloodlineDef Def;

        public float Exposure;
        public double LastExposureGain = -999;
        public int FeedTarget;                 // mortal id or 0
        public float FeedStartX, FeedStartZ;
        public float FedThisFeeding;

        public double AttackReadyAt;
        public readonly double[] GiftReadyAt = new double[GameRules.GiftSlots];
        public double IronSkinUntil, GlamourUntil, MistUntil, MajestyUntil, SilencedUntil;
        public double SurgeUntil, SlowUntil, RootUntil;
        public double TorporUntil = -1;
        public double OutlawUntil, BloodHuntUntil;
        public double LastCombat = -999;
        public double NextMend, NextBloodDecay, NextStarve, NextSun;
        public double LastMoveTime;
        public double NextHunterCheck;
        public bool WarnedFaltering;

        public readonly Thrall[] Thralls = new Thrall[GameRules.ThrallsPerPlayer];
        public readonly HashSet<int> Known = new HashSet<int>();

        public PlayerEntity()
        {
            Kind = EntityKind.Player;
            for (int i = 0; i < Thralls.Length; i++) Thralls[i] = new Thrall();
        }

        public bool InTorpor { get { return TorporUntil > 0; } }
        public override bool IsDown { get { return InTorpor; } }

        public float Blood { get { return Profile.Blood; } set { Profile.Blood = value; } }
        public float Conscience { get { return Profile.Conscience; } set { Profile.Conscience = value; } }
        public int Standing { get { return Profile.Standing; } set { Profile.Standing = value; } }
        public float MaxBlood { get { return Def.MaxBlood; } }

        public bool HasGift(int slot) { return slot == 0 || (Profile.Gifts & (1 << slot)) != 0; }
    }

    /// <summary>A network connection, logged in or not.</summary>
    public sealed class Session
    {
        public int Id;
        public Connection Conn;
        public PlayerEntity Player;            // null until login succeeds
        public double ConnectedAt;
        public double NextChatAllowed;
        public int ChatBurst;

        public void Send(byte[] data) { Conn.Send(data); }
    }
}
