using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Sanguine.Server
{
    /// <summary>Persistent character data (survives logout and server restarts).</summary>
    public sealed class PlayerProfile
    {
        public string Name = "";
        public string Salt = "";
        public string PasswordHash = "";
        public Bloodline Bloodline;
        public float Blood = 6f;
        public float Health = 10f;
        public float Conscience = 7f;
        public int Standing;
        public float X, Z;
        public string Circle = "";
        public int Kills, Feedings, Missions;
        /// <summary>Bit mask of learned gift slots; slot 0 (bit 0) is innate.</summary>
        public int Gifts = 1;
        /// <summary>Profile key of the vampire who Embraced this one, or empty.</summary>
        public string Sire = "";
        /// <summary>True once the sire has released this childe from tutelage.</summary>
        public bool Released;

        public string Key { get { return Store.KeyOf(Name); } }

        public void SetPassword(string password)
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Salt = Convert.ToBase64String(bytes);
            PasswordHash = Hash(Salt, password);
        }

        public bool CheckPassword(string password)
        {
            return PasswordHash == Hash(Salt, password);
        }

        private static string Hash(string salt, string password)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + (password ?? "")));
                return Convert.ToBase64String(h);
            }
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            Kv(sb, "name", Name);
            Kv(sb, "salt", Salt);
            Kv(sb, "hash", PasswordHash);
            Kv(sb, "bloodline", ((int)Bloodline).ToString(CultureInfo.InvariantCulture));
            Kv(sb, "blood", F(Blood));
            Kv(sb, "health", F(Health));
            Kv(sb, "conscience", F(Conscience));
            Kv(sb, "standing", Standing.ToString(CultureInfo.InvariantCulture));
            Kv(sb, "x", F(X));
            Kv(sb, "z", F(Z));
            Kv(sb, "circle", Circle);
            Kv(sb, "kills", Kills.ToString(CultureInfo.InvariantCulture));
            Kv(sb, "feedings", Feedings.ToString(CultureInfo.InvariantCulture));
            Kv(sb, "missions", Missions.ToString(CultureInfo.InvariantCulture));
            Kv(sb, "gifts", Gifts.ToString(CultureInfo.InvariantCulture));
            Kv(sb, "sire", Sire);
            Kv(sb, "released", Released ? "1" : "0");
            return sb.ToString();
        }

        public static PlayerProfile Deserialize(string text)
        {
            var d = Store.ParseKv(text);
            var p = new PlayerProfile();
            p.Name = Get(d, "name", "");
            p.Salt = Get(d, "salt", "");
            p.PasswordHash = Get(d, "hash", "");
            p.Bloodline = (Bloodline)Store.ParseInt(Get(d, "bloodline", "0"));
            p.Blood = Store.ParseFloat(Get(d, "blood", "6"));
            p.Health = Store.ParseFloat(Get(d, "health", "10"));
            p.Conscience = Store.ParseFloat(Get(d, "conscience", "7"));
            p.Standing = Store.ParseInt(Get(d, "standing", "0"));
            p.X = Store.ParseFloat(Get(d, "x", "0"));
            p.Z = Store.ParseFloat(Get(d, "z", "0"));
            p.Circle = Get(d, "circle", "");
            p.Kills = Store.ParseInt(Get(d, "kills", "0"));
            p.Feedings = Store.ParseInt(Get(d, "feedings", "0"));
            p.Missions = Store.ParseInt(Get(d, "missions", "0"));
            p.Gifts = Store.ParseInt(Get(d, "gifts", "1")) | 1;
            p.Sire = Store.KeyOf(Get(d, "sire", ""));
            p.Released = Get(d, "released", "0") == "1";
            return p;
        }

        private static string Get(Dictionary<string, string> d, string k, string def)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : def;
        }

        private static string F(float f) { return f.ToString("R", CultureInfo.InvariantCulture); }

        private static void Kv(StringBuilder sb, string k, string v)
        {
            sb.Append(k).Append('=').Append((v ?? "").Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
        }
    }

    /// <summary>A player-founded faction (the game's guild / coterie equivalent).</summary>
    public sealed class Circle
    {
        public string Name = "";
        public string Founder = "";                  // profile key
        public readonly List<string> Members = new List<string>();      // profile keys
        public readonly HashSet<string> Invites = new HashSet<string>(); // profile keys (not persisted)
    }

    /// <summary>Persistent city-wide state: politics, circles and district influence.</summary>
    public sealed class WorldSave
    {
        public int Night = 1;
        public string Regent = "";      // display name
        public string Edict = "";
        public readonly Dictionary<string, Circle> Circles = new Dictionary<string, Circle>(); // key: lower-case name
        public readonly Dictionary<string, int>[] Influence =
        {
            new Dictionary<string, int>(), new Dictionary<string, int>(),
            new Dictionary<string, int>(), new Dictionary<string, int>()
        };

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("night=").Append(Night.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("regent=").Append(Regent).Append('\n');
            sb.Append("edict=").Append(Edict.Replace('\n', ' ')).Append('\n');
            int ci = 0;
            foreach (var c in Circles.Values)
            {
                sb.Append("circle.").Append(ci++).Append('=').Append(c.Name).Append('|').Append(c.Founder).Append('|')
                  .Append(string.Join(",", c.Members.ToArray())).Append('\n');
            }
            for (int d = 0; d < Influence.Length; d++)
            {
                var parts = new List<string>();
                foreach (var kv in Influence[d]) parts.Add(kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture));
                sb.Append("influence.").Append(d).Append('=').Append(string.Join(",", parts.ToArray())).Append('\n');
            }
            return sb.ToString();
        }

        public static WorldSave Deserialize(string text)
        {
            var w = new WorldSave();
            var d = Store.ParseKv(text);
            foreach (var kv in d)
            {
                if (kv.Key == "night") w.Night = Math.Max(1, Store.ParseInt(kv.Value));
                else if (kv.Key == "regent") w.Regent = kv.Value;
                else if (kv.Key == "edict") w.Edict = kv.Value;
                else if (kv.Key.StartsWith("circle.", StringComparison.Ordinal))
                {
                    string[] p = kv.Value.Split('|');
                    if (p.Length < 3 || p[0].Length == 0) continue;
                    var c = new Circle { Name = p[0], Founder = p[1] };
                    foreach (string m in p[2].Split(',')) if (m.Length > 0) c.Members.Add(m);
                    w.Circles[Store.KeyOf(c.Name)] = c;
                }
                else if (kv.Key.StartsWith("influence.", StringComparison.Ordinal))
                {
                    int di = Store.ParseInt(kv.Key.Substring("influence.".Length));
                    if (di < 0 || di >= w.Influence.Length) continue;
                    foreach (string part in kv.Value.Split(','))
                    {
                        int colon = part.LastIndexOf(':');
                        if (colon <= 0) continue;
                        w.Influence[di][part.Substring(0, colon)] = Store.ParseInt(part.Substring(colon + 1));
                    }
                }
            }
            return w;
        }
    }

    /// <summary>Plain-text file store. Swap for a database in production.</summary>
    public sealed class Store
    {
        private readonly string _dir;
        private readonly Action<string> _log;

        public Store(string dir, Action<string> log)
        {
            _dir = dir;
            _log = log;
            if (Enabled)
            {
                Directory.CreateDirectory(Path.Combine(_dir, "players"));
            }
        }

        public bool Enabled { get { return !string.IsNullOrEmpty(_dir); } }

        public static string KeyOf(string name) { return (name ?? "").Trim().ToLowerInvariant(); }

        public Dictionary<string, PlayerProfile> LoadAllProfiles()
        {
            var result = new Dictionary<string, PlayerProfile>();
            if (!Enabled) return result;
            foreach (string file in Directory.GetFiles(Path.Combine(_dir, "players"), "*.txt"))
            {
                try
                {
                    var p = PlayerProfile.Deserialize(File.ReadAllText(file, Encoding.UTF8));
                    if (p.Name.Length > 0) result[p.Key] = p;
                }
                catch (Exception e) { _log("Failed to load profile " + file + ": " + e.Message); }
            }
            return result;
        }

        public void SaveProfile(PlayerProfile p)
        {
            if (!Enabled) return;
            WriteAtomic(Path.Combine(Path.Combine(_dir, "players"), p.Key + ".txt"), p.Serialize());
        }

        public WorldSave LoadWorld()
        {
            if (!Enabled) return new WorldSave();
            string path = Path.Combine(_dir, "world.txt");
            if (!File.Exists(path)) return new WorldSave();
            try { return WorldSave.Deserialize(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception e) { _log("Failed to load world: " + e.Message); return new WorldSave(); }
        }

        public void SaveWorld(WorldSave w)
        {
            if (!Enabled) return;
            WriteAtomic(Path.Combine(_dir, "world.txt"), w.Serialize());
        }

        private void WriteAtomic(string path, string content)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content, Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { _log("Save failed for " + path + ": " + e.Message); }
        }

        public static Dictionary<string, string> ParseKv(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return d;
        }

        public static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        public static float ParseFloat(string s)
        {
            float v;
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return 0f;
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
            return v;
        }
    }
}
