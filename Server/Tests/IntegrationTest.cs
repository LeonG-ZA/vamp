using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sanguine;
using Sanguine.Server;

namespace Sanguine.Tests
{
    /// <summary>A scripted headless client used to exercise the server over real TCP.</summary>
    public sealed class Bot
    {
        public readonly string Name;
        public Connection Conn;
        public int Id;
        public bool Welcomed, NewCharacter;
        public string LoginError;
        public SelfStateMsg Self;
        public WorldStateMsg World;
        public readonly List<EntityState> Entities = new List<EntityState>();
        public readonly Dictionary<int, EntityInfoMsg> Infos = new Dictionary<int, EntityInfoMsg>();
        public readonly List<string> Notices = new List<string>();
        public readonly List<string> Chat = new List<string>();
        public float X, Z;

        public Bot(string name) { Name = name; }

        public void Connect(int port, string password, Bloodline b)
        {
            Conn = Connection.Connect("127.0.0.1", port);
            Conn.Send(new LoginMsg { Version = GameRules.ProtocolVersion, Name = Name, Password = password, Bloodline = b }.Write());
        }

        public void Pump()
        {
            byte[] data;
            while (Conn.TryReceive(out data))
            {
                var r = new PacketReader(data);
                switch (r.Type)
                {
                    case Msg.Welcome:
                        var w = WelcomeMsg.Read(r);
                        Id = w.EntityId; Welcomed = true; NewCharacter = w.NewCharacter; X = w.X; Z = w.Z;
                        break;
                    case Msg.LoginFailed: LoginError = r.Str(); break;
                    case Msg.SelfState: Self = SelfStateMsg.Read(r); break;
                    case Msg.WorldState: World = WorldStateMsg.Read(r); break;
                    case Msg.Snapshot: SnapshotMsg.Read(r, Entities); break;
                    case Msg.EntityInfo: var info = EntityInfoMsg.Read(r); Infos[info.Id] = info; break;
                    case Msg.Notice: r.Byte(); Notices.Add(r.Str()); break;
                    case Msg.ChatMsg: r.Byte(); string from = r.Str(); Chat.Add((from.Length > 0 ? from + ": " : "") + r.Str()); break;
                    case Msg.Teleport: X = r.Float(); Z = r.Float(); break;
                }
            }
        }

        public bool WaitFor(Func<bool> cond, double seconds)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                Pump();
                if (cond()) return true;
                Thread.Sleep(20);
            }
            Pump();
            return cond();
        }

        public void Say(string text) { Conn.Send(SimpleMsg.Chat(ChatChannel.Local, text)); }

        public bool HasNotice(string fragment)
        {
            foreach (var n in Notices) if (n.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public bool HasChat(string fragment)
        {
            foreach (var n in Chat) if (n.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public bool Flag(SelfFlags f) { return Self != null && (Self.Flags & f) != 0; }
    }

    public static class IntegrationTest
    {
        private static int _failures, _passes;

        private static void Check(bool ok, string what)
        {
            if (ok) { _passes++; Console.WriteLine("  PASS  " + what); }
            else { _failures++; Console.WriteLine("  FAIL  " + what); }
        }

        private static GameServer StartServer(string saveDir)
        {
            var cfg = new ServerConfig
            {
                Port = 0, SaveDirectory = saveDir, DebugCommands = true, SimSeed = 42,
                NightSeconds = 600, DaySeconds = 3, ThrallMissionSeconds = 1, MortalsPerDistrict = 20,
                Log = s => { if (Environment.GetEnvironmentVariable("SANGUINE_VERBOSE") != null) Console.WriteLine(s); }
            };
            var server = new GameServer(cfg);
            server.Start();
            return server;
        }

        public static int Main()
        {
            string saveDir = Path.Combine(Path.GetTempPath(), "sanguine-test-" + Guid.NewGuid().ToString("N"));
            Console.WriteLine("Sanguine City integration test (save dir " + saveDir + ")");
            var server = StartServer(saveDir);
            int port = server.Port;

            try
            {
                // --- Login ---------------------------------------------------
                Console.WriteLine("[login]");
                var alaric = new Bot("Alaric"); alaric.Connect(port, "secret1", Bloodline.Ashborn);
                var beatrix = new Bot("Beatrix"); beatrix.Connect(port, "secret2", Bloodline.Aurum);
                var corvin = new Bot("Corvin"); corvin.Connect(port, "secret3", Bloodline.Vorlen);
                foreach (var b in new[] { alaric, beatrix, corvin })
                    Check(b.WaitFor(() => b.Welcomed && b.Self != null && b.World != null, 5) && b.NewCharacter, b.Name + " logs in as a new character");

                var dup = new Bot("alaric"); dup.Connect(port, "secret1", Bloodline.Ashborn);
                Check(dup.WaitFor(() => dup.LoginError != null, 3), "duplicate login is rejected (" + dup.LoginError + ")");
                dup.Conn.Close("done");

                var haven = server.City.HavenFor(Bloodline.Ashborn);
                Check(haven.Contains(alaric.X, alaric.Z), "new character spawns in their bloodline haven");
                Check(alaric.WaitFor(() => alaric.Entities.Count > 1, 3), "snapshots include nearby entities (" + alaric.Entities.Count + ")");

                // --- Circles ---------------------------------------------------
                Console.WriteLine("[circles]");
                alaric.Say("/circle create Nightjar Court");
                Check(alaric.WaitFor(() => alaric.World.MyCircle == "Nightjar Court", 4), "Alaric founds circle 'Nightjar Court'");
                alaric.Say("/circle invite Beatrix");
                Check(beatrix.WaitFor(() => beatrix.HasNotice("invites you"), 3), "Beatrix receives the invite");
                beatrix.Say("/circle join Nightjar Court");
                Check(beatrix.WaitFor(() => beatrix.World.MyCircle == "Nightjar Court", 4), "Beatrix joins the circle");
                corvin.Say("/circle join Nightjar Court");
                Check(corvin.WaitFor(() => corvin.HasChat("not been invited"), 3), "uninvited join is refused");

                // --- Feeding ---------------------------------------------------
                Console.WriteLine("[feeding]");
                alaric.Say("/setblood 2");
                Check(alaric.WaitFor(() => alaric.Self.Blood <= 2.01f, 2), "debug: blood set to 2");
                bool fed = false;
                for (int attempt = 0; attempt < 5 && !fed; attempt++)
                {
                    // Jump to a calm mortal somewhere in the city.
                    alaric.Say("/tp " + (-150 + attempt * 50) + " 0");
                    alaric.WaitFor(() => false, 0.4);
                    EntityState? target = null;
                    float best = 999;
                    foreach (var e in alaric.Entities)
                    {
                        if (e.Kind != EntityKind.Mortal || (e.Flags & (EntityFlags.Alarmed | EntityFlags.Down | EntityFlags.Feeding)) != 0) continue;
                        float d = Entity.Dist(e.X, e.Z, alaric.X, alaric.Z);
                        if (d < best) { best = d; target = e; }
                    }
                    if (target == null) continue;
                    alaric.Say("/tp " + (target.Value.X + 0.5f).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + target.Value.Z.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    alaric.WaitFor(() => false, 0.15);
                    alaric.Conn.Send(SimpleMsg.FeedStart(target.Value.Id));
                    fed = alaric.WaitFor(() => alaric.Flag(SelfFlags.Feeding), 1.5);
                }
                Check(fed, "Alaric starts feeding on a mortal");
                Check(alaric.WaitFor(() => alaric.Self.Blood >= 4.9f, 5), "blood rises while feeding (" + alaric.Self.Blood.ToString("0.0") + ")");
                alaric.Conn.Send(SimpleMsg.FeedStop());
                Check(alaric.WaitFor(() => !alaric.Flag(SelfFlags.Feeding), 2), "feeding stops on release");
                Check(alaric.WaitFor(() => alaric.HasNotice("restraint"), 2), "restraint is rewarded with conscience");
                alaric.Say("/setexposure 0");

                // --- Thralls ---------------------------------------------------
                Console.WriteLine("[thralls]");
                alaric.Say("/thrall 3");
                Check(alaric.WaitFor(() => alaric.Self.Thralls[0].State == ThrallState.OnMission, 2), "thrall sent to Old Quarter");
                Check(alaric.WaitFor(() => alaric.HasNotice("thrall returns") || alaric.HasNotice("thrall was caught"), 4), "thrall mission resolves");
                bool succeeded = alaric.HasNotice("thrall returns");
                Console.WriteLine("        (mission " + (succeeded ? "succeeded" : "failed") + ")");
                if (succeeded) Check(alaric.Self.Standing >= 2, "successful mission grants standing");

                // --- Election ---------------------------------------------------
                Console.WriteLine("[politics]");
                alaric.Say("/vote Alaric");
                beatrix.Say("/vote Alaric");
                corvin.Say("/vote Corvin");
                Check(alaric.WaitFor(() => alaric.World.TopCandidates.Length == 2, 4), "vote tally visible (" + string.Join(", ", alaric.World.TopCandidates) + ")");
                alaric.Say("/dawn");
                Check(alaric.WaitFor(() => alaric.World.Regent == "Alaric", 5), "Alaric elected Regent at dawn");
                Check(alaric.HasNotice("Dawn breaks"), "dawn warning broadcast");
                Check(corvin.WaitFor(() => !corvin.World.IsDay && corvin.World.Night == 2, 8), "night 2 falls after the short test day");
                Check(alaric.Flag(SelfFlags.Regent), "Regent flag set on self state");

                alaric.Say("/hunt Corvin");
                Check(corvin.WaitFor(() => corvin.Flag(SelfFlags.BloodHunt) && corvin.Flag(SelfFlags.Marked), 3), "blood hunt marks Corvin");
                beatrix.Say("/hunt Alaric");
                Check(beatrix.WaitFor(() => beatrix.HasChat("Only the Regent"), 3), "non-regent cannot call a blood hunt");

                // --- PvP ---------------------------------------------------------
                Console.WriteLine("[pvp]");
                alaric.Say("/tp -100 0");
                beatrix.Say("/tp -101 0");
                corvin.Say("/tp -99 0");
                alaric.Say("/setexposure 0");
                corvin.WaitFor(() => false, 0.5);
                beatrix.Conn.Send(SimpleMsg.Attack(alaric.Id));
                Check(beatrix.WaitFor(() => beatrix.HasNotice("of your circle"), 3), "circle members cannot harm each other");

                corvin.Conn.Send(SimpleMsg.Power(0)); // Surge
                Check(corvin.WaitFor(() => corvin.Flag(SelfFlags.Surge), 2), "Corvin activates Surge");
                for (int i = 0; i < 12 && !alaric.Flag(SelfFlags.Torpor); i++)
                {
                    corvin.Conn.Send(SimpleMsg.Attack(alaric.Id));
                    alaric.WaitFor(() => alaric.Flag(SelfFlags.Torpor), 1.05);
                }
                Check(alaric.Flag(SelfFlags.Torpor), "Alaric falls into torpor");
                Check(corvin.WaitFor(() => corvin.Flag(SelfFlags.Outlaw), 2), "killing an unmarked vampire makes Corvin an outlaw");
                Check(alaric.WaitFor(() => !alaric.Flag(SelfFlags.Torpor), 13) && haven.Contains(alaric.X, alaric.Z), "Alaric awakens in his haven");

                // --- Sanctum -------------------------------------------------------
                Console.WriteLine("[sanctum]");
                var opera = server.City.Zones.Find(z => z.Kind == ZoneKind.Sanctum);
                corvin.Say("/tp " + opera.CenterX + " " + (opera.CenterZ - 12));
                beatrix.Say("/tp " + (opera.CenterX + 1) + " " + (opera.CenterZ - 12));
                beatrix.WaitFor(() => beatrix.Flag(SelfFlags.InSanctum), 2);
                beatrix.Conn.Send(SimpleMsg.Attack(corvin.Id));
                Check(beatrix.WaitFor(() => beatrix.HasNotice("forbidden in a sanctum"), 3), "violence is blocked inside a sanctum");

                // --- Persistence -----------------------------------------------------
                Console.WriteLine("[persistence]");
                int standing = alaric.Self.Standing;
                alaric.Conn.Close("logout");
                Thread.Sleep(300);
                var wrong = new Bot("Alaric"); wrong.Connect(port, "nope", Bloodline.Vorlen);
                Check(wrong.WaitFor(() => wrong.LoginError != null, 3), "wrong password rejected");
                wrong.Conn.Close("done");

                server.Stop();
                Console.WriteLine("        (server restarted)");
                server = StartServer(saveDir);
                port = server.Port;

                var again = new Bot("Alaric"); again.Connect(port, "secret1", Bloodline.Vorlen);
                Check(again.WaitFor(() => again.Welcomed && again.World != null && again.Self != null, 5) && !again.NewCharacter, "Alaric logs back in after restart");
                Check(again.Self.Standing == standing, "standing persisted (" + again.Self.Standing + ")");
                Check(again.World.MyCircle == "Nightjar Court", "circle membership persisted");
                Check(again.World.Regent == "Alaric", "Regent persisted");
                Check(again.WaitFor(() => again.Entities.Exists(e => e.Kind == EntityKind.Mortal), 3), "mortals repopulate after restart");
                again.Conn.Close("done");
                beatrix.Conn.Close("done");
                corvin.Conn.Close("done");
            }
            catch (Exception e)
            {
                _failures++;
                Console.WriteLine("  FAIL  exception: " + e);
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(saveDir, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(_passes + " passed, " + _failures + " failed");
            return _failures == 0 ? 0 : 1;
        }
    }
}
