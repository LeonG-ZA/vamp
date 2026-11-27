using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Sanguine.Server
{
    /// <summary>
    /// Authoritative game server. Runs a fixed-rate simulation on its own thread.
    /// It has no UnityEngine dependency, so the same code runs as a standalone
    /// dedicated server and embedded inside the Unity client ("Host" mode).
    /// </summary>
    public sealed partial class GameServer
    {
        private readonly ServerConfig _cfg;
        private readonly Store _store;
        private readonly CityLayout _city;
        private readonly CityCollision _collision;
        private readonly Random _rand;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly ConcurrentQueue<Connection> _pendingConnections = new ConcurrentQueue<Connection>();

        private TcpListener _listener;
        private Thread _loopThread, _acceptThread;
        private volatile bool _running;

        private readonly List<Session> _sessions = new List<Session>();
        private readonly Dictionary<int, Entity> _entities = new Dictionary<int, Entity>();
        private readonly List<PlayerEntity> _players = new List<PlayerEntity>();
        private readonly List<Mortal> _mortals = new List<Mortal>();
        private readonly List<Hunter> _hunters = new List<Hunter>();
        private readonly Dictionary<string, PlayerProfile> _profiles;
        private readonly WorldSave _world;
        private readonly List<int> _scratchNodes = new List<int>();
        private readonly List<EntityState> _scratchStates = new List<EntityState>();

        private int _nextEntityId = 1;
        private int _nextSessionId = 1;
        private double _now;
        private double _cycleTime;
        private bool _wasDay;
        private bool _regentHuntUsed;
        private readonly Dictionary<string, string> _votes = new Dictionary<string, string>(); // voter key -> candidate key
        private double _nextSnapshot, _nextSelfState, _nextWorldState, _nextAutosave;

        public int Port { get; private set; }
        public bool IsRunning { get { return _running; } }
        public int OnlinePlayers { get { lock (_players) return _players.Count; } }
        public CityLayout City { get { return _city; } }
        public CityCollision Collision { get { return _collision; } }

        public GameServer(ServerConfig cfg)
        {
            _cfg = cfg;
            _rand = cfg.SimSeed != 0 ? new Random(cfg.SimSeed) : new Random();
            _city = new CityLayout(cfg.CitySeed);
            _collision = new CityCollision(_city, CityDressing.Create(_city));
            _store = new Store(cfg.SaveDirectory, cfg.Log);
            _profiles = _store.LoadAllProfiles();
            _world = _store.LoadWorld();
            BuildLineageIndex();
            Log("Loaded " + _profiles.Count + " profiles, " + _world.Circles.Count + " circles, night " + _world.Night + ".");
        }

        private void Log(string s) { if (_cfg.Log != null) _cfg.Log("[server] " + s); }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, _cfg.Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _clock.Start();
            SpawnInitialMortals();

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "server-accept" };
            _acceptThread.Start();
            _loopThread = new Thread(MainLoop) { IsBackground = true, Name = "server-loop" };
            _loopThread.Start();
            Log("Sanguine City server listening on port " + Port + " (city seed " + _cfg.CitySeed + ").");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener.Stop(); } catch { }
            if (_loopThread != null && Thread.CurrentThread != _loopThread) _loopThread.Join(3000);
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient tcp = _listener.AcceptTcpClient();
                    _pendingConnections.Enqueue(new Connection(tcp));
                }
                catch (Exception)
                {
                    if (!_running) return;
                    Thread.Sleep(50);
                }
            }
        }

        private void MainLoop()
        {
            double tickLen = 1.0 / _cfg.TickRate;
            double last = _clock.Elapsed.TotalSeconds;
            while (_running)
            {
                double t = _clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(0.25, t - last);
                last = t;
                try { Tick(t, dt); }
                catch (Exception e) { Log("Tick error: " + e); }
                double sleep = tickLen - (_clock.Elapsed.TotalSeconds - t);
                if (sleep > 0) Thread.Sleep((int)(sleep * 1000));
            }
            Shutdown();
        }

        private void Shutdown()
        {
            Log("Shutting down, saving state...");
            foreach (var s in _sessions.ToArray()) { if (s.Player != null) SavePlayer(s.Player); s.Conn.Close("server shutdown"); }
            _store.SaveWorld(_world);
        }

        // ------------------------------------------------------------------
        // Tick
        // ------------------------------------------------------------------

        private void Tick(double now, float dt)
        {
            _now = now;
            AcceptPending();
            ProcessIncoming();
            UpdateCycle(dt);
            UpdatePlayers(dt);
            UpdateMortals(dt);
            UpdateHunters(dt);
            UpdateThralls();

            if (now >= _nextSnapshot) { _nextSnapshot = now + 1.0 / _cfg.SnapshotRate; SendSnapshots(); }
            if (now >= _nextSelfState) { _nextSelfState = now + 0.2; foreach (var p in _players) SendSelfState(p); }
            if (now >= _nextWorldState)
            {
                _nextWorldState = now + 2.0;
                foreach (var p in _players) { SendWorldState(p); SendLineage(p); }
            }
            foreach (var p in _players) if (p.LineageDirty) SendLineage(p);
            if (now >= _nextAutosave)
            {
                _nextAutosave = now + _cfg.AutosaveSeconds;
                if (now > 1) { foreach (var p in _players) SavePlayer(p); _store.SaveWorld(_world); }
            }
        }

        private void AcceptPending()
        {
            Connection c;
            while (_pendingConnections.TryDequeue(out c))
            {
                _sessions.Add(new Session { Id = _nextSessionId++, Conn = c, ConnectedAt = _now });
                Log("Connection from " + c.RemoteAddress);
            }
        }

        private void ProcessIncoming()
        {
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                Session s = _sessions[i];
                byte[] data;
                int budget = 64;
                while (budget-- > 0 && s.Conn.TryReceive(out data))
                {
                    try { Handle(s, new PacketReader(data)); }
                    catch (Exception e)
                    {
                        Log("Bad packet from " + s.Conn.RemoteAddress + ": " + e.Message);
                        s.Conn.Close("protocol error");
                        break;
                    }
                }

                if (s.Player == null && !s.Conn.IsClosed && _now - s.ConnectedAt > 30) s.Conn.Close("login timeout");
                if (s.Conn.IsClosed)
                {
                    _sessions.RemoveAt(i);
                    if (s.Player != null) RemovePlayer(s.Player, s.Conn.CloseReason);
                }
            }
        }

        private void Handle(Session s, PacketReader r)
        {
            if (s.Player == null)
            {
                if (r.Type == Msg.Login) HandleLogin(s, LoginMsg.Read(r));
                else s.Conn.Close("expected login");
                return;
            }

            PlayerEntity p = s.Player;
            switch (r.Type)
            {
                case Msg.Move: HandleMove(p, r.Float(), r.Float(), r.Float()); break;
                case Msg.FeedStart: StartFeeding(p, r.Int()); break;
                case Msg.FeedStop: StopFeeding(p, true); break;
                case Msg.Attack: Attack(p, r.Int()); break;
                case Msg.Power: UsePower(p, r.Byte(), r.Int()); break;
                case Msg.Chat: HandleChat(p, (ChatChannel)r.Byte(), r.Str(240)); break;
                default: break;
            }
        }

        // ------------------------------------------------------------------
        // Login / logout
        // ------------------------------------------------------------------

        public static bool ValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3 || name.Length > 16) return false;
            foreach (char ch in name)
                if (!(char.IsLetterOrDigit(ch) && ch < 128) && ch != '_') return false;
            return true;
        }

        private void HandleLogin(Session s, LoginMsg m)
        {
            if (m.Version != GameRules.ProtocolVersion) { Reject(s, "Version mismatch: server " + GameRules.ProtocolVersion + ", client " + m.Version + "."); return; }
            if (!ValidName(m.Name)) { Reject(s, "Names must be 3-16 letters, digits or underscores."); return; }
            if (m.Password == null || m.Password.Length < 3) { Reject(s, "Password must be at least 3 characters."); return; }
            if (_players.Count >= _cfg.MaxPlayers) { Reject(s, "The city is full tonight."); return; }

            string key = Store.KeyOf(m.Name);
            foreach (var op in _players)
                if (op.Profile.Key == key) { Reject(s, "That character is already in the city."); return; }

            PlayerProfile prof;
            bool isNew = false;
            if (_profiles.TryGetValue(key, out prof))
            {
                if (!prof.CheckPassword(m.Password)) { Reject(s, "Wrong password for that character."); return; }
            }
            else
            {
                isNew = true;
                prof = new PlayerProfile { Name = m.Name, Bloodline = (Bloodline)Math.Min((int)m.Bloodline, GameData.Bloodlines.Length - 1) };
                prof.SetPassword(m.Password);
                var def0 = GameData.GetBloodline(prof.Bloodline);
                prof.Health = def0.MaxHealth;
                prof.Blood = 6f;
                float sx, sz;
                CityLayout.SpawnPoint(_city.HavenFor(prof.Bloodline), out sx, out sz);
                prof.X = sx; prof.Z = sz;
                _profiles[key] = prof;
                _store.SaveProfile(prof);
            }

            var def = GameData.GetBloodline(prof.Bloodline);
            var p = new PlayerEntity
            {
                Id = _nextEntityId++, Name = prof.Name, Session = s, Profile = prof, Def = def,
                MaxHealth = def.MaxHealth, Health = Clamp(prof.Health, 1f, def.MaxHealth),
                X = prof.X, Z = prof.Z, LastMoveTime = _now,
                NextBloodDecay = _now + GameRules.BloodDecaySeconds
            };
            if (Math.Abs(p.X) > CityLayout.MapHalf || Math.Abs(p.Z) > CityLayout.MapHalf || (p.X == 0 && p.Z == 0))
            {
                float sx, sz;
                CityLayout.SpawnPoint(_city.HavenFor(prof.Bloodline), out sx, out sz);
                p.X = sx; p.Z = sz;
            }
            // Drop stale circle membership.
            if (prof.Circle.Length > 0 && FindCircle(prof.Circle) == null) prof.Circle = "";

            s.Player = p;
            _entities[p.Id] = p;
            lock (_players) _players.Add(p);
            MarkLineageDirty();

            s.Send(new WelcomeMsg { EntityId = p.Id, Name = prof.Name, Bloodline = prof.Bloodline, CitySeed = _cfg.CitySeed, X = p.X, Z = p.Z, NewCharacter = isNew }.Write());
            SendSelfState(p);
            SendWorldState(p);
            Log(prof.Name + (isNew ? " was embraced (new " : " rises (") + def.Name + ").");

            if (isNew)
            {
                Notice(p, NoticeLevel.Info, "Welcome to Sanguine City, " + def.Name + ". Hunt, but keep the Hush.");
                SystemMsg(p, "You are of the " + def.Name + ", " + def.Epithet + ". Your gift: " + def.Gifts[0].Name + " - " + def.Gifts[0].Description);
                SystemMsg(p, "Two more gifts can be learned with standing: " + def.Gifts[1].Name + " (" + def.Gifts[1].StandingCost + ") and " +
                             def.Gifts[2].Name + " (" + def.Gifts[2].StandingCost + "). See /gifts.");
                SystemMsg(p, "You are a fledgling. Until you learn your second gift, an elder may offer you the Embrace and become your sire (/lineage).");
                SystemMsg(p, "Type /help for commands. Feed (F) on mortals away from witnesses. Return to your haven before dawn.");
            }
            else Notice(p, NoticeLevel.Info, "You rise again, " + prof.Name + ".");
            if (_world.Edict.Length > 0) SystemMsg(p, "Edict of the Regent: " + _world.Edict);
            BroadcastSystem(prof.Name + " of the " + def.Name + " stirs in the city.", p);
        }

        private void Reject(Session s, string reason)
        {
            s.Send(SimpleMsg.LoginFailed(reason));
            Log("Login rejected from " + s.Conn.RemoteAddress + ": " + reason);
            // Leave the socket open briefly so the message is flushed; the login timeout closes it.
        }

        private void RemovePlayer(PlayerEntity p, string reason)
        {
            StopFeeding(p, false);
            SavePlayer(p);
            _entities.Remove(p.Id);
            lock (_players) _players.Remove(p);
            MarkLineageDirty();
            foreach (var h in _hunters) if (h.TargetPlayer == p.Id) h.TargetPlayer = 0;
            foreach (var o in _players) o.Known.Remove(p.Id);
            Log(p.Name + " left (" + reason + ").");
            BroadcastSystem(p.Name + " has gone to ground.", null);
        }

        private void SavePlayer(PlayerEntity p)
        {
            p.Profile.X = p.X;
            p.Profile.Z = p.Z;
            p.Profile.Health = p.InTorpor ? p.MaxHealth : p.Health;
            _store.SaveProfile(p.Profile);
        }

        // ------------------------------------------------------------------
        // Movement
        // ------------------------------------------------------------------

        public float SpeedMultiplier(PlayerEntity p)
        {
            float m = 1f;
            if (_now < p.SurgeUntil) m *= GameRules.SurgeSpeedMultiplier;
            if (_now < p.MistUntil) m *= 1.3f;
            if (_now < p.SlowUntil) m *= GameRules.SlowSpeedMultiplier;
            return m;
        }

        private void HandleMove(PlayerEntity p, float x, float z, float rot)
        {
            double elapsed = _now - p.LastMoveTime;
            p.LastMoveTime = _now;
            p.Rot = rot;
            float d = Entity.Dist(p.X, p.Z, x, z);
            if (d < 0.001f) return;

            bool frozen = p.InTorpor || _now < p.RootUntil || p.IsStunned(_now);
            if (p.FeedTarget != 0)
            {
                // Moving away breaks the feeding embrace.
                if (Entity.Dist(p.FeedStartX, p.FeedStartZ, x, z) > 1.0f) StopFeeding(p, true);
            }

            float allowed = (float)(GameRules.WalkSpeed * SpeedMultiplier(p) * 1.35 * Math.Min(elapsed + 0.1, 1.0)) + 0.3f;
            // Reject speed hacks and walking through walls (solids shrunk slightly: the client's capsule
            // already keeps the player ~0.4 m from walls, so honest clients never trip this).
            if (frozen || d > allowed || _collision.IsSolid(x, z, -0.15f))
            {
                p.Session.Send(SimpleMsg.Teleport(p.X, p.Z));
                return;
            }
            p.X = Clamp(x, -CityLayout.MapHalf, CityLayout.MapHalf);
            p.Z = Clamp(z, -CityLayout.MapHalf, CityLayout.MapHalf);
        }

        private void TeleportPlayer(PlayerEntity p, float x, float z)
        {
            p.X = x; p.Z = z;
            p.Session.Send(SimpleMsg.Teleport(x, z));
        }

        // ------------------------------------------------------------------
        // Networking out
        // ------------------------------------------------------------------

        private void SendSnapshots()
        {
            foreach (var p in _players)
            {
                _scratchStates.Clear();
                foreach (var e in _entities.Values)
                {
                    if (e != p && Entity.Dist(e.X, e.Z, p.X, p.Z) > GameRules.InterestRadius) continue;
                    if (!p.Known.Contains(e.Id))
                    {
                        p.Known.Add(e.Id);
                        p.Session.Send(InfoFor(e).Write());
                    }
                    _scratchStates.Add(StateOf(e));
                }
                p.Session.Send(SnapshotMsg.Write((float)_now, _scratchStates));
            }
        }

        private EntityInfoMsg InfoFor(Entity e)
        {
            var info = new EntityInfoMsg { Id = e.Id, Kind = e.Kind, Name = e.Name, Circle = "", Sire = "" };
            var pe = e as PlayerEntity;
            if (pe != null) { info.Bloodline = pe.Profile.Bloodline; info.Circle = pe.Profile.Circle; info.Sire = NameOf(pe.Profile.Sire); }
            return info;
        }

        private void InvalidateInfo(int entityId)
        {
            foreach (var p in _players) p.Known.Remove(entityId);
        }

        private EntityState StateOf(Entity e)
        {
            var s = new EntityState { Id = e.Id, Kind = e.Kind, X = e.X, Z = e.Z, Rot = e.Rot };
            s.HealthPct = (byte)Clamp(e.MaxHealth > 0 ? e.Health / e.MaxHealth * 255f : 0f, 0f, 255f);
            EntityFlags f = EntityFlags.None;
            if (e.IsDown) f |= EntityFlags.Down;
            if (e.IsStunned(_now)) f |= EntityFlags.Stunned;
            var p = e as PlayerEntity;
            if (p != null)
            {
                if (p.FeedTarget != 0) f |= EntityFlags.Feeding;
                if (IsMarked(p)) f |= EntityFlags.Marked;
                if (_now < p.SurgeUntil) f |= EntityFlags.Surging;
                if (_now < p.RootUntil) f |= EntityFlags.Stunned;
                if (IsRegent(p)) f |= EntityFlags.Regent;
                if (_now < p.MistUntil) f |= EntityFlags.Mist;
                if (_now < p.IronSkinUntil) f |= EntityFlags.IronSkin;
                if (_now < p.MajestyUntil) f |= EntityFlags.Majesty;
            }
            var m = e as Mortal;
            if (m != null)
            {
                if (m.Mode == MortalMode.Fleeing) f |= EntityFlags.Alarmed;
                if (m.Mode == MortalMode.Charmed) f |= EntityFlags.Charmed;
                if (m.FedUponBy != 0) f |= EntityFlags.Feeding;
            }
            s.Flags = f;
            return s;
        }

        private void SendSelfState(PlayerEntity p)
        {
            var m = new SelfStateMsg
            {
                Blood = p.Blood, MaxBlood = p.MaxBlood, Health = p.Health, MaxHealth = p.MaxHealth,
                Conscience = p.Conscience, Exposure = p.Exposure, Standing = p.Standing,
                AttackCooldown = (float)Math.Max(0, p.AttackReadyAt - _now),
                FeedTarget = p.FeedTarget, District = (byte)GameData.DistrictAt(p.X, p.Z)
            };
            SelfFlags f = SelfFlags.None;
            if (IsMarked(p)) f |= SelfFlags.Marked;
            if (_now < p.OutlawUntil) f |= SelfFlags.Outlaw;
            if (_now < p.BloodHuntUntil) f |= SelfFlags.BloodHunt;
            ZoneDef z = _city.ZoneAt(p.X, p.Z);
            if (z != null && z.Kind == ZoneKind.Haven) f |= SelfFlags.InHaven;
            if (z != null && z.Kind == ZoneKind.Sanctum) f |= SelfFlags.InSanctum;
            if (IsDay && z == null) f |= SelfFlags.Sunlight;
            if (_now < p.SurgeUntil) f |= SelfFlags.Surge;
            if (_now < p.SlowUntil) f |= SelfFlags.Slowed;
            if (_now < p.RootUntil || p.IsStunned(_now)) f |= SelfFlags.Rooted;
            if (p.InTorpor) f |= SelfFlags.Torpor;
            if (p.FeedTarget != 0) f |= SelfFlags.Feeding;
            if (p.Conscience <= GameRules.ConscienceBeastThreshold) f |= SelfFlags.Beast;
            if (p.Blood <= 0f) f |= SelfFlags.Starving;
            if (IsRegent(p)) f |= SelfFlags.Regent;
            if (_now < p.IronSkinUntil) f |= SelfFlags.IronSkin;
            if (_now < p.GlamourUntil) f |= SelfFlags.Glamour;
            if (_now < p.MistUntil) f |= SelfFlags.Mist;
            if (_now < p.MajestyUntil) f |= SelfFlags.Majesty;
            if (_now < p.SilencedUntil) f |= SelfFlags.Silenced;
            if (p.Bonded) f |= SelfFlags.Bonded;
            m.Flags = f;
            m.Bloodline = p.Profile.Bloodline;
            m.LearnedGifts = (byte)(p.Profile.Gifts | 1);
            for (int i = 0; i < GameRules.GiftSlots; i++) m.GiftCooldowns[i] = (float)Math.Max(0, p.GiftReadyAt[i] - _now);
            for (int i = 0; i < p.Thralls.Length; i++)
            {
                m.Thralls[i] = new ThrallInfo
                {
                    State = p.Thralls[i].State, District = (byte)p.Thralls[i].District,
                    Remaining = (float)Math.Max(0, p.Thralls[i].Until - _now)
                };
            }
            p.Session.Send(m.Write());
        }

        private void SendWorldState(PlayerEntity p)
        {
            var m = new WorldStateMsg
            {
                CyclePhase = (float)(_cycleTime / CycleLength),
                IsDay = IsDay,
                SecondsToChange = (float)(IsDay ? CycleLength - _cycleTime : _cfg.NightSeconds - _cycleTime),
                Night = _world.Night, Regent = _world.Regent, Edict = _world.Edict,
                MyCircle = p.Profile.Circle
            };
            string vote;
            if (_votes.TryGetValue(p.Profile.Key, out vote))
            {
                PlayerProfile cand;
                m.MyVote = _profiles.TryGetValue(vote, out cand) ? cand.Name : vote;
            }
            for (int d = 0; d < 4; d++) m.DistrictControllers[d] = DistrictController(d);
            m.TopCandidates = TopCandidates(3);
            p.Session.Send(m.Write());
        }

        private void Notice(PlayerEntity p, NoticeLevel level, string text) { p.Session.Send(SimpleMsg.Notice(level, text)); }

        private void SystemMsg(PlayerEntity p, string text) { p.Session.Send(SimpleMsg.ChatMsg(ChatChannel.System, "", text)); }

        private void BroadcastSystem(string text, PlayerEntity except)
        {
            byte[] data = SimpleMsg.ChatMsg(ChatChannel.System, "", text);
            foreach (var p in _players) if (p != except) p.Session.Send(data);
        }

        private void BroadcastNotice(NoticeLevel level, string text)
        {
            byte[] data = SimpleMsg.Notice(level, text);
            foreach (var p in _players) p.Session.Send(data);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static float Clamp(float v, float min, float max) { return v < min ? min : (v > max ? max : v); }

        private float RandF() { return (float)_rand.NextDouble(); }

        private T Get<T>(int id) where T : Entity
        {
            Entity e;
            return _entities.TryGetValue(id, out e) ? e as T : null;
        }

        private PlayerEntity FindOnline(string name)
        {
            string key = Store.KeyOf(name);
            foreach (var p in _players) if (p.Profile.Key == key) return p;
            return null;
        }
    }
}
