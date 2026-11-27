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
        public LineageMsg Lineage;
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
                    case Msg.Lineage: Lineage = LineageMsg.Read(r); break;
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
                if (succeeded) Check(alaric.WaitFor(() => alaric.Self.Standing >= 2, 2), "successful mission grants standing");

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

                corvin.Conn.Send(SimpleMsg.Power(0, 0)); // Surge
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

                // --- Collision and pathfinding ---------------------------------------
                Console.WriteLine("[collision]");
                CityCollision col = server.Collision;
                CityDressing dressing = CityDressing.Create(server.City);
                DressedBuilding tower = dressing.Buildings.Find(b => b.Def.Style == BuildingStyle.Normal && (b.StreetFaces & 1) != 0);
                BuildingDef bd = tower.Def;
                Check(col.SolidCount > 150, "server knows the city's solid geometry (" + col.SolidCount + " obstacles)");
                Check(col.IsSolid(bd.CX, bd.CZ) && !col.IsSolid(server.City.Nodes[40].X, server.City.Nodes[40].Z), "buildings are solid, streets are clear");
                Check(!col.LineOfSight(bd.CX - bd.SX, bd.CZ, bd.CX + bd.SX, bd.CZ) && col.LineOfSight(-150, -200, 150, -200), "line of sight is blocked by buildings, open along streets");
                Check(col.Route(0, CityLayout.NodeIndex(8, 8)).Count == 17, "street route between opposite corners has 17 nodes");

                // Walk up to a building, then try to walk into it.
                float fx = bd.CX, fz = bd.CZ + bd.SZ * 0.5f;
                Func<float, string> Fmt = v => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                alaric.Say("/tp " + Fmt(fx) + " " + Fmt(fz + 0.9f));
                alaric.WaitFor(() => Math.Abs(alaric.Z - (fz + 0.9f)) < 0.05f, 2);
                Thread.Sleep(200);
                alaric.Conn.Send(SimpleMsg.Move(fx, fz - 0.6f, 180f));
                alaric.Chat.Clear();
                alaric.Say("/pos");
                Check(alaric.WaitFor(() => alaric.HasChat("pos " + Fmt(fx).TrimEnd('0').TrimEnd('.')), 2) && alaric.Chat.Exists(c => c.Contains("pos") && c.Contains(" " + (fz + 0.9f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))),
                      "moving into a building is rejected");
                Thread.Sleep(200);
                alaric.Conn.Send(SimpleMsg.Move(fx + 1.0f, fz + 0.9f, 90f));
                alaric.Chat.Clear();
                alaric.Say("/pos");
                Check(alaric.WaitFor(() => alaric.Chat.Exists(c => c.Contains("pos " + (fx + 1.0f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))), 2),
                      "moving along the sidewalk is accepted");

                // A hunter must close in without ever passing through a building.
                alaric.Say("/setexposure 65");
                bool spawned = alaric.WaitFor(() => alaric.Entities.Exists(e => e.Kind == EntityKind.Hunter), 6);
                Check(spawned, "exposure summons a hunter");
                bool insideWall = false;
                float firstDist = -1, lastDist = -1;
                DateTime until = DateTime.UtcNow.AddSeconds(6);
                while (spawned && DateTime.UtcNow < until)
                {
                    alaric.Pump();
                    foreach (EntityState e in alaric.Entities)
                    {
                        if (e.Kind != EntityKind.Hunter) continue;
                        if (col.IsSolid(e.X, e.Z, -0.2f)) insideWall = true;
                        float dd = Entity.Dist(e.X, e.Z, alaric.X, alaric.Z);
                        if (firstDist < 0) firstDist = dd;
                        lastDist = dd;
                    }
                    Thread.Sleep(50);
                }
                Check(spawned && !insideWall, "hunters never walk through buildings");
                Check(spawned && lastDist < firstDist - 5f, "the hunter closes in along the streets (" + firstDist.ToString("0") + " m -> " + lastDist.ToString("0") + " m)");
                alaric.Say("/setexposure 0");

                // --- Gifts -----------------------------------------------------------
                Console.WriteLine("[gifts]");
                alaric.Chat.Clear();
                alaric.Say("/learn 2");
                Check(alaric.WaitFor(() => alaric.HasChat("costs 5 standing"), 3), "learning a gift needs standing");
                alaric.Say("/setstanding 20");
                Thread.Sleep(300);
                alaric.Say("/learn 2");
                Check(alaric.WaitFor(() => alaric.HasNotice("teach you Siphon"), 3), "Alaric learns Siphon for 5 standing");
                Thread.Sleep(1100);
                alaric.Say("/learn crimson");
                Check(alaric.WaitFor(() => alaric.Self.LearnedGifts == 7 && alaric.Self.Standing == 3, 3),
                      "Alaric learns Crimson Mist; standing spent (" + alaric.Self.Standing + " left)");

                alaric.Say("/tp -100 4"); beatrix.Say("/tp -101 4"); corvin.Say("/tp -99 4");
                Thread.Sleep(1100);
                alaric.Say("/setblood 2"); corvin.Say("/setblood 6");
                Thread.Sleep(1100);
                alaric.Conn.Send(SimpleMsg.Power(1, corvin.Id));
                Check(alaric.WaitFor(() => alaric.Self.Blood >= 2.9f, 2) && corvin.WaitFor(() => corvin.Self.Blood <= 4.1f, 2),
                      "Siphon (1 blood) draws 2 blood from Corvin into Alaric");

                corvin.Say("/setstanding 10");
                Thread.Sleep(1100);
                corvin.Say("/learn 2");
                corvin.WaitFor(() => (corvin.Self.LearnedGifts & 2) != 0, 3);
                corvin.Conn.Send(SimpleMsg.Power(1, 0));
                Check(corvin.WaitFor(() => corvin.Flag(SelfFlags.IronSkin), 2), "Corvin hardens with Iron Skin");
                float before = corvin.Self.Health;
                beatrix.Conn.Send(SimpleMsg.Attack(corvin.Id));
                Check(corvin.WaitFor(() => corvin.Self.Health < before - 0.01f, 2) && Math.Abs(before - corvin.Self.Health - 0.5f) < 0.05f,
                      "Iron Skin halves damage (" + before.ToString("0.0") + " -> " + corvin.Self.Health.ToString("0.0") + ")");

                beatrix.Say("/setstanding 30");
                Thread.Sleep(1100);
                beatrix.Say("/learn 2");
                Thread.Sleep(1100);
                beatrix.Say("/learn 3");
                Check(beatrix.WaitFor(() => beatrix.Self.LearnedGifts == 7, 3), "Beatrix learns Majesty and Dominate");
                beatrix.Conn.Send(SimpleMsg.Power(2, corvin.Id));
                Check(corvin.WaitFor(() => corvin.Flag(SelfFlags.Silenced), 2) && !corvin.Flag(SelfFlags.Rooted),
                      "Dominate silences Corvin; Iron Skin shrugs off the root");
                corvin.Conn.Send(SimpleMsg.Power(0, 0));
                Check(corvin.WaitFor(() => corvin.HasNotice("silenced"), 2), "a silenced vampire cannot use gifts");

                beatrix.Conn.Send(SimpleMsg.Power(1, 0));
                Check(beatrix.WaitFor(() => beatrix.Flag(SelfFlags.Majesty), 2), "Beatrix radiates Majesty");
                Thread.Sleep(1100);
                corvin.Conn.Send(SimpleMsg.Attack(beatrix.Id));
                Check(corvin.WaitFor(() => corvin.HasNotice("cannot bring yourself"), 2), "Majesty stops a rival's attack");

                alaric.Say("/setblood 6");
                Thread.Sleep(400);
                alaric.Conn.Send(SimpleMsg.Power(2, 0));
                Check(alaric.WaitFor(() => alaric.Flag(SelfFlags.Mist), 2), "Alaric dissolves into Crimson Mist");
                Check(corvin.WaitFor(() => corvin.Entities.Exists(e => e.Id == alaric.Id && (e.Flags & EntityFlags.Mist) != 0), 2),
                      "other players see the mist");
                Thread.Sleep(1100);
                corvin.Conn.Send(SimpleMsg.Attack(alaric.Id));
                Check(corvin.WaitFor(() => corvin.HasNotice("passes through crimson mist"), 2), "mist cannot be struck");

                // --- Lineage -----------------------------------------------------------
                Console.WriteLine("[lineage]");
                var dorian = new Bot("Dorian"); dorian.Connect(port, "secret4", Bloodline.Sylphine);
                Check(dorian.WaitFor(() => dorian.Welcomed && dorian.Self != null && dorian.Lineage != null, 5) &&
                      dorian.Lineage.Status == LineageStatus.Fledgling && !dorian.Lineage.CanSire, "a new character is a fledgling");
                Check(alaric.WaitFor(() => alaric.Lineage != null && alaric.Lineage.CanSire && alaric.Lineage.Status == LineageStatus.Unclaimed, 3),
                      "a vampire who knows their second gift may sire");
                dorian.Say("/embrace Corvin");
                Check(dorian.WaitFor(() => dorian.HasChat("second gift may sire"), 3), "a fledgling cannot sire");
                alaric.Say("/setblood 10");
                Thread.Sleep(1100);
                alaric.Say("/embrace Dorian");
                Check(alaric.WaitFor(() => alaric.HasChat("Stand beside Dorian"), 3), "the Embrace needs the fledgling at your side");
                dorian.Say("/tp -101.5 4");
                Thread.Sleep(1100);
                alaric.Say("/embrace Dorian");
                Check(dorian.WaitFor(() => dorian.HasNotice("offers you the Embrace") && dorian.Lineage.OfferFrom == "Alaric" &&
                                           dorian.Lineage.OfferBloodline == Bloodline.Ashborn, 3), "Dorian is offered the Embrace");
                dorian.Say("/accept");
                Check(dorian.WaitFor(() => dorian.Self.Bloodline == Bloodline.Ashborn && dorian.Lineage.Sire.Name == "Alaric" &&
                                           dorian.Lineage.Status == LineageStatus.Tutelage && dorian.Self.LearnedGifts == 1, 3),
                      "accepting makes Dorian Alaric's childe, remade as Ashborn");
                Check(alaric.WaitFor(() => alaric.Lineage.Childer.Length == 1 && alaric.Lineage.Childer[0].Name == "Dorian" && alaric.Self.Blood <= 7.1f, 3),
                      "Alaric gains a childe and shares 3 blood (" + alaric.Self.Blood.ToString("0.0") + " left)");
                Check(corvin.WaitFor(() => corvin.Infos.ContainsKey(dorian.Id) && corvin.Infos[dorian.Id].Sire == "Alaric" &&
                                           corvin.Infos[dorian.Id].Bloodline == Bloodline.Ashborn, 3), "others see Dorian's new bloodline and sire");
                Check(dorian.WaitFor(() => dorian.Flag(SelfFlags.Bonded), 3) && alaric.WaitFor(() => alaric.Flag(SelfFlags.Bonded), 3),
                      "sire and childe side by side share the blood bond");
                Thread.Sleep(1100);
                dorian.Say("/lin Thank you, sire");
                Check(alaric.WaitFor(() => alaric.HasChat("Thank you, sire"), 3) && !corvin.WaitFor(() => corvin.HasChat("Thank you, sire"), 0.5),
                      "lineage chat reaches the family only");
                dorian.Conn.Send(SimpleMsg.Attack(alaric.Id));
                Check(dorian.WaitFor(() => dorian.HasNotice("against your sire"), 2), "a childe cannot strike their sire");
                beatrix.Say("/embrace Dorian");
                Check(beatrix.WaitFor(() => beatrix.HasChat("already has a sire"), 3), "a childe cannot be claimed twice");

                dorian.Say("/setstanding 3");
                Thread.Sleep(1100);
                alaric.WaitFor(() => false, 0.3);
                int teacherBefore = alaric.Self.Standing;
                dorian.Say("/learn 2");
                Check(dorian.WaitFor(() => (dorian.Self.LearnedGifts & 2) != 0 && dorian.Self.Standing == 0, 3),
                      "Alaric teaches Dorian Siphon for 3 standing instead of 5");
                Check(alaric.WaitFor(() => alaric.Self.Standing == teacherBefore + 1, 3), "the sire gains standing for teaching");
                Thread.Sleep(1100);
                dorian.Say("/embrace Corvin");
                Check(dorian.WaitFor(() => dorian.HasChat("still in your sire's tutelage"), 3), "a childe in tutelage cannot sire");

                // A childe's crime shames the sire.
                int shamedBefore = alaric.Self.Standing;
                bool killed = false;
                for (int attempt = 0; attempt < 4 && !killed; attempt++)
                {
                    Thread.Sleep(1100);
                    dorian.Say("/tp " + (-150 + attempt * 40) + " 0");
                    dorian.WaitFor(() => false, 0.5);
                    EntityState? prey = null;
                    foreach (var e in dorian.Entities)
                        if (e.Kind == EntityKind.Mortal && (e.Flags & (EntityFlags.Down | EntityFlags.Feeding)) == 0 &&
                            Entity.Dist(e.X, e.Z, dorian.X, dorian.Z) < 12f) { prey = e; break; }
                    if (prey == null) continue;
                    dorian.Conn.Send(SimpleMsg.Power(0, prey.Value.Id)); // Blood Lash: 3 damage kills a mortal
                    killed = alaric.WaitFor(() => alaric.HasNotice("has killed a mortal"), 1.5);
                }
                Check(killed && alaric.WaitFor(() => alaric.Self.Standing < shamedBefore, 2),
                      "the sire answers for the childe's kill (" + shamedBefore + " -> " + alaric.Self.Standing + " standing)");
                Thread.Sleep(1100);
                dorian.Say("/setexposure 0");

                Thread.Sleep(1100);
                alaric.Say("/lineage release Dorian");
                Check(alaric.WaitFor(() => alaric.HasChat("must know all three gifts"), 3), "a childe must know all three gifts to be released");
                dorian.Say("/setstanding 7");
                Thread.Sleep(1100);
                dorian.Say("/learn 3");
                Check(dorian.WaitFor(() => dorian.Self.LearnedGifts == 7 && dorian.Self.Standing == 0, 3), "Dorian learns Crimson Mist from his sire for 7");
                Thread.Sleep(1100);
                alaric.WaitFor(() => false, 0.3);
                int releaseBefore = alaric.Self.Standing;
                alaric.Say("/lineage release Dorian");
                Check(dorian.WaitFor(() => dorian.Lineage.Status == LineageStatus.Released && dorian.Lineage.CanSire, 3) &&
                      alaric.WaitFor(() => alaric.Self.Standing == releaseBefore + 3, 3), "Alaric releases Dorian (+3 standing); Dorian may now sire");

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
                Check(again.Self.LearnedGifts == 7, "learned gifts persisted");
                Check(again.WaitFor(() => again.Entities.Exists(e => e.Kind == EntityKind.Mortal), 3), "mortals repopulate after restart");
                Check(again.WaitFor(() => again.Lineage != null && again.Lineage.Childer.Length == 1 && again.Lineage.Childer[0].Released, 3),
                      "lineage persisted (Alaric's childe Dorian, released)");
                var dorian2 = new Bot("Dorian"); dorian2.Connect(port, "secret4", Bloodline.Vorlen);
                Check(dorian2.WaitFor(() => dorian2.Welcomed && dorian2.Self != null && dorian2.Lineage != null, 5) &&
                      dorian2.Self.Bloodline == Bloodline.Ashborn && dorian2.Lineage.Sire.Name == "Alaric", "Dorian is still Alaric's Ashborn childe after restart");
                dorian2.Say("/lineage renounce");
                Check(again.WaitFor(() => again.Lineage.Childer.Length == 0 && again.HasNotice("renounced you"), 3) &&
                      dorian2.WaitFor(() => dorian2.Lineage.Sire.Name == "" && dorian2.Lineage.Status == LineageStatus.Unclaimed, 3),
                      "Dorian renounces his sire");
                dorian2.Conn.Close("done");
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
