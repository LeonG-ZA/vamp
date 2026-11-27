using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sanguine
{
    /// <summary>Message ids. Client to server are below 100, server to client are 100 and up.</summary>
    public enum Msg : byte
    {
        // Client -> Server
        Login = 1,
        Move = 2,
        FeedStart = 3,
        FeedStop = 4,
        Attack = 5,
        Power = 6,
        Chat = 7,

        // Server -> Client
        Welcome = 100,
        LoginFailed = 101,
        Snapshot = 102,
        EntityInfo = 103,
        SelfState = 104,
        ChatMsg = 105,
        Teleport = 106,
        WorldState = 107,
        Notice = 108,
        Lineage = 109,
    }

    public enum ChatChannel : byte { Local = 0, Circle = 1, City = 2, System = 3, Lineage = 4 }

    /// <summary>Where a vampire stands in their line.</summary>
    public enum LineageStatus : byte
    {
        Fledgling = 0,   // no sire and no second gift yet: may still be Embraced
        Unclaimed = 1,   // came into their power without a sire
        Tutelage = 2,    // childe still under their sire's tutelage
        Released = 3,    // childe released by their sire
    }

    public enum NoticeLevel : byte { Info = 0, Good = 1, Warning = 2, Danger = 3 }

    [Flags]
    public enum EntityFlags : ushort
    {
        None = 0,
        Down = 1,       // torpor (players) or dead (NPCs)
        Feeding = 2,    // player is feeding / mortal is being fed upon
        Alarmed = 4,    // mortal fleeing
        Marked = 8,     // player is fair game in PvP
        Charmed = 16,
        Stunned = 32,
        Surging = 64,
        Regent = 128,
        Mist = 256,     // Crimson Mist: shown as swirling red mist
        IronSkin = 512,
        Majesty = 1024,
    }

    [Flags]
    public enum SelfFlags : int
    {
        None = 0,
        Marked = 1 << 0,
        Outlaw = 1 << 1,
        BloodHunt = 1 << 2,
        InHaven = 1 << 3,
        InSanctum = 1 << 4,
        Sunlight = 1 << 5,
        Surge = 1 << 6,
        Slowed = 1 << 7,
        Rooted = 1 << 8,
        Torpor = 1 << 9,
        Feeding = 1 << 10,
        Beast = 1 << 11,
        Starving = 1 << 12,
        Regent = 1 << 13,
        IronSkin = 1 << 14,
        Glamour = 1 << 15,
        Mist = 1 << 16,
        Majesty = 1 << 17,
        Silenced = 1 << 18,
        Bonded = 1 << 19,      // sire or childe nearby: the blood bond
    }

    public enum ThrallState : byte { Idle = 0, OnMission = 1, Recovering = 2 }

    public sealed class PacketWriter
    {
        private readonly MemoryStream _ms = new MemoryStream(256);
        private readonly BinaryWriter _w;

        public PacketWriter(Msg type)
        {
            _w = new BinaryWriter(_ms, Encoding.UTF8);
            _w.Write((byte)type);
        }

        public PacketWriter Byte(byte v) { _w.Write(v); return this; }
        public PacketWriter Bool(bool v) { _w.Write(v); return this; }
        public PacketWriter Int(int v) { _w.Write(v); return this; }
        public PacketWriter UShort(ushort v) { _w.Write(v); return this; }
        public PacketWriter Float(float v) { _w.Write(v); return this; }
        public PacketWriter Str(string v) { _w.Write(v ?? string.Empty); return this; }

        public byte[] ToArray() { _w.Flush(); return _ms.ToArray(); }
    }

    public sealed class PacketReader
    {
        private readonly BinaryReader _r;
        public readonly Msg Type;

        public PacketReader(byte[] data)
        {
            _r = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8);
            Type = (Msg)_r.ReadByte();
        }

        public byte Byte() { return _r.ReadByte(); }
        public bool Bool() { return _r.ReadBoolean(); }
        public int Int() { return _r.ReadInt32(); }
        public ushort UShort() { return _r.ReadUInt16(); }
        public float Float()
        {
            float f = _r.ReadSingle();
            if (float.IsNaN(f) || float.IsInfinity(f)) throw new InvalidDataException("bad float");
            return f;
        }
        public string Str(int maxLen = 512)
        {
            string s = _r.ReadString();
            if (s.Length > maxLen) s = s.Substring(0, maxLen);
            return s;
        }
    }

    // ---------------------------------------------------------------------
    // Typed messages shared by client and server.
    // ---------------------------------------------------------------------

    public struct LoginMsg
    {
        public int Version;
        public string Name, Password;
        public Bloodline Bloodline;

        public byte[] Write()
        {
            return new PacketWriter(Msg.Login).Int(Version).Str(Name).Str(Password).Byte((byte)Bloodline).ToArray();
        }

        public static LoginMsg Read(PacketReader r)
        {
            return new LoginMsg { Version = r.Int(), Name = r.Str(32), Password = r.Str(64), Bloodline = (Bloodline)r.Byte() };
        }
    }

    public struct WelcomeMsg
    {
        public int EntityId;
        public string Name;
        public Bloodline Bloodline;
        public int CitySeed;
        public float X, Z;
        public bool NewCharacter;

        public byte[] Write()
        {
            return new PacketWriter(Msg.Welcome).Int(EntityId).Str(Name).Byte((byte)Bloodline).Int(CitySeed)
                .Float(X).Float(Z).Bool(NewCharacter).ToArray();
        }

        public static WelcomeMsg Read(PacketReader r)
        {
            return new WelcomeMsg
            {
                EntityId = r.Int(), Name = r.Str(), Bloodline = (Bloodline)r.Byte(), CitySeed = r.Int(),
                X = r.Float(), Z = r.Float(), NewCharacter = r.Bool()
            };
        }
    }

    public struct EntityState
    {
        public int Id;
        public EntityKind Kind;
        public float X, Z, Rot;
        public byte HealthPct;
        public EntityFlags Flags;
    }

    public static class SnapshotMsg
    {
        public static byte[] Write(float serverTime, List<EntityState> states)
        {
            var w = new PacketWriter(Msg.Snapshot).Float(serverTime).Int(states.Count);
            for (int i = 0; i < states.Count; i++)
            {
                EntityState s = states[i];
                w.Int(s.Id).Byte((byte)s.Kind).Float(s.X).Float(s.Z).Float(s.Rot).Byte(s.HealthPct).UShort((ushort)s.Flags);
            }
            return w.ToArray();
        }

        public static float Read(PacketReader r, List<EntityState> into)
        {
            into.Clear();
            float t = r.Float();
            int n = r.Int();
            if (n < 0 || n > 4096) throw new InvalidDataException("bad snapshot");
            for (int i = 0; i < n; i++)
            {
                into.Add(new EntityState
                {
                    Id = r.Int(), Kind = (EntityKind)r.Byte(), X = r.Float(), Z = r.Float(), Rot = r.Float(),
                    HealthPct = r.Byte(), Flags = (EntityFlags)r.UShort()
                });
            }
            return t;
        }
    }

    public struct EntityInfoMsg
    {
        public int Id;
        public EntityKind Kind;
        public string Name;
        public Bloodline Bloodline;
        public string Circle;
        /// <summary>Name of the vampire's sire, or empty.</summary>
        public string Sire;

        public byte[] Write()
        {
            return new PacketWriter(Msg.EntityInfo).Int(Id).Byte((byte)Kind).Str(Name).Byte((byte)Bloodline).Str(Circle).Str(Sire).ToArray();
        }

        public static EntityInfoMsg Read(PacketReader r)
        {
            return new EntityInfoMsg
            {
                Id = r.Int(), Kind = (EntityKind)r.Byte(), Name = r.Str(), Bloodline = (Bloodline)r.Byte(), Circle = r.Str(), Sire = r.Str()
            };
        }
    }

    public struct ThrallInfo
    {
        public ThrallState State;
        public byte District;
        public float Remaining;
    }

    public sealed class SelfStateMsg
    {
        public float Blood, MaxBlood, Health, MaxHealth, Conscience, Exposure;
        public int Standing;
        public SelfFlags Flags;
        /// <summary>Current bloodline (it changes when a fledgling is Embraced into another line).</summary>
        public Bloodline Bloodline;
        public float AttackCooldown;
        /// <summary>Remaining cooldown of each gift slot.</summary>
        public float[] GiftCooldowns = new float[GameRules.GiftSlots];
        /// <summary>Bit mask of learned gift slots (bit 0, the innate gift, is always set).</summary>
        public byte LearnedGifts = 1;
        public int FeedTarget;
        public byte District;
        public ThrallInfo[] Thralls = new ThrallInfo[GameRules.ThrallsPerPlayer];

        public byte[] Write()
        {
            var w = new PacketWriter(Msg.SelfState)
                .Float(Blood).Float(MaxBlood).Float(Health).Float(MaxHealth).Float(Conscience).Float(Exposure)
                .Int(Standing).Int((int)Flags).Byte((byte)Bloodline).Float(AttackCooldown).Int(FeedTarget).Byte(District).Byte(LearnedGifts);
            for (int i = 0; i < GiftCooldowns.Length; i++) w.Float(GiftCooldowns[i]);
            w.Byte((byte)Thralls.Length);
            for (int i = 0; i < Thralls.Length; i++)
                w.Byte((byte)Thralls[i].State).Byte(Thralls[i].District).Float(Thralls[i].Remaining);
            return w.ToArray();
        }

        public static SelfStateMsg Read(PacketReader r)
        {
            var m = new SelfStateMsg
            {
                Blood = r.Float(), MaxBlood = r.Float(), Health = r.Float(), MaxHealth = r.Float(),
                Conscience = r.Float(), Exposure = r.Float(), Standing = r.Int(), Flags = (SelfFlags)r.Int(),
                Bloodline = (Bloodline)r.Byte(), AttackCooldown = r.Float(), FeedTarget = r.Int(), District = r.Byte(), LearnedGifts = r.Byte()
            };
            for (int i = 0; i < m.GiftCooldowns.Length; i++) m.GiftCooldowns[i] = r.Float();
            int n = r.Byte();
            m.Thralls = new ThrallInfo[n];
            for (int i = 0; i < n; i++)
                m.Thralls[i] = new ThrallInfo { State = (ThrallState)r.Byte(), District = r.Byte(), Remaining = r.Float() };
            return m;
        }
    }

    public sealed class WorldStateMsg
    {
        public float CyclePhase;     // 0..1 through the whole night+day cycle
        public bool IsDay;
        public float SecondsToChange;
        public int Night;            // night counter
        public string Regent = "";
        public string Edict = "";
        public string MyCircle = "";
        public string MyVote = "";
        public string[] DistrictControllers = new string[4];
        public string[] TopCandidates = new string[0];

        public byte[] Write()
        {
            var w = new PacketWriter(Msg.WorldState).Float(CyclePhase).Bool(IsDay).Float(SecondsToChange).Int(Night)
                .Str(Regent).Str(Edict).Str(MyCircle).Str(MyVote).Byte((byte)DistrictControllers.Length);
            for (int i = 0; i < DistrictControllers.Length; i++) w.Str(DistrictControllers[i]);
            w.Byte((byte)TopCandidates.Length);
            for (int i = 0; i < TopCandidates.Length; i++) w.Str(TopCandidates[i]);
            return w.ToArray();
        }

        public static WorldStateMsg Read(PacketReader r)
        {
            var m = new WorldStateMsg
            {
                CyclePhase = r.Float(), IsDay = r.Bool(), SecondsToChange = r.Float(), Night = r.Int(),
                Regent = r.Str(), Edict = r.Str(), MyCircle = r.Str(), MyVote = r.Str()
            };
            int n = r.Byte();
            m.DistrictControllers = new string[n];
            for (int i = 0; i < n; i++) m.DistrictControllers[i] = r.Str();
            int c = r.Byte();
            m.TopCandidates = new string[c];
            for (int i = 0; i < c; i++) m.TopCandidates[i] = r.Str();
            return m;
        }
    }

    /// <summary>A sire or childe as seen in the lineage panel.</summary>
    public struct KinInfo
    {
        public string Name;
        public bool Online;
        public bool Released;     // childer only: released from tutelage
        public byte GiftsKnown;   // 1-3
    }

    /// <summary>The player's own place in the family tree. Sent on change and every couple of seconds.</summary>
    public sealed class LineageMsg
    {
        public LineageStatus Status;
        public KinInfo Sire;                              // Name empty when there is no sire
        public string[] Ancestors = new string[0];        // grandsire first, then further back
        public string[] Siblings = new string[0];         // the sire's other childer
        public KinInfo[] Childer = new KinInfo[0];
        /// <summary>Every member of the family (the whole tree under the eldest ancestor), for nameplates.</summary>
        public string[] Family = new string[0];
        public int FamilyOnline;
        public bool CanSire;
        public string OfferFrom = "";                     // a pending Embrace offer
        public Bloodline OfferBloodline;
        public float OfferSeconds;

        public byte[] Write()
        {
            var w = new PacketWriter(Msg.Lineage).Byte((byte)Status);
            WriteKin(w, Sire);
            WriteStrings(w, Ancestors);
            WriteStrings(w, Siblings);
            w.Byte((byte)Childer.Length);
            for (int i = 0; i < Childer.Length; i++) WriteKin(w, Childer[i]);
            WriteStrings(w, Family);
            w.Int(FamilyOnline).Bool(CanSire).Str(OfferFrom).Byte((byte)OfferBloodline).Float(OfferSeconds);
            return w.ToArray();
        }

        public static LineageMsg Read(PacketReader r)
        {
            var m = new LineageMsg { Status = (LineageStatus)r.Byte() };
            m.Sire = ReadKin(r);
            m.Ancestors = ReadStrings(r);
            m.Siblings = ReadStrings(r);
            int n = r.Byte();
            m.Childer = new KinInfo[n];
            for (int i = 0; i < n; i++) m.Childer[i] = ReadKin(r);
            m.Family = ReadStrings(r);
            m.FamilyOnline = r.Int();
            m.CanSire = r.Bool();
            m.OfferFrom = r.Str();
            m.OfferBloodline = (Bloodline)r.Byte();
            m.OfferSeconds = r.Float();
            return m;
        }

        private static void WriteKin(PacketWriter w, KinInfo k)
        {
            w.Str(k.Name).Bool(k.Online).Bool(k.Released).Byte(k.GiftsKnown);
        }

        private static KinInfo ReadKin(PacketReader r)
        {
            return new KinInfo { Name = r.Str(), Online = r.Bool(), Released = r.Bool(), GiftsKnown = r.Byte() };
        }

        private static void WriteStrings(PacketWriter w, string[] a)
        {
            int n = Math.Min(a.Length, 255);
            w.Byte((byte)n);
            for (int i = 0; i < n; i++) w.Str(a[i]);
        }

        private static string[] ReadStrings(PacketReader r)
        {
            int n = r.Byte();
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = r.Str();
            return a;
        }
    }

    public static class SimpleMsg
    {
        public static byte[] Move(float x, float z, float rot) { return new PacketWriter(Msg.Move).Float(x).Float(z).Float(rot).ToArray(); }
        public static byte[] FeedStart(int target) { return new PacketWriter(Msg.FeedStart).Int(target).ToArray(); }
        public static byte[] FeedStop() { return new PacketWriter(Msg.FeedStop).ToArray(); }
        public static byte[] Attack(int target) { return new PacketWriter(Msg.Attack).Int(target).ToArray(); }
        public static byte[] Power(int slot, int target) { return new PacketWriter(Msg.Power).Byte((byte)slot).Int(target).ToArray(); }
        public static byte[] Chat(ChatChannel ch, string text) { return new PacketWriter(Msg.Chat).Byte((byte)ch).Str(text).ToArray(); }

        public static byte[] LoginFailed(string reason) { return new PacketWriter(Msg.LoginFailed).Str(reason).ToArray(); }
        public static byte[] ChatMsg(ChatChannel ch, string from, string text) { return new PacketWriter(Msg.ChatMsg).Byte((byte)ch).Str(from).Str(text).ToArray(); }
        public static byte[] Teleport(float x, float z) { return new PacketWriter(Msg.Teleport).Float(x).Float(z).ToArray(); }
        public static byte[] Notice(NoticeLevel level, string text) { return new PacketWriter(Msg.Notice).Byte((byte)level).Str(text).ToArray(); }
    }
}
