using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Sanguine.Server
{
    // Chat, slash commands, circles (player factions), the Regent election,
    // blood hunts, district influence and thrall missions.
    public sealed partial class GameServer
    {
        // ------------------------------------------------------------------
        // Chat
        // ------------------------------------------------------------------

        private void HandleChat(PlayerEntity p, ChatChannel channel, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;

            Session s = p.Session;
            if (_now < s.NextChatAllowed) { s.ChatBurst++; if (s.ChatBurst > 5) { SystemMsg(p, "Slow down."); return; } }
            else s.ChatBurst = 0;
            s.NextChatAllowed = _now + 1.0;

            if (text[0] == '/') { HandleCommand(p, text); return; }

            switch (channel)
            {
                case ChatChannel.Circle:
                    if (p.Profile.Circle.Length == 0) { SystemMsg(p, "You are not in a circle."); return; }
                    byte[] cm = SimpleMsg.ChatMsg(ChatChannel.Circle, p.Name, text);
                    foreach (var o in _players) if (SameCircle(o, p) || o == p) o.Session.Send(cm);
                    break;
                case ChatChannel.Lineage:
                    LineageChat(p, text);
                    break;
                case ChatChannel.City:
                    byte[] gm = SimpleMsg.ChatMsg(ChatChannel.City, p.Name, text);
                    foreach (var o in _players) o.Session.Send(gm);
                    break;
                default:
                    byte[] lm = SimpleMsg.ChatMsg(ChatChannel.Local, p.Name, text);
                    foreach (var o in _players) if (o.DistanceTo(p) <= GameRules.LocalChatRange) o.Session.Send(lm);
                    break;
            }
        }

        private void HandleCommand(PlayerEntity p, string text)
        {
            string[] parts = text.Substring(1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            string cmd = parts[0].ToLowerInvariant();
            string rest = parts.Length > 1 ? text.Substring(text.IndexOf(' ') + 1).Trim() : "";
            string arg1 = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "help": CmdHelp(p); break;
                case "who": CmdWho(p); break;
                case "stats": CmdStats(p); break;
                case "c": HandleChat(p, ChatChannel.Circle, rest); break;
                case "city": case "g": HandleChat(p, ChatChannel.City, rest); break;
                case "l": case "say": HandleChat(p, ChatChannel.Local, rest); break;
                case "circle": CmdCircle(p, parts); break;
                case "vote": CmdVote(p, arg1); break;
                case "hunt": CmdHunt(p, arg1); break;
                case "edict": CmdEdict(p, rest); break;
                case "thrall": CmdThrall(p, arg1); break;
                case "gifts": CmdGifts(p); break;
                case "learn": CmdLearn(p, rest); break;
                case "districts": CmdDistricts(p); break;
                case "lin": HandleChat(p, ChatChannel.Lineage, rest); break;
                case "embrace": CmdEmbrace(p, arg1); break;
                case "accept": CmdAccept(p); break;
                case "refuse": CmdRefuse(p); break;
                case "lineage": case "line": CmdLineage(p, parts); break;
                default:
                    if (_cfg.DebugCommands && HandleDebugCommand(p, cmd, parts)) break;
                    SystemMsg(p, "Unknown command. Try /help.");
                    break;
            }
        }

        private void CmdHelp(PlayerEntity p)
        {
            SystemMsg(p, "Chat: /say /c (circle) /lin (lineage) /city   Info: /who /stats /districts /lineage");
            SystemMsg(p, "Circles: /circle create <name> | invite <player> | join <name> | leave | kick <player> | info");
            SystemMsg(p, "Politics: /vote <player> (Regent chosen at dawn, votes weighted by standing)");
            SystemMsg(p, "Regent only: /hunt <player> (declare a blood hunt)  /edict <text>");
            SystemMsg(p, "Thralls: /thrall <1-4|district> sends a ghoul servant to sway a district.");
            SystemMsg(p, "Gifts: /gifts lists your bloodline's gifts; /learn <2|3|name> spends standing to learn one.");
            SystemMsg(p, "Lineage: /embrace <fledgling>, /accept or /refuse an offer, /lineage [release|disown <childe> | renounce].");
        }

        private void CmdWho(PlayerEntity p)
        {
            var sb = new StringBuilder("Kindred in the city (" + _players.Count + "): ");
            for (int i = 0; i < _players.Count; i++)
            {
                var o = _players[i];
                if (i > 0) sb.Append(", ");
                sb.Append(o.Name).Append(" [").Append(o.Def.Name).Append(']');
                if (o.Profile.Circle.Length > 0) sb.Append(" <").Append(o.Profile.Circle).Append('>');
                if (o.Profile.Sire.Length > 0) sb.Append(" childe of ").Append(NameOf(o.Profile.Sire));
                if (IsRegent(o)) sb.Append(" (Regent)");
            }
            SystemMsg(p, sb.ToString());
        }

        private void CmdStats(PlayerEntity p)
        {
            var pr = p.Profile;
            SystemMsg(p, string.Format(CultureInfo.InvariantCulture,
                "{0} of the {1}: standing {2}, conscience {3:0.0}, feedings {4}, mortal deaths {5}, thrall missions {6}.",
                pr.Name, p.Def.Name, pr.Standing, pr.Conscience, pr.Feedings, pr.Kills, pr.Missions));
        }

        // ------------------------------------------------------------------
        // Gifts
        // ------------------------------------------------------------------

        private void CmdGifts(PlayerEntity p)
        {
            SystemMsg(p, "Gifts of the " + p.Def.Name + " (standing " + p.Standing + "):");
            foreach (GiftDef g in p.Def.Gifts)
            {
                PlayerProfile teacher;
                int cost = GiftCostFor(p, g, out teacher);
                string state = p.HasGift(g.Slot) ? "known" : "learn for " + cost + " standing" + (teacher != null ? ", taught by " + teacher.Name : "");
                SystemMsg(p, string.Format(CultureInfo.InvariantCulture, "{0}. {1} [{2}] - {3} ({4} blood, {5}s)",
                    g.Tier, g.Name, state, g.Description, g.Cost, g.Cooldown));
            }
        }

        private void CmdLearn(PlayerEntity p, string arg)
        {
            GiftDef gift = null;
            int n;
            if (int.TryParse(arg, out n) && n >= 1 && n <= GameRules.GiftSlots) gift = p.Def.Gifts[n - 1];
            else
                foreach (GiftDef g in p.Def.Gifts)
                    if (arg.Length > 0 && g.Name.ToLowerInvariant().StartsWith(arg.ToLowerInvariant())) gift = g;
            if (gift == null) { SystemMsg(p, "Usage: /learn <2|3|gift name>. See /gifts."); return; }
            if (p.HasGift(gift.Slot)) { SystemMsg(p, "You already know " + gift.Name + "."); return; }
            if (gift.Slot > 1 && !p.HasGift(gift.Slot - 1)) { SystemMsg(p, "Learn " + p.Def.Gifts[gift.Slot - 1].Name + " first."); return; }
            PlayerProfile teacher;
            int cost = GiftCostFor(p, gift, out teacher);
            if (p.Standing < cost)
            {
                SystemMsg(p, gift.Name + " costs " + cost + " standing; you have " + p.Standing + ".");
                return;
            }
            bool wasFledgling = IsFledgling(p.Profile);
            p.Standing -= cost;
            p.Profile.Gifts |= 1 << gift.Slot;
            _store.SaveProfile(p.Profile);
            if (teacher != null)
            {
                teacher.Standing += GameRules.TeachStanding;
                PlayerEntity to = OnlineByKey(teacher.Key);
                if (to != null) Notice(to, NoticeLevel.Good, "You teach " + gift.Name + " to your childe " + p.Name + ". (+" + GameRules.TeachStanding + " standing)");
                else _store.SaveProfile(teacher);
                Notice(p, NoticeLevel.Good, "Your sire " + teacher.Name + " teaches you " + gift.Name + ". (-" + cost + " standing)");
                p.LineageDirty = true;
                if (to != null) to.LineageDirty = true;
            }
            else Notice(p, NoticeLevel.Good, "Your elders teach you " + gift.Name + ". (-" + cost + " standing)");
            SystemMsg(p, gift.Name + ": " + gift.Description + " Use it with key " + (gift.Slot + 2) + ".");
            if (wasFledgling)
            {
                p.EmbraceOfferFrom = "";
                SystemMsg(p, "You came into your power alone: you are Unclaimed now. No sire can claim you, but you may /embrace fledglings of your own.");
                MarkLineageDirty();
            }
        }

        private void CmdDistricts(PlayerEntity p)
        {
            for (int d = 0; d < 4; d++)
            {
                var dd = GameData.Districts[d];
                var sb = new StringBuilder();
                sb.Append(d + 1).Append(". ").Append(dd.Name).Append(" (").Append(dd.Influence).Append("): ");
                string ctrl = DistrictController(d);
                sb.Append(ctrl.Length > 0 ? "controlled by " + ctrl : "uncontrolled");
                var list = new List<KeyValuePair<string, int>>(_world.Influence[d]);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < list.Count && i < 3; i++)
                {
                    Circle c = FindCircle(list[i].Key);
                    sb.Append(i == 0 ? " | " : ", ").Append(c != null ? c.Name : list[i].Key).Append(' ').Append(list[i].Value);
                }
                SystemMsg(p, sb.ToString());
            }
        }

        // ------------------------------------------------------------------
        // Circles
        // ------------------------------------------------------------------

        private Circle FindCircle(string name)
        {
            Circle c;
            return _world.Circles.TryGetValue(Store.KeyOf(name), out c) ? c : null;
        }

        private static bool ValidCircleName(string n)
        {
            if (n.Length < 3 || n.Length > 20) return false;
            foreach (char ch in n) if (!((char.IsLetterOrDigit(ch) && ch < 128) || ch == ' ' || ch == '_' || ch == '-')) return false;
            return true;
        }

        private void CmdCircle(PlayerEntity p, string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "info";
            string arg = parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2) : "";
            string myKey = p.Profile.Key;
            Circle mine = p.Profile.Circle.Length > 0 ? FindCircle(p.Profile.Circle) : null;

            switch (sub)
            {
                case "create":
                    if (mine != null) { SystemMsg(p, "Leave your current circle first."); return; }
                    if (!ValidCircleName(arg)) { SystemMsg(p, "Circle names are 3-20 letters, digits, spaces, - or _."); return; }
                    if (FindCircle(arg) != null) { SystemMsg(p, "A circle by that name already exists."); return; }
                    var c = new Circle { Name = arg, Founder = myKey };
                    c.Members.Add(myKey);
                    _world.Circles[Store.KeyOf(arg)] = c;
                    SetCircle(p, c.Name);
                    BroadcastSystem(p.Name + " has founded the circle '" + c.Name + "'.", null);
                    _store.SaveWorld(_world);
                    break;

                case "invite":
                {
                    if (mine == null) { SystemMsg(p, "You are not in a circle."); return; }
                    var target = FindOnline(arg);
                    if (target == null) { SystemMsg(p, "No such Kindred is awake."); return; }
                    mine.Invites.Add(target.Profile.Key);
                    SystemMsg(p, "You invited " + target.Name + " to " + mine.Name + ".");
                    Notice(target, NoticeLevel.Info, p.Name + " invites you to the circle '" + mine.Name + "'. Type /circle join " + mine.Name);
                    break;
                }

                case "join":
                {
                    if (mine != null) { SystemMsg(p, "Leave your current circle first."); return; }
                    Circle c2 = FindCircle(arg);
                    if (c2 == null) { SystemMsg(p, "No such circle."); return; }
                    if (!c2.Invites.Contains(myKey)) { SystemMsg(p, "You have not been invited to " + c2.Name + "."); return; }
                    c2.Invites.Remove(myKey);
                    c2.Members.Add(myKey);
                    SetCircle(p, c2.Name);
                    CircleBroadcast(c2, p.Name + " has joined the circle.");
                    _store.SaveWorld(_world);
                    break;
                }

                case "leave":
                    if (mine == null) { SystemMsg(p, "You are not in a circle."); return; }
                    RemoveFromCircle(mine, myKey);
                    SetCircle(p, "");
                    SystemMsg(p, "You left " + mine.Name + ".");
                    CircleBroadcast(mine, p.Name + " has left the circle.");
                    break;

                case "kick":
                {
                    if (mine == null || mine.Founder != myKey) { SystemMsg(p, "Only the founder may cast out members."); return; }
                    string key = Store.KeyOf(arg);
                    if (key == myKey || !mine.Members.Contains(key)) { SystemMsg(p, "They are not a member."); return; }
                    RemoveFromCircle(mine, key);
                    PlayerProfile kp;
                    if (_profiles.TryGetValue(key, out kp)) kp.Circle = "";
                    var online = FindOnline(arg);
                    if (online != null) { SetCircle(online, ""); Notice(online, NoticeLevel.Warning, "You were cast out of " + mine.Name + "."); }
                    else if (kp != null) _store.SaveProfile(kp);
                    CircleBroadcast(mine, arg + " was cast out of the circle.");
                    break;
                }

                default:
                {
                    if (mine == null) { SystemMsg(p, "You are not in a circle. /circle create <name> to found one."); return; }
                    var names = new List<string>();
                    foreach (string k in mine.Members)
                    {
                        PlayerProfile mp;
                        string n = _profiles.TryGetValue(k, out mp) ? mp.Name : k;
                        if (k == mine.Founder) n += " (founder)";
                        names.Add(n);
                    }
                    SystemMsg(p, "Circle " + mine.Name + ": " + string.Join(", ", names.ToArray()));
                    break;
                }
            }
        }

        private void SetCircle(PlayerEntity p, string circleName)
        {
            p.Profile.Circle = circleName;
            InvalidateInfo(p.Id);
            _store.SaveProfile(p.Profile);
            SendWorldState(p);
        }

        private void RemoveFromCircle(Circle c, string key)
        {
            c.Members.Remove(key);
            if (c.Members.Count == 0)
            {
                _world.Circles.Remove(Store.KeyOf(c.Name));
                foreach (var inf in _world.Influence) inf.Remove(Store.KeyOf(c.Name));
                BroadcastSystem("The circle '" + c.Name + "' has dissolved.", null);
            }
            else if (c.Founder == key) c.Founder = c.Members[0];
            _store.SaveWorld(_world);
        }

        private void CircleBroadcast(Circle c, string text)
        {
            byte[] data = SimpleMsg.ChatMsg(ChatChannel.Circle, "", text);
            foreach (var o in _players)
                if (Store.KeyOf(o.Profile.Circle) == Store.KeyOf(c.Name)) o.Session.Send(data);
        }

        // ------------------------------------------------------------------
        // Regent election
        // ------------------------------------------------------------------

        private int VoteWeight(string voterKey)
        {
            PlayerProfile vp;
            return 1 + (_profiles.TryGetValue(voterKey, out vp) ? vp.Standing / 10 : 0);
        }

        private Dictionary<string, int> Tally()
        {
            var t = new Dictionary<string, int>();
            foreach (var kv in _votes)
            {
                int cur;
                t.TryGetValue(kv.Value, out cur);
                t[kv.Value] = cur + VoteWeight(kv.Key);
            }
            return t;
        }

        private string[] TopCandidates(int n)
        {
            var list = new List<KeyValuePair<string, int>>(Tally());
            list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            var result = new List<string>();
            for (int i = 0; i < list.Count && i < n; i++)
            {
                PlayerProfile pr;
                string name = _profiles.TryGetValue(list[i].Key, out pr) ? pr.Name : list[i].Key;
                result.Add(name + " (" + list[i].Value + ")");
            }
            return result.ToArray();
        }

        private void CmdVote(PlayerEntity p, string candidate)
        {
            string key = Store.KeyOf(candidate);
            PlayerProfile cand;
            if (!_profiles.TryGetValue(key, out cand)) { SystemMsg(p, "No Kindred by that name is known to the court."); return; }
            _votes[p.Profile.Key] = key;
            SystemMsg(p, "You pledge your voice (" + VoteWeight(p.Profile.Key) + ") to " + cand.Name + ". The court decides at dawn.");
            SendWorldState(p);
        }

        private void CmdHunt(PlayerEntity p, string name)
        {
            if (!IsRegent(p)) { SystemMsg(p, "Only the Regent may call a blood hunt."); return; }
            if (_regentHuntUsed) { SystemMsg(p, "You have already called a blood hunt this night."); return; }
            var target = FindOnline(name);
            if (target == null) { SystemMsg(p, "No such Kindred is awake."); return; }
            if (target == p) { SystemMsg(p, "You cannot hunt yourself."); return; }
            _regentHuntUsed = true;
            target.BloodHuntUntil = _now + 600;
            BroadcastNotice(NoticeLevel.Danger, "BLOOD HUNT! Regent " + p.Name + " has declared " + target.Name + " forfeit. Destroy them for standing!");
        }

        private void CmdEdict(PlayerEntity p, string text)
        {
            if (!IsRegent(p)) { SystemMsg(p, "Only the Regent may issue edicts."); return; }
            if (text.Length > 160) text = text.Substring(0, 160);
            _world.Edict = text;
            BroadcastSystem("Edict of Regent " + p.Name + ": " + (text.Length > 0 ? text : "(rescinded)"), null);
            _store.SaveWorld(_world);
        }

        // ------------------------------------------------------------------
        // Influence and thralls
        // ------------------------------------------------------------------

        private string DistrictController(int d)
        {
            string best = "";
            int bestV = GameRules.InfluenceControlThreshold - 1;
            foreach (var kv in _world.Influence[d])
            {
                if (kv.Value > bestV) { bestV = kv.Value; best = kv.Key; }
            }
            if (best.Length == 0) return "";
            Circle c = FindCircle(best);
            return c != null ? c.Name : "";
        }

        private void CmdThrall(PlayerEntity p, string districtArg)
        {
            int d = GameData.ParseDistrict(districtArg);
            if (d < 0) { SystemMsg(p, "Usage: /thrall <1-4 or district name>. Districts: 1 Harbor, 2 Midtown, 3 Old Quarter, 4 Financial Row."); return; }
            if (p.InTorpor) return;
            Thrall free = null;
            foreach (var t in p.Thralls) if (t.State == ThrallState.Idle) { free = t; break; }
            if (free == null) { SystemMsg(p, "All your thralls are busy or recovering."); return; }
            free.State = ThrallState.OnMission;
            free.District = d;
            free.Until = _now + _cfg.ThrallMissionSeconds;
            Notice(p, NoticeLevel.Info, "Your thrall slips into " + GameData.Districts[d].Name + " to work on its " + GameData.Districts[d].Influence + ".");
        }

        private void UpdateThralls()
        {
            foreach (var p in _players)
            {
                foreach (var t in p.Thralls)
                {
                    if (t.State == ThrallState.Recovering && _now >= t.Until) { t.State = ThrallState.Idle; Notice(p, NoticeLevel.Info, "A thrall is ready to serve again."); }
                    if (t.State != ThrallState.OnMission || _now < t.Until) continue;
                    ResolveMission(p, t);
                }
            }
        }

        private void ResolveMission(PlayerEntity p, Thrall t)
        {
            DistrictDef dd = GameData.Districts[t.District];
            float chance = 0.65f;
            if (p.Def.FavoredDistrict == t.District) chance += 0.2f;
            string circle = p.Profile.Circle;
            if (circle.Length > 0 && Store.KeyOf(DistrictController(t.District)) == Store.KeyOf(circle)) chance += 0.1f;
            if (p.Exposure >= GameRules.ExposureHunterThreshold) chance -= 0.15f;

            if (RandF() < chance)
            {
                t.State = ThrallState.Idle;
                p.Profile.Missions++;
                p.Standing += 2;
                string extra = "";
                if (circle.Length > 0)
                {
                    string ck = Store.KeyOf(circle);
                    int cur;
                    _world.Influence[t.District].TryGetValue(ck, out cur);
                    string before = DistrictController(t.District);
                    _world.Influence[t.District][ck] = cur + GameRules.InfluencePerMission;
                    extra = " " + circle + " gains influence (" + (cur + GameRules.InfluencePerMission) + ").";
                    string after = DistrictController(t.District);
                    if (after != before && after.Length > 0)
                        BroadcastSystem("The circle '" + after + "' now controls " + dd.Name + "'s " + dd.Influence + ".", null);
                }
                else
                {
                    p.Standing += 1;
                    extra = " (Join a circle to claim districts.)";
                }
                Notice(p, NoticeLevel.Good, "Your thrall returns from " + dd.Name + " with favours owed. +2 standing." + extra);
            }
            else
            {
                t.State = ThrallState.Recovering;
                t.Until = _now + GameRules.ThrallRecoverySeconds;
                AddExposure(p, 10f, "a thrall was caught");
                Notice(p, NoticeLevel.Warning, "Your thrall was caught meddling in " + dd.Name + ". It needs time to recover. (+10 exposure)");
            }
        }

        // ------------------------------------------------------------------
        // Debug commands (ServerConfig.DebugCommands only)
        // ------------------------------------------------------------------

        private bool HandleDebugCommand(PlayerEntity p, string cmd, string[] parts)
        {
            switch (cmd)
            {
                case "tp":
                    if (parts.Length < 3) return true;
                    TeleportPlayer(p, Store.ParseFloat(parts[1]), Store.ParseFloat(parts[2]));
                    return true;
                case "dawn":
                    ForceDawn();
                    SystemMsg(p, "[debug] Dawn forced.");
                    return true;
                case "setexposure":
                    p.Exposure = parts.Length > 1 ? Store.ParseFloat(parts[1]) : 0;
                    p.LastExposureGain = _now;
                    return true;
                case "setstanding":
                    p.Standing = parts.Length > 1 ? Store.ParseInt(parts[1]) : 0;
                    return true;
                case "setblood":
                    p.Blood = Math.Min(p.MaxBlood, parts.Length > 1 ? Store.ParseFloat(parts[1]) : p.MaxBlood);
                    return true;
                case "pos":
                    SystemMsg(p, string.Format(CultureInfo.InvariantCulture, "[debug] pos {0:0.0} {1:0.0}", p.X, p.Z));
                    return true;
            }
            return false;
        }
    }
}
