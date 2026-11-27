using System.Text;
using UnityEngine;

namespace Sanguine.Client
{
    /// <summary>All UI drawn with IMGUI so the project needs no prefabs, canvases or fonts.</summary>
    public sealed class ClientUI : MonoBehaviour
    {
        public GameClient Client;

        private string _chatInput = "";
        private ChatChannel _chatChannel = ChatChannel.Local;
        private string _portText;
        private GUIStyle _title, _label, _small, _box, _notice, _worldLabel, _center;
        private Texture2D _white;
        private float _uiScale = 1f;
        private readonly Rect[] _mouseRects = new Rect[8];
        private int _mouseRectCount;

        private void Start()
        {
            Client.Host = PlayerPrefs.GetString("sanguine.host", "127.0.0.1");
            Client.Port = PlayerPrefs.GetInt("sanguine.port", GameRules.DefaultPort);
            Client.PlayerName = PlayerPrefs.GetString("sanguine.name", "");
            Client.ChosenBloodline = (Bloodline)PlayerPrefs.GetInt("sanguine.bloodline", 0);
            _portText = Client.Port.ToString();
        }

        private void SavePrefs()
        {
            PlayerPrefs.SetString("sanguine.host", Client.Host);
            PlayerPrefs.SetInt("sanguine.port", Client.Port);
            PlayerPrefs.SetString("sanguine.name", Client.PlayerName);
            PlayerPrefs.SetInt("sanguine.bloodline", (int)Client.ChosenBloodline);
        }

        private void EnsureStyles()
        {
            if (_title != null) return;
            _white = Texture2D.whiteTexture;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 44, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _title.normal.textColor = new Color(0.8f, 0.08f, 0.12f);
            _label = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, wordWrap = true };
            _label.normal.textColor = new Color(0.9f, 0.88f, 0.9f);
            _small = new GUIStyle(_label) { fontSize = 12 };
            _center = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter };
            _box = new GUIStyle(GUI.skin.box);
            _notice = new GUIStyle(_label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _worldLabel = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, wordWrap = false, fontStyle = FontStyle.Bold };
        }

        private void OnGUI()
        {
            EnsureStyles();
            GUI.matrix = Matrix4x4.identity;
            _uiScale = Mathf.Clamp(Screen.height / 900f, 0.8f, 2f);
            _mouseRectCount = 0;

            if (Client.State != ClientState.InGame) { DrawMenu(); return; }

            DrawWorldLabels();
            DrawOverlays();
            GUI.matrix = Matrix4x4.Scale(new Vector3(_uiScale, _uiScale, 1f));
            float w = Screen.width / _uiScale, h = Screen.height / _uiScale;

            DrawStatus();
            DrawClock(w);
            DrawCityPanel(w);
            DrawTarget(w);
            DrawNotices(w, h);
            DrawActionBar(w, h);
            DrawChat(h);
            if (Client.ThrallPanelOpen) DrawThralls(w, h);
            if (Client.HelpOpen) DrawHelp(w, h);

            // Track whether the mouse is over UI so clicks don't pick targets or orbit the camera.
            // (Event.current.mousePosition is affected by GUI.matrix, so use raw screen coordinates.)
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool over = false;
            for (int i = 0; i < _mouseRectCount; i++) if (_mouseRects[i].Contains(mouse)) over = true;
            Client.UiCapturesMouse = over;
        }

        private void Capture(Rect r) { if (_mouseRectCount < _mouseRects.Length) _mouseRects[_mouseRectCount++] = r; }

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        private void Bar(Rect r, float value, float max, Color c, string text)
        {
            Fill(r, new Color(0, 0, 0, 0.6f));
            Fill(new Rect(r.x + 1, r.y + 1, (r.width - 2) * Mathf.Clamp01(max > 0 ? value / max : 0), r.height - 2), c);
            GUI.Label(r, text, _center);
        }

        // ------------------------------------------------------------------
        // Main menu
        // ------------------------------------------------------------------

        private void DrawMenu()
        {
            float w = 720, h = 560;
            Rect area = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.55f));
            GUILayout.BeginArea(area, _box);
            GUILayout.Label("SANGUINE CITY", _title);
            GUILayout.Label("<i>An online game of blood, secrecy and night-time politics.</i>", _center);
            GUILayout.Space(12);

            bool busy = Client.State == ClientState.Connecting;
            GUI.enabled = !busy;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Server", _label, GUILayout.Width(80));
            Client.Host = GUILayout.TextField(Client.Host, 64, GUILayout.Width(250));
            GUILayout.Label("Port", _label, GUILayout.Width(40));
            _portText = GUILayout.TextField(_portText, 5, GUILayout.Width(70));
            int port;
            if (int.TryParse(_portText, out port) && port > 0 && port < 65536) Client.Port = port;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", _label, GUILayout.Width(80));
            Client.PlayerName = GUILayout.TextField(Client.PlayerName, 16, GUILayout.Width(250));
            GUILayout.Label("Password", _label, GUILayout.Width(70));
            Client.Password = GUILayout.PasswordField(Client.Password, '*', 32, GUILayout.Width(170));
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("<b>Choose a bloodline</b> (new characters only)", _label);
            GUILayout.BeginHorizontal();
            foreach (BloodlineDef def in GameData.Bloodlines)
            {
                Color old = GUI.backgroundColor;
                GUI.backgroundColor = Client.ChosenBloodline == def.Id ? new Color(def.R, def.G, def.B) * 1.6f : new Color(0.4f, 0.4f, 0.45f);
                if (GUILayout.Button(def.Name + "\n<size=11>" + def.Epithet + "</size>", new GUIStyle(GUI.skin.button) { richText = true }, GUILayout.Height(48)))
                    Client.ChosenBloodline = def.Id;
                GUI.backgroundColor = old;
            }
            GUILayout.EndHorizontal();
            BloodlineDef sel = GameData.GetBloodline(Client.ChosenBloodline);
            GUILayout.Label(sel.Description, _label);
            GUILayout.Label("<b>Gift: " + sel.PowerName + "</b> - " + sel.PowerDescription +
                            "\nBlood " + sel.MaxBlood + "  Health " + sel.MaxHealth + "  Melee " + sel.MeleeDamage +
                            "  Favoured district: " + GameData.Districts[sel.FavoredDistrict].Name, _small);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Volume", _label, GUILayout.Width(80));
            AudioDirector.MasterVolume = GUILayout.HorizontalSlider(AudioDirector.MasterVolume, 0f, 1f, GUILayout.Width(200));
            GUILayout.Space(20);
            GUILayout.Label("Music", _label, GUILayout.Width(50));
            AudioDirector.MusicVolume = GUILayout.HorizontalSlider(AudioDirector.MusicVolume, 0f, 1f, GUILayout.Width(200));
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Join Server", GUILayout.Height(40))) { SavePrefs(); UiClick(); Client.Connect(false); }
            if (GUILayout.Button("Host & Play", GUILayout.Height(40))) { SavePrefs(); UiClick(); Client.Connect(true); }
            if (GUILayout.Button("Quit", GUILayout.Height(40), GUILayout.Width(90))) Application.Quit();
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(Client.StatusText)) GUILayout.Label(Client.StatusText, _center);
            if (Client.HostedServer != null && Client.HostedServer.IsRunning)
                GUILayout.Label("<size=11>Hosting on port " + Client.HostedServer.Port + " - friends can join with your IP address.</size>", _center);
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------
        // HUD
        // ------------------------------------------------------------------

        private void DrawWorldLabels()
        {
            Camera cam = Camera.main;
            if (cam == null || Client.Local == null) return;
            Vector3 me = Client.Local.transform.position;
            foreach (EntityView v in Client.Views.Values)
            {
                if (v == null) continue;
                float d = Vector3.Distance(me, v.transform.position);
                if (d > 35f && v.Kind != EntityKind.Player) continue;
                Vector3 sp = cam.WorldToScreenPoint(v.transform.position + Vector3.up * 2.7f);
                if (sp.z <= 0) continue;
                string text = v.DisplayName;
                if ((v.Flags & EntityFlags.Regent) != 0) text = "Regent " + text;
                if ((v.Flags & EntityFlags.Down) != 0) text += v.Kind == EntityKind.Player ? " (torpor)" : " (dead)";
                Rect r = new Rect(sp.x - 120, Screen.height - sp.y - 10, 240, 20);
                _worldLabel.normal.textColor = Color.black;
                GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, _worldLabel);
                _worldLabel.normal.textColor = v.LabelColor;
                GUI.Label(r, text, _worldLabel);
                if (v.Id == Client.TargetId || v.HealthPct < 0.999f)
                    Fill(new Rect(sp.x - 25, Screen.height - sp.y + 10, 50 * v.HealthPct, 3), new Color(0.9f, 0.15f, 0.15f));
            }

            // Zone names.
            if (Client.City != null)
            {
                foreach (ZoneDef z in Client.City.Zones)
                {
                    Vector3 pos = new Vector3(z.CenterX, 8f, z.CenterZ - 10f);
                    if (Vector3.Distance(me, pos) > 60f) continue;
                    Vector3 sp = cam.WorldToScreenPoint(pos);
                    if (sp.z <= 0) continue;
                    _worldLabel.normal.textColor = z.Kind == ZoneKind.Haven ? Mats.BloodlineColor(z.Owner) : new Color(1f, 0.9f, 0.6f);
                    GUI.Label(new Rect(sp.x - 150, Screen.height - sp.y, 300, 20), z.Name, _worldLabel);
                }
            }
        }

        private void DrawOverlays()
        {
            SelfStateMsg s = Client.Self;
            if (s == null) return;
            if ((s.Flags & SelfFlags.Torpor) != 0)
            {
                Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.25f, 0f, 0f, 0.6f));
                GUI.Label(new Rect(0, Screen.height / 2 - 40, Screen.width, 80), "TORPOR\n<size=16>You will awaken in your haven...</size>", _title);
            }
            else if ((s.Flags & SelfFlags.Sunlight) != 0)
            {
                Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(1f, 0.5f, 0.1f, 0.18f + 0.1f * Mathf.Sin(Time.time * 6f)));
            }
            else if ((s.Flags & SelfFlags.Starving) != 0)
            {
                Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.5f, 0f, 0f, 0.1f + 0.06f * Mathf.Sin(Time.time * 3f)));
            }
        }

        private void DrawStatus()
        {
            SelfStateMsg s = Client.Self;
            if (s == null) return;
            BloodlineDef def = Client.MyDef;
            Rect panel = new Rect(10, 10, 290, 230);
            Capture(Scale(panel));
            Fill(panel, new Color(0, 0, 0, 0.5f));
            GUILayout.BeginArea(new Rect(panel.x + 8, panel.y + 6, panel.width - 16, panel.height - 10));
            GUILayout.Label("<b>" + Client.MyName + "</b>  <color=#" + ColorUtility.ToHtmlStringRGB(Mats.BloodlineColor(def.Id)) + ">" + def.Name + "</color>" +
                            ((s.Flags & SelfFlags.Regent) != 0 ? "  <color=#ffcc33>Regent</color>" : ""), _label);
            ZoneDef zone = Client.City != null && Client.Local != null ? Client.City.ZoneAt(Client.Local.transform.position.x, Client.Local.transform.position.z) : null;
            GUILayout.Label(GameData.Districts[Mathf.Clamp(s.District, 0, 3)].Name + (zone != null ? " - " + zone.Name : ""), _small);
            GUILayout.EndArea();

            float y = panel.y + 52;
            Bar(new Rect(panel.x + 8, y, 274, 18), s.Blood, s.MaxBlood, new Color(0.65f, 0.02f, 0.05f), "Blood " + s.Blood.ToString("0.0") + " / " + s.MaxBlood);
            Bar(new Rect(panel.x + 8, y + 22, 274, 18), s.Health, s.MaxHealth, new Color(0.25f, 0.5f, 0.3f), "Health " + Mathf.CeilToInt(s.Health) + " / " + s.MaxHealth);
            Color expColor = s.Exposure >= GameRules.ExposureMarkedThreshold ? Color.red : (s.Exposure >= GameRules.ExposureHunterThreshold ? new Color(1f, 0.55f, 0f) : new Color(0.7f, 0.6f, 0.2f));
            Rect er = new Rect(panel.x + 8, y + 44, 274, 18);
            Bar(er, s.Exposure, 100f, expColor, "Exposure " + Mathf.RoundToInt(s.Exposure) + "%");
            Fill(new Rect(er.x + er.width * 0.5f, er.y, 1, er.height), Color.white);
            Fill(new Rect(er.x + er.width * 0.8f, er.y, 1, er.height), Color.red);
            Bar(new Rect(panel.x + 8, y + 66, 274, 18), s.Conscience, 10f, new Color(0.3f, 0.4f, 0.75f), "Conscience " + s.Conscience.ToString("0.0") + " / 10");
            GUI.Label(new Rect(panel.x + 8, y + 88, 274, 20), "Standing <b>" + s.Standing + "</b>", _label);

            var flags = new StringBuilder();
            AddFlag(flags, s.Flags, SelfFlags.Marked, "MARKED", "ff4040");
            AddFlag(flags, s.Flags, SelfFlags.Outlaw, "OUTLAW", "ff4040");
            AddFlag(flags, s.Flags, SelfFlags.BloodHunt, "BLOOD HUNT", "ff2020");
            AddFlag(flags, s.Flags, SelfFlags.Beast, "BEAST", "ff8080");
            AddFlag(flags, s.Flags, SelfFlags.Sunlight, "SUNLIGHT!", "ffaa33");
            AddFlag(flags, s.Flags, SelfFlags.Starving, "STARVING", "ff6060");
            AddFlag(flags, s.Flags, SelfFlags.Feeding, "Feeding", "ff9090");
            AddFlag(flags, s.Flags, SelfFlags.Surge, "Surge", "ffd080");
            AddFlag(flags, s.Flags, SelfFlags.Slowed, "Slowed", "c0c0ff");
            AddFlag(flags, s.Flags, SelfFlags.Rooted, "Rooted", "c0c0ff");
            AddFlag(flags, s.Flags, SelfFlags.InHaven, "Haven (safe)", "80ff80");
            AddFlag(flags, s.Flags, SelfFlags.InSanctum, "Sanctum (no violence)", "fff0a0");
            GUI.Label(new Rect(panel.x + 8, y + 108, 274, 60), flags.ToString(), _small);
        }

        private static void AddFlag(StringBuilder sb, SelfFlags flags, SelfFlags f, string text, string color)
        {
            if ((flags & f) == 0) return;
            if (sb.Length > 0) sb.Append("  ");
            sb.Append("<color=#").Append(color).Append('>').Append(text).Append("</color>");
        }

        private void DrawClock(float w)
        {
            WorldStateMsg world = Client.World;
            if (world == null) return;
            int secs = Mathf.Max(0, Mathf.RoundToInt(world.SecondsToChange));
            string time = (secs / 60) + ":" + (secs % 60).ToString("00");
            string line = world.IsDay
                ? "<color=#ffb040><b>DAY</b></color> - nightfall in " + time
                : "<b>Night " + world.Night + "</b> - dawn in " + (secs < 60 ? "<color=#ff6040>" + time + "</color>" : time);
            Rect r = new Rect(w / 2 - 220, 8, 440, 64);
            Fill(r, new Color(0, 0, 0, 0.45f));
            string regent = world.Regent.Length > 0 ? "Regent: <color=#ffcc33>" + world.Regent + "</color>" : "The city has no Regent";
            string edict = world.Edict.Length > 0 ? "\n<i>Edict: " + world.Edict + "</i>" : "";
            GUI.Label(new Rect(r.x + 6, r.y + 2, r.width - 12, r.height), line + "\n" + regent + edict, _center);
        }

        private void DrawCityPanel(float w)
        {
            WorldStateMsg world = Client.World;
            if (world == null) return;
            Rect r = new Rect(w - 270, 10, 260, 190);
            Capture(Scale(r));
            Fill(r, new Color(0, 0, 0, 0.5f));
            var sb = new StringBuilder();
            sb.Append("<b>Districts</b>\n");
            for (int i = 0; i < 4; i++)
            {
                string c = world.DistrictControllers.Length > i ? world.DistrictControllers[i] : "";
                sb.Append(i + 1).Append(". ").Append(GameData.Districts[i].Name).Append(": ")
                  .Append(c.Length > 0 ? "<color=#ffd080>" + c + "</color>" : "<color=#888888>contested</color>").Append('\n');
            }
            sb.Append("<b>Circle:</b> ").Append(world.MyCircle.Length > 0 ? world.MyCircle : "<color=#888888>none</color>").Append('\n');
            sb.Append("<b>Election</b> (at dawn): ");
            sb.Append(world.TopCandidates.Length > 0 ? string.Join(", ", world.TopCandidates) : "no votes yet");
            if (world.MyVote.Length > 0) sb.Append("\nYour vote: ").Append(world.MyVote);
            GUI.Label(new Rect(r.x + 8, r.y + 4, r.width - 16, r.height - 8), sb.ToString(), _small);
        }

        private void DrawTarget(float w)
        {
            EntityView t = Client.Target;
            if (t == null || Client.Local == null) return;
            Rect r = new Rect(w / 2 - 160, 80, 320, 54);
            Fill(r, new Color(0, 0, 0, 0.5f));
            float dist = Vector3.Distance(Client.Local.transform.position, t.transform.position);
            string kind = t.Kind == EntityKind.Player ? GameData.GetBloodline(t.Info.Bloodline).Name : (t.Kind == EntityKind.Mortal ? "Mortal" : "Hunter");
            var fl = new StringBuilder();
            if ((t.Flags & EntityFlags.Alarmed) != 0) fl.Append(" alarmed");
            if ((t.Flags & EntityFlags.Charmed) != 0) fl.Append(" charmed");
            if ((t.Flags & EntityFlags.Marked) != 0) fl.Append(" <color=#ff4040>MARKED</color>");
            if ((t.Flags & EntityFlags.Stunned) != 0) fl.Append(" stunned");
            if ((t.Flags & EntityFlags.Down) != 0) fl.Append(" down");
            GUI.Label(new Rect(r.x, r.y + 2, r.width, 22), "<b>" + t.DisplayName + "</b> (" + kind + ") " + dist.ToString("0.0") + "m" + fl, _center);
            Bar(new Rect(r.x + 10, r.y + 28, r.width - 20, 16), t.HealthPct, 1f, new Color(0.7f, 0.1f, 0.1f), "");
        }

        private void DrawNotices(float w, float h)
        {
            float y = h * 0.28f;
            foreach (NoticeLine n in Client.Notices)
            {
                float age = Time.time - n.Time;
                float alpha = Mathf.Clamp01(7f - age);
                Color c;
                switch (n.Level)
                {
                    case NoticeLevel.Good: c = new Color(0.6f, 1f, 0.6f, alpha); break;
                    case NoticeLevel.Warning: c = new Color(1f, 0.75f, 0.3f, alpha); break;
                    case NoticeLevel.Danger: c = new Color(1f, 0.3f, 0.3f, alpha); break;
                    default: c = new Color(0.95f, 0.95f, 0.95f, alpha); break;
                }
                _notice.normal.textColor = new Color(0, 0, 0, alpha);
                GUI.Label(new Rect(1, y + 1, w, 26), n.Text, _notice);
                _notice.normal.textColor = c;
                GUI.Label(new Rect(0, y, w, 26), n.Text, _notice);
                y += 26;
            }
        }

        private void DrawActionBar(float w, float h)
        {
            SelfStateMsg s = Client.Self;
            if (s == null) return;
            BloodlineDef def = Client.MyDef;
            float bw = 120, bh = 48, gap = 6;
            float total = bw * 5 + gap * 4;
            float x = w / 2 - total / 2, y = h - bh - 10;
            Capture(Scale(new Rect(x, y, total, bh)));

            bool feeding = (s.Flags & SelfFlags.Feeding) != 0;
            if (ActionButton(new Rect(x, y, bw, bh), feeding ? "[F] Release" : "[F] Feed", 0)) Client.ToggleFeed();
            x += bw + gap;
            if (ActionButton(new Rect(x, y, bw, bh), "[1] Strike", s.AttackCooldown)) Client.DoAttack();
            x += bw + gap;
            if (ActionButton(new Rect(x, y, bw, bh), "[2] " + def.PowerName + "\n<size=10>" + def.PowerCost + " blood</size>", s.PowerCooldown)) Client.DoPower();
            x += bw + gap;
            if (ActionButton(new Rect(x, y, bw, bh), "[T] Thralls", 0)) Client.ThrallPanelOpen = !Client.ThrallPanelOpen;
            x += bw + gap;
            if (ActionButton(new Rect(x, y, bw, bh), "[H] Help", 0)) Client.HelpOpen = !Client.HelpOpen;
        }

        private static void UiClick()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.Click();
        }

        private bool ActionButton(Rect r, string text, float cooldown)
        {
            var style = new GUIStyle(GUI.skin.button) { richText = true, fontSize = 13 };
            bool pressed = GUI.Button(r, text, style);
            if (pressed) UiClick();
            if (cooldown > 0.05f)
            {
                Fill(r, new Color(0, 0, 0, 0.55f));
                GUI.Label(r, cooldown.ToString("0.0") + "s", _center);
            }
            return pressed;
        }

        private void DrawChat(float h)
        {
            Rect log = new Rect(10, h - 300, 460, 220);
            Capture(Scale(new Rect(log.x, log.y, log.width, log.height + 70)));
            Fill(log, new Color(0, 0, 0, 0.35f));
            var sb = new StringBuilder();
            int start = Mathf.Max(0, Client.Chat.Count - 11);
            for (int i = start; i < Client.Chat.Count; i++)
            {
                ChatLine c = Client.Chat[i];
                string color;
                switch (c.Channel)
                {
                    case ChatChannel.Circle: color = "80d0ff"; break;
                    case ChatChannel.City: color = "ffd080"; break;
                    case ChatChannel.System: color = "c0a0ff"; break;
                    default: color = "e0e0e0"; break;
                }
                sb.Append("<color=#").Append(color).Append('>');
                if (c.Channel != ChatChannel.Local && c.Channel != ChatChannel.System) sb.Append('[').Append(c.Channel).Append("] ");
                if (!string.IsNullOrEmpty(c.From)) sb.Append("<b>").Append(Escape(c.From)).Append("</b>: ");
                sb.Append(Escape(c.Text)).Append("</color>\n");
            }
            GUI.Label(new Rect(log.x + 6, log.y + 4, log.width - 12, log.height - 8), sb.ToString(), _small);

            Rect input = new Rect(10, h - 76, 460, 24);
            Event e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                if (Client.ChatOpen)
                {
                    Client.SendChat(_chatChannel, _chatInput);
                    _chatInput = "";
                    Client.ChatOpen = false;
                    GUI.FocusControl(null);
                }
                else
                {
                    Client.ChatOpen = true;
                    GUI.FocusControl("chat");
                }
                e.Use();
            }
            else if (Client.ChatOpen && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                Client.ChatOpen = false;
                _chatInput = "";
                GUI.FocusControl(null);
                e.Use();
            }

            if (Client.ChatOpen)
            {
                if (GUI.Button(new Rect(input.x, input.y, 70, input.height), _chatChannel.ToString()))
                    _chatChannel = _chatChannel == ChatChannel.Local ? ChatChannel.Circle : (_chatChannel == ChatChannel.Circle ? ChatChannel.City : ChatChannel.Local);
                GUI.SetNextControlName("chat");
                _chatInput = GUI.TextField(new Rect(input.x + 74, input.y, input.width - 74, input.height), _chatInput, 200);
                GUI.FocusControl("chat");
            }
            else
            {
                GUI.Label(input, "<color=#999999>Press Enter to chat. /help for commands.</color>", _small);
            }
        }

        private static string Escape(string s)
        {
            // Prevent players from injecting rich-text tags.
            return s.Replace("<", "<​");
        }

        private void DrawThralls(float w, float h)
        {
            SelfStateMsg s = Client.Self;
            if (s == null) return;
            Rect r = new Rect(w / 2 - 230, h - 300, 460, 230);
            Capture(Scale(r));
            GUILayout.BeginArea(r, _box);
            GUILayout.Label("<b>Thralls</b> - ghoul servants who sway the city's institutions for your circle.", _label);
            for (int i = 0; i < s.Thralls.Length; i++)
            {
                ThrallInfo t = s.Thralls[i];
                string state;
                switch (t.State)
                {
                    case ThrallState.OnMission: state = "working " + GameData.Districts[Mathf.Clamp(t.District, 0, 3)].Name + " (" + Mathf.CeilToInt(t.Remaining) + "s)"; break;
                    case ThrallState.Recovering: state = "<color=#ff9060>recovering</color> (" + Mathf.CeilToInt(t.Remaining) + "s)"; break;
                    default: state = "<color=#80ff80>ready</color>"; break;
                }
                GUILayout.Label("Thrall " + (i + 1) + ": " + state, _label);
            }
            GUILayout.Label("Send a ready thrall to:", _small);
            GUILayout.BeginHorizontal();
            for (int d = 0; d < 4; d++)
            {
                DistrictDef dd = GameData.Districts[d];
                string fav = Client.MyDef.FavoredDistrict == d ? " *" : "";
                if (GUILayout.Button(dd.Name + fav + "\n<size=10>" + dd.Influence + "</size>", new GUIStyle(GUI.skin.button) { richText = true }, GUILayout.Height(40)))
                    Client.SendChat(ChatChannel.Local, "/thrall " + (d + 1));
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("<size=11>* favoured district (+20% success). Success: +2 standing and +10 influence for your circle. Failure: the thrall is caught (+10 exposure).</size>", _small);
            GUILayout.EndArea();
        }

        private void DrawHelp(float w, float h)
        {
            Rect r = new Rect(w / 2 - 300, 120, 600, Mathf.Min(560, h - 200));
            Capture(Scale(r));
            GUILayout.BeginArea(r, _box);
            GUILayout.Label("<b>How to survive the night</b>", _label);
            GUILayout.Label(
                "<b>Controls</b>: WASD move, right-drag or Q/E turn the camera, mouse wheel zoom. Click or Tab to target. " +
                "F feed / release, 1 strike, 2 use your gift, T thralls, Enter chat, Esc clear target.\n\n" +
                "<b>Blood</b> drains over time and fuels your gift and healing. Feed on mortals, but release them (F) before " +
                "their heart stops: killing lowers your <b>Conscience</b>. At 3 or less the Beast rules you and you are Marked.\n\n" +
                "<b>The Hush</b>: feeding, violence and powers seen by mortals raise <b>Exposure</b>. At 50% hunters come for you; " +
                "at 80% you are <b>Marked</b>. Exposure fades when you lie low. Aurum can make witnesses forget.\n\n" +
                "<b>PvP</b> is open everywhere except havens and sanctums. Destroying a Marked vampire earns standing; destroying " +
                "an innocent one makes you an Outlaw. Circle members cannot harm each other.\n\n" +
                "<b>Dawn</b>: be inside a haven or sanctum when the sun rises, or burn.\n\n" +
                "<b>Politics</b>: /circle create to found a faction, send thralls to win districts, /vote for a Regent each night. " +
                "The Regent may /edict and call one /hunt per night.", _small);
            if (GUILayout.Button("Close")) Client.HelpOpen = false;
            GUILayout.EndArea();
        }

        private Rect Scale(Rect r)
        {
            return new Rect(r.x * _uiScale, r.y * _uiScale, r.width * _uiScale, r.height * _uiScale);
        }
    }
}
