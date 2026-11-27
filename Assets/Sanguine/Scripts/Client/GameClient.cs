using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sanguine.Server;
using UnityEngine;

namespace Sanguine.Client
{
    public enum ClientState { Menu, Connecting, InGame }

    public struct ChatLine { public ChatChannel Channel; public string From, Text; }
    public struct NoticeLine { public NoticeLevel Level; public string Text; public float Time; }

    /// <summary>
    /// Owns the connection to the server, the replicated world state and player input for actions.
    /// Can also host an embedded server (the same code as the dedicated server) for "Host &amp; Play".
    /// </summary>
    public sealed class GameClient : MonoBehaviour
    {
        public ClientState State = ClientState.Menu;
        public string StatusText = "";

        // Login settings (persisted in PlayerPrefs by the UI).
        public string Host = "127.0.0.1";
        public int Port = GameRules.DefaultPort;
        public string PlayerName = "";
        public string Password = "";
        public Bloodline ChosenBloodline = Bloodline.Vorlen;

        // Replicated state.
        public int MyId;
        public string MyName = "";
        public Bloodline MyBloodline;
        public SelfStateMsg Self;
        public WorldStateMsg World;
        public CityLayout City;
        public LocalPlayer Local;
        public readonly Dictionary<int, EntityView> Views = new Dictionary<int, EntityView>();
        public readonly Dictionary<int, EntityInfoMsg> Infos = new Dictionary<int, EntityInfoMsg>();
        public readonly List<ChatLine> Chat = new List<ChatLine>();
        public readonly List<NoticeLine> Notices = new List<NoticeLine>();
        public int TargetId;

        // UI flags (set by ClientUI).
        public bool ChatOpen;
        public bool UiCapturesMouse;
        public bool ThrallPanelOpen;
        public bool HelpOpen;
        public bool GiftsPanelOpen;

        public GameServer HostedServer { get; private set; }

        private Connection _conn;
        private Thread _connectThread;
        private volatile Connection _pendingConn;
        private volatile string _pendingError;
        private GameObject _cityRoot, _entityRoot;
        private readonly List<EntityState> _snapshot = new List<EntityState>();
        private readonly List<int> _toRemove = new List<int>();
        private GameObject _targetRing;

        private void Awake()
        {
            Application.runInBackground = true;
            gameObject.AddComponent<ClientUI>().Client = this;
            gameObject.AddComponent<Atmosphere>().Client = this;
            gameObject.AddComponent<AudioDirector>().Client = this;
            if (Camera.main == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                Camera cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
                cam.transform.position = new Vector3(-60, 28, -150);
                cam.transform.rotation = Quaternion.Euler(12, 20, 0);
            }
            PostFx.Setup(Camera.main);
            // Show the city behind the menu.
            BuildCity(1931);
        }

        // ------------------------------------------------------------------
        // Connecting
        // ------------------------------------------------------------------

        public void Connect(bool hostServer)
        {
            if (State != ClientState.Menu) return;
            if (!GameServer.ValidName(PlayerName)) { StatusText = "Name must be 3-16 letters, digits or underscores."; return; }
            if (Password.Length < 3) { StatusText = "Password must be at least 3 characters."; return; }

            string host = Host;
            if (hostServer)
            {
                try
                {
                    StartHostedServer();
                    host = "127.0.0.1";
                }
                catch (Exception e)
                {
                    StatusText = "Could not start server: " + e.Message;
                    return;
                }
            }

            State = ClientState.Connecting;
            StatusText = "Connecting to " + host + ":" + Port + "...";
            _pendingConn = null;
            _pendingError = null;
            int port = Port;
            _connectThread = new Thread(() =>
            {
                try { _pendingConn = Connection.Connect(host, port); }
                catch (Exception e) { _pendingError = e.Message; }
            }) { IsBackground = true };
            _connectThread.Start();
        }

        private void StartHostedServer()
        {
            if (HostedServer != null && HostedServer.IsRunning) return;
            var cfg = new ServerConfig
            {
                Port = Port,
                SaveDirectory = Path.Combine(Application.persistentDataPath, "server-save"),
                Log = s => Debug.Log(s)
            };
            HostedServer = new GameServer(cfg);
            HostedServer.Start();
        }

        public void Disconnect(string reason)
        {
            if (_conn != null) { _conn.Close(reason); _conn = null; }
            if (Local != null) { Destroy(Local.gameObject); Local = null; }
            foreach (var v in Views.Values) if (v != null) Destroy(v.gameObject);
            Views.Clear();
            Infos.Clear();
            Self = null;
            World = null;
            TargetId = 0;
            ChatOpen = false;
            State = ClientState.Menu;
            StatusText = reason;
        }

        public void Send(byte[] data)
        {
            if (_conn != null) _conn.Send(data);
        }

        private void OnApplicationQuit()
        {
            if (_conn != null) _conn.Close("quit");
            if (HostedServer != null) HostedServer.Stop();
        }

        // ------------------------------------------------------------------
        // Update loop
        // ------------------------------------------------------------------

        private void Update()
        {
            if (State == ClientState.Connecting)
            {
                if (_pendingError != null) { Disconnect("Connection failed: " + _pendingError); _pendingError = null; return; }
                if (_pendingConn != null)
                {
                    _conn = _pendingConn;
                    _pendingConn = null;
                    StatusText = "Logging in...";
                    _conn.Send(new LoginMsg { Version = GameRules.ProtocolVersion, Name = PlayerName, Password = Password, Bloodline = ChosenBloodline }.Write());
                }
            }

            if (_conn != null)
            {
                byte[] data;
                int budget = 512;
                while (budget-- > 0 && _conn != null && _conn.TryReceive(out data))
                {
                    try { Handle(new PacketReader(data)); }
                    catch (Exception e) { Debug.LogException(e); }
                }
                if (_conn != null && _conn.IsClosed) Disconnect("Disconnected: " + _conn.CloseReason);
            }

            if (State == ClientState.InGame) HandleActionInput();
            UpdateTargetRing();
            if (Notices.Count > 0 && Time.time - Notices[0].Time > 7f) Notices.RemoveAt(0);
        }

        private void Handle(PacketReader r)
        {
            switch (r.Type)
            {
                case Msg.Welcome: OnWelcome(WelcomeMsg.Read(r)); break;
                case Msg.LoginFailed: Disconnect("Login failed: " + r.Str()); break;
                case Msg.SelfState: Self = SelfStateMsg.Read(r); break;
                case Msg.WorldState: World = WorldStateMsg.Read(r); break;
                case Msg.Snapshot: OnSnapshot(r); break;
                case Msg.EntityInfo:
                {
                    EntityInfoMsg info = EntityInfoMsg.Read(r);
                    Infos[info.Id] = info;
                    EntityView v;
                    if (Views.TryGetValue(info.Id, out v)) v.ApplyInfo(info);
                    break;
                }
                case Msg.ChatMsg:
                {
                    var line = new ChatLine { Channel = (ChatChannel)r.Byte(), From = r.Str(), Text = r.Str() };
                    Chat.Add(line);
                    if (Chat.Count > 100) Chat.RemoveAt(0);
                    break;
                }
                case Msg.Notice:
                {
                    var n = new NoticeLine { Level = (NoticeLevel)r.Byte(), Text = r.Str(), Time = Time.time };
                    Notices.Add(n);
                    if (AudioDirector.Instance != null) AudioDirector.Instance.OnNotice(n.Level);
                    if (Notices.Count > 5) Notices.RemoveAt(0);
                    break;
                }
                case Msg.Teleport:
                {
                    float x = r.Float(), z = r.Float();
                    if (Local != null) Local.Teleport(new Vector3(x, CityDressing.GroundHeight(x, z) + 0.05f, z));
                    break;
                }
            }
        }

        private void OnWelcome(WelcomeMsg w)
        {
            MyId = w.EntityId;
            MyName = w.Name;
            MyBloodline = w.Bloodline;
            BuildCity(w.CitySeed);
            if (_entityRoot == null) _entityRoot = new GameObject("Entities");
            Local = LocalPlayer.Create(this, w.Bloodline, new Vector3(w.X, CityDressing.GroundHeight(w.X, w.Z) + 0.05f, w.Z));
            State = ClientState.InGame;
            StatusText = "";
            Chat.Clear();
            Notices.Clear();
            PlayerPrefs.Save();
        }

        private void BuildCity(int seed)
        {
            if (City != null && City.Seed == seed && _cityRoot != null) return;
            if (_cityRoot != null) Destroy(_cityRoot);
            City = new CityLayout(seed);
            _cityRoot = CityBuilder.Build(City);
        }

        private void OnSnapshot(PacketReader r)
        {
            SnapshotMsg.Read(r, _snapshot);
            float now = Time.time;
            foreach (EntityState s in _snapshot)
            {
                if (s.Id == MyId) continue;
                EntityView v;
                if (!Views.TryGetValue(s.Id, out v) || v == null)
                {
                    EntityInfoMsg info;
                    bool known = Infos.TryGetValue(s.Id, out info);
                    v = EntityView.Create(s.Id, s.Kind, _entityRoot.transform, known ? info : (EntityInfoMsg?)null);
                    Views[s.Id] = v;
                }
                v.ApplyState(s, now);
            }
            // Entities missing from the snapshot left our interest area (or were removed).
            _toRemove.Clear();
            foreach (var kv in Views) if (kv.Value == null || kv.Value.LastSeen < now) _toRemove.Add(kv.Key);
            foreach (int id in _toRemove)
            {
                if (Views[id] != null) Destroy(Views[id].gameObject);
                Views.Remove(id);
                if (TargetId == id) TargetId = 0;
            }
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------

        public EntityView Target
        {
            get
            {
                EntityView v;
                return TargetId != 0 && Views.TryGetValue(TargetId, out v) ? v : null;
            }
        }

        public BloodlineDef MyDef { get { return GameData.GetBloodline(MyBloodline); } }

        private void HandleActionInput()
        {
            if (ChatOpen || Local == null) return;

            if (Input.GetMouseButtonDown(0) && !UiCapturesMouse) PickTarget();
            if (Input.GetKeyDown(KeyCode.Tab)) CycleTarget();
            if (Input.GetKeyDown(KeyCode.Escape)) { TargetId = 0; ThrallPanelOpen = false; HelpOpen = false; GiftsPanelOpen = false; }
            if (Input.GetKeyDown(KeyCode.F)) ToggleFeed();
            if (Input.GetKeyDown(KeyCode.Alpha1)) DoAttack();
            if (Input.GetKeyDown(KeyCode.Alpha2)) UseGift(0);
            if (Input.GetKeyDown(KeyCode.Alpha3)) UseGift(1);
            if (Input.GetKeyDown(KeyCode.Alpha4)) UseGift(2);
            if (Input.GetKeyDown(KeyCode.G)) GiftsPanelOpen = !GiftsPanelOpen;
            if (Input.GetKeyDown(KeyCode.T)) ThrallPanelOpen = !ThrallPanelOpen;
            if (Input.GetKeyDown(KeyCode.H) || Input.GetKeyDown(KeyCode.F1)) HelpOpen = !HelpOpen;
        }

        public void ToggleFeed()
        {
            if (Self == null) return;
            if ((Self.Flags & SelfFlags.Feeding) != 0) { Send(SimpleMsg.FeedStop()); return; }
            EntityView t = Target;
            if (t == null || t.Kind != EntityKind.Mortal)
            {
                // Feed on the closest mortal in reach if nothing suitable is targeted.
                t = Nearest(e => e.Kind == EntityKind.Mortal && (e.Flags & EntityFlags.Down) == 0, GameRules.FeedRange + 0.5f);
                if (t == null) { LocalNotice("Target a mortal within reach to feed (Tab / click)."); return; }
                TargetId = t.Id;
            }
            Send(SimpleMsg.FeedStart(t.Id));
        }

        public void DoAttack()
        {
            EntityView t = Target;
            if (t == null) { LocalNotice("No target. Tab or click to select one."); return; }
            Send(SimpleMsg.Attack(t.Id));
        }

        public bool KnowsGift(int slot)
        {
            return Self != null && (Self.LearnedGifts & (1 << slot)) != 0;
        }

        /// <summary>Uses the gift in a slot (0-2): self/area gifts need no target.</summary>
        public void UseGift(int slot)
        {
            GiftDef gift = MyDef.Gifts[slot];
            if (!KnowsGift(slot))
            {
                LocalNotice(gift.Name + " is not learned yet (" + gift.StandingCost + " standing). Press G.");
                GiftsPanelOpen = true;
                return;
            }
            if (gift.Target == PowerTarget.Self) { Send(SimpleMsg.Power(slot, 0)); return; }
            EntityView t = Target;
            if (t == null) { LocalNotice(gift.Name + " needs a target."); return; }
            Send(SimpleMsg.Power(slot, t.Id));
        }

        public void SendChat(ChatChannel ch, string text)
        {
            if (!string.IsNullOrEmpty(text)) Send(SimpleMsg.Chat(ch, text));
        }

        public void LocalNotice(string text)
        {
            Notices.Add(new NoticeLine { Level = NoticeLevel.Info, Text = text, Time = Time.time });
            if (Notices.Count > 5) Notices.RemoveAt(0);
        }

        private EntityView Nearest(Func<EntityView, bool> filter, float maxDist)
        {
            EntityView best = null;
            float bestD = maxDist;
            Vector3 me = Local.transform.position;
            foreach (EntityView v in Views.Values)
            {
                if (v == null || !filter(v)) continue;
                float d = Vector3.Distance(me, v.transform.position);
                if (d < bestD) { bestD = d; best = v; }
            }
            return best;
        }

        private void CycleTarget()
        {
            // Cycle through living entities within 25m ordered by distance.
            var list = new List<EntityView>();
            Vector3 me = Local.transform.position;
            foreach (EntityView v in Views.Values)
                if (v != null && (v.Flags & EntityFlags.Down) == 0 && Vector3.Distance(me, v.transform.position) < 25f) list.Add(v);
            if (list.Count == 0) { TargetId = 0; return; }
            list.Sort((a, b) => Vector3.Distance(me, a.transform.position).CompareTo(Vector3.Distance(me, b.transform.position)));
            int idx = list.FindIndex(v => v.Id == TargetId);
            TargetId = list[(idx + 1) % list.Count].Id;
        }

        private void PickTarget()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, 120f, ~0, QueryTriggerInteraction.Collide);
            float best = float.MaxValue;
            EntityView picked = null;
            foreach (RaycastHit h in hits)
            {
                EntityView v = h.collider.GetComponentInParent<EntityView>();
                if (v != null && h.distance < best) { best = h.distance; picked = v; }
            }
            if (picked != null) TargetId = picked.Id;
        }

        private void UpdateTargetRing()
        {
            EntityView t = State == ClientState.InGame ? Target : null;
            if (t == null) { if (_targetRing != null) _targetRing.SetActive(false); return; }
            if (_targetRing == null)
            {
                _targetRing = Mats.Prim(PrimitiveType.Cylinder, null, Vector3.zero, new Vector3(1.6f, 0.02f, 1.6f), new Color(0.9f, 0.1f, 0.15f), false, true);
                _targetRing.name = "Target Ring";
            }
            _targetRing.SetActive(true);
            _targetRing.transform.position = t.transform.position + new Vector3(0, 0.05f, 0);
        }
    }
}
