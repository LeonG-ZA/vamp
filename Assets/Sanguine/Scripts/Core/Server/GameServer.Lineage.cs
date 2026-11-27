using System;
using System.Collections.Generic;
using System.Text;

namespace Sanguine.Server
{
    // Sires and childer: the Embrace as a player action, the family tree and the ties it brings.
    //
    //  * A fledgling (no sire, second gift not yet learned) can be offered the Embrace by an elder who knows
    //    their own second gift. Accepting remakes the fledgling in the sire's bloodline.
    //  * While in tutelage the childe learns gifts its sire knows at a discount (the sire gains standing for
    //    teaching), cannot sire, and the sire answers for the childe's crimes with their own standing.
    //  * Once the childe knows all three gifts the sire may release them (+standing for the sire).
    //  * Sire and childe can never harm each other, share the blood bond when close, feel each other fall
    //    into torpor, and the whole family shares a lineage chat channel.
    public sealed partial class GameServer
    {
        private static readonly List<string> NoKeys = new List<string>();

        /// <summary>Sire key to the keys of their childer. Derived from PlayerProfile.Sire.</summary>
        private readonly Dictionary<string, List<string>> _childer = new Dictionary<string, List<string>>();

        // ------------------------------------------------------------------
        // The family tree
        // ------------------------------------------------------------------

        private void BuildLineageIndex()
        {
            _childer.Clear();
            foreach (PlayerProfile pr in _profiles.Values)
                if (pr.Sire.Length > 0 && (pr.Sire == pr.Key || !_profiles.ContainsKey(pr.Sire))) { pr.Sire = ""; pr.Released = false; }

            // A hand-edited save could contain a loop; cut it where it closes.
            foreach (PlayerProfile pr in _profiles.Values)
            {
                var seen = new HashSet<string> { pr.Key };
                PlayerProfile cur = pr;
                while (cur.Sire.Length > 0)
                {
                    if (!seen.Add(cur.Sire)) { Log("Lineage loop at " + cur.Name + "; cutting it."); cur.Sire = ""; cur.Released = false; break; }
                    cur = _profiles[cur.Sire];
                }
            }
            foreach (PlayerProfile pr in _profiles.Values) if (pr.Sire.Length > 0) ChilderList(pr.Sire, true).Add(pr.Key);
        }

        private List<string> ChilderList(string sireKey, bool create)
        {
            List<string> list;
            if (_childer.TryGetValue(sireKey, out list)) return list;
            if (!create) return NoKeys;
            list = new List<string>();
            _childer[sireKey] = list;
            return list;
        }

        private List<string> ChilderOf(string key) { return ChilderList(key, false); }

        private PlayerProfile Prof(string key)
        {
            PlayerProfile p;
            return key != null && key.Length > 0 && _profiles.TryGetValue(key, out p) ? p : null;
        }

        private string NameOf(string key)
        {
            PlayerProfile p = Prof(key);
            return p != null ? p.Name : "";
        }

        private PlayerEntity OnlineByKey(string key)
        {
            foreach (PlayerEntity p in _players) if (p.Profile.Key == key) return p;
            return null;
        }

        private static bool InTutelage(PlayerProfile p) { return p.Sire.Length > 0 && !p.Released; }

        private bool IsFledgling(PlayerProfile p)
        {
            return p.Sire.Length == 0 && (p.Gifts & 2) == 0 && ChilderOf(p.Key).Count == 0;
        }

        private LineageStatus StatusOf(PlayerProfile p)
        {
            if (p.Sire.Length > 0) return p.Released ? LineageStatus.Released : LineageStatus.Tutelage;
            return IsFledgling(p) ? LineageStatus.Fledgling : LineageStatus.Unclaimed;
        }

        private int TutelageCount(string key)
        {
            int n = 0;
            foreach (string c in ChilderOf(key)) { PlayerProfile cp = Prof(c); if (cp != null && !cp.Released) n++; }
            return n;
        }

        /// <summary>Why this vampire may not sire a childe right now, or null if they may.</summary>
        private string CannotSire(PlayerProfile p)
        {
            if (InTutelage(p)) return "You cannot sire while you are still in your sire's tutelage.";
            if ((p.Gifts & 2) == 0) return "Only Kindred who have learned their second gift may sire a childe.";
            if (TutelageCount(p.Key) >= GameRules.MaxChilderInTutelage)
                return "You already have " + GameRules.MaxChilderInTutelage + " childer in your tutelage. Release one first.";
            return null;
        }

        /// <summary>Sire and childe: blood of each other's blood.</summary>
        private static bool IsDirectKin(PlayerEntity a, PlayerEntity b)
        {
            return a != b && (a.Profile.Sire == b.Profile.Key || b.Profile.Sire == a.Profile.Key);
        }

        private string RootOf(string key)
        {
            PlayerProfile p = Prof(key);
            for (int guard = 0; p != null && p.Sire.Length > 0 && guard < 64; guard++)
            {
                PlayerProfile up = Prof(p.Sire);
                if (up == null) break;
                p = up;
            }
            return p != null ? p.Key : key;
        }

        private void CollectFamily(string root, List<string> into)
        {
            if (into.Contains(root) || into.Count > 250) return;
            into.Add(root);
            foreach (string c in ChilderOf(root)) CollectFamily(c, into);
        }

        private static int GiftsKnown(PlayerProfile p)
        {
            int n = 0;
            for (int i = 0; i < GameRules.GiftSlots; i++) if (i == 0 || (p.Gifts & (1 << i)) != 0) n++;
            return n;
        }

        /// <summary>Lineage changes are rare, so every online client simply gets a fresh view.</summary>
        private void MarkLineageDirty()
        {
            foreach (PlayerEntity p in _players) p.LineageDirty = true;
        }

        private void SetSire(PlayerProfile childe, string sireKey)
        {
            if (childe.Sire.Length > 0) ChilderList(childe.Sire, false).Remove(childe.Key);
            childe.Sire = sireKey;
            childe.Released = false;
            if (sireKey.Length > 0) ChilderList(sireKey, true).Add(childe.Key);
            PlayerEntity online = OnlineByKey(childe.Key);
            if (online != null) InvalidateInfo(online.Id);
            _store.SaveProfile(childe);
            MarkLineageDirty();
        }

        // ------------------------------------------------------------------
        // The Embrace
        // ------------------------------------------------------------------

        private void CmdEmbrace(PlayerEntity p, string name)
        {
            if (name.Length == 0) { SystemMsg(p, "Usage: /embrace <fledgling>. Offer your blood to a fledgling standing beside you."); return; }
            if (p.InTorpor) return;
            string why = CannotSire(p.Profile);
            if (why != null) { SystemMsg(p, why); return; }
            PlayerEntity t = FindOnline(name);
            if (t == null) { SystemMsg(p, "No such Kindred is awake."); return; }
            if (t == p) { SystemMsg(p, "You cannot Embrace yourself."); return; }
            PlayerProfile tp = t.Profile;
            if (tp.Sire.Length > 0) { SystemMsg(p, t.Name + " already has a sire: " + NameOf(tp.Sire) + "."); return; }
            if (!IsFledgling(tp)) { SystemMsg(p, t.Name + " came into their power alone. No one can claim them now."); return; }
            if (t.InTorpor) { SystemMsg(p, t.Name + " lies in torpor."); return; }
            if (p.DistanceTo(t) > GameRules.EmbraceRange) { SystemMsg(p, "Stand beside " + t.Name + " to offer the Embrace."); return; }
            if (p.Blood < GameRules.EmbraceBloodCost + 1f) { SystemMsg(p, "You need more than " + GameRules.EmbraceBloodCost + " blood to share."); return; }
            if (t.EmbraceOfferFrom.Length > 0 && t.EmbraceOfferFrom != p.Profile.Key && _now < t.EmbraceOfferUntil)
            {
                SystemMsg(p, t.Name + " is already weighing another offer.");
                return;
            }

            t.EmbraceOfferFrom = p.Profile.Key;
            t.EmbraceOfferUntil = _now + GameRules.EmbraceOfferSeconds;
            t.LineageDirty = true;
            Notice(p, NoticeLevel.Info, "You offer the Embrace to " + t.Name + ".");
            Notice(t, NoticeLevel.Good, p.Name + " of the " + p.Def.Name + " offers you the Embrace. /accept or /refuse");
            SystemMsg(t, "If you accept, " + p.Name + " becomes your sire and you are remade as one of the " + p.Def.Name + " (" + p.Def.Epithet +
                         "), with their gifts. Your sire will teach you, and will answer for your crimes until they release you.");
        }

        private void CmdAccept(PlayerEntity p)
        {
            if (p.EmbraceOfferFrom.Length == 0 || _now >= p.EmbraceOfferUntil) { SystemMsg(p, "No one is offering you the Embrace."); return; }
            PlayerEntity sire = OnlineByKey(p.EmbraceOfferFrom);
            p.EmbraceOfferFrom = "";
            p.LineageDirty = true;
            if (sire == null) { SystemMsg(p, "Your would-be sire is gone."); return; }
            string why = CannotSire(sire.Profile);
            if (why != null || !IsFledgling(p.Profile)) { SystemMsg(p, "The Embrace can no longer take place."); return; }
            if (p.InTorpor || sire.InTorpor) return;
            if (p.DistanceTo(sire) > GameRules.EmbraceRange + 2f) { SystemMsg(p, "You must be at " + sire.Name + "'s side to accept."); return; }
            if (sire.Blood < GameRules.EmbraceBloodCost) { SystemMsg(p, sire.Name + " has too little blood left to share."); return; }

            StopFeeding(p, false);
            StopFeeding(sire, false);
            bool newLine = p.Profile.Bloodline != sire.Profile.Bloodline;
            if (newLine) ChangeBloodline(p, sire.Profile.Bloodline);
            sire.Blood -= GameRules.EmbraceBloodCost;
            p.Health = p.MaxHealth;
            p.Blood = Math.Min(p.MaxBlood, p.Blood + GameRules.EmbraceBloodCost);
            SetSire(p.Profile, sire.Profile.Key);
            _store.SaveProfile(p.Profile);
            _store.SaveProfile(sire.Profile);

            Notice(p, NoticeLevel.Good, sire.Name + "'s blood floods your veins. You are their childe" + (newLine ? ", one of the " + p.Def.Name + "." : "."));
            SystemMsg(p, "Your innate gift is now " + p.Def.Gifts[0].Name + ". Your sire can teach you " + p.Def.Gifts[1].Name + " and " +
                         p.Def.Gifts[2].Name + " for less standing (/gifts). Use /lin to speak to your family.");
            Notice(sire, NoticeLevel.Good, p.Name + " accepts your blood. You have a childe. (-" + GameRules.EmbraceBloodCost + " blood)");
            BroadcastSystem(sire.Name + " of the " + sire.Def.Name + " has Embraced " + p.Name + ".", null);
            Log(sire.Name + " embraced " + p.Name + ".");
        }

        private void CmdRefuse(PlayerEntity p)
        {
            if (p.EmbraceOfferFrom.Length == 0 || _now >= p.EmbraceOfferUntil) { SystemMsg(p, "No one is offering you the Embrace."); return; }
            PlayerEntity sire = OnlineByKey(p.EmbraceOfferFrom);
            p.EmbraceOfferFrom = "";
            p.LineageDirty = true;
            SystemMsg(p, "You refuse the Embrace.");
            if (sire != null) Notice(sire, NoticeLevel.Warning, p.Name + " refuses your blood.");
        }

        /// <summary>Remakes a fledgling in another bloodline. Fledglings only know their innate gift, so nothing is lost.</summary>
        private void ChangeBloodline(PlayerEntity p, Bloodline b)
        {
            BloodlineDef def = GameData.GetBloodline(b);
            p.Profile.Bloodline = b;
            p.Profile.Gifts = 1;
            p.Def = def;
            p.MaxHealth = def.MaxHealth;
            p.Health = Math.Min(p.Health, p.MaxHealth);
            p.Blood = Math.Min(p.Blood, p.MaxBlood);
            for (int i = 0; i < GameRules.GiftSlots; i++) p.GiftReadyAt[i] = 0;
            p.SurgeUntil = p.IronSkinUntil = p.GlamourUntil = p.MistUntil = p.MajestyUntil = 0;
            InvalidateInfo(p.Id);
        }

        // ------------------------------------------------------------------
        // /lineage
        // ------------------------------------------------------------------

        private void CmdLineage(PlayerEntity p, string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "info";
            string arg = parts.Length > 2 ? parts[2] : "";
            switch (sub)
            {
                case "release": LineageRelease(p, arg); break;
                case "disown": LineageDisown(p, arg); break;
                case "renounce": LineageRenounce(p); break;
                case "accept": CmdAccept(p); break;
                case "refuse": CmdRefuse(p); break;
                default: LineageInfo(p); break;
            }
        }

        private void LineageInfo(PlayerEntity p)
        {
            PlayerProfile pr = p.Profile;
            var sb = new StringBuilder("Lineage of " + pr.Name + ": ");
            switch (StatusOf(pr))
            {
                case LineageStatus.Fledgling:
                    sb.Append("a fledgling. No sire has claimed you; an elder may offer you the Embrace until you learn your second gift.");
                    break;
                case LineageStatus.Unclaimed:
                    sb.Append("unclaimed, you came into your power alone.");
                    break;
                default:
                {
                    sb.Append("childe of ").Append(NameOf(pr.Sire)).Append(pr.Released ? " (released)" : " (in tutelage)");
                    var line = new List<string>();
                    PlayerProfile sire = Prof(pr.Sire);
                    PlayerProfile up = sire != null ? Prof(sire.Sire) : null;
                    for (int guard = 0; up != null && guard < 32; guard++) { line.Add(up.Name); up = Prof(up.Sire); }
                    if (line.Count > 0) sb.Append(", of the line of ").Append(string.Join(" < ", line.ToArray()));
                    sb.Append('.');
                    break;
                }
            }
            SystemMsg(p, sb.ToString());

            List<string> childer = ChilderOf(pr.Key);
            if (childer.Count > 0)
            {
                var names = new List<string>();
                foreach (string c in childer)
                {
                    PlayerProfile cp = Prof(c);
                    if (cp == null) continue;
                    names.Add(cp.Name + " (" + (cp.Released ? "released" : "tutelage, " + GiftsKnown(cp) + "/3 gifts") + (OnlineByKey(c) != null ? ", awake" : "") + ")");
                }
                SystemMsg(p, "Your childer: " + string.Join(", ", names.ToArray()) + ".");
            }
            string why = CannotSire(pr);
            SystemMsg(p, why == null ? "You may /embrace a fledgling beside you." : why);
        }

        private bool FindMyChilde(PlayerEntity p, string name, out PlayerProfile cp)
        {
            cp = Prof(Store.KeyOf(name));
            if (cp == null || cp.Sire != p.Profile.Key) { SystemMsg(p, (name.Length > 0 ? name : "That") + " is not your childe."); return false; }
            return true;
        }

        private void LineageRelease(PlayerEntity p, string name)
        {
            PlayerProfile cp;
            if (!FindMyChilde(p, name, out cp)) return;
            if (cp.Released) { SystemMsg(p, cp.Name + " is already released."); return; }
            if (GiftsKnown(cp) < GameRules.GiftSlots) { SystemMsg(p, cp.Name + " must know all three gifts of the blood before you release them."); return; }
            cp.Released = true;
            p.Standing += GameRules.ReleaseStanding;
            PlayerEntity co = OnlineByKey(cp.Key);
            if (co != null) Notice(co, NoticeLevel.Good, p.Name + " releases you from tutelage. You may sire childer of your own.");
            _store.SaveProfile(cp);
            _store.SaveProfile(p.Profile);
            Notice(p, NoticeLevel.Good, "You release " + cp.Name + " into the night. The court honours your line. (+" + GameRules.ReleaseStanding + " standing)");
            BroadcastSystem(p.Name + " has released their childe " + cp.Name + ".", null);
            MarkLineageDirty();
        }

        private void LineageDisown(PlayerEntity p, string name)
        {
            PlayerProfile cp;
            if (!FindMyChilde(p, name, out cp)) return;
            SetSire(cp, "");
            PlayerEntity co = OnlineByKey(cp.Key);
            if (co != null) Notice(co, NoticeLevel.Danger, p.Name + " has disowned you. You stand alone.");
            Notice(p, NoticeLevel.Warning, "You disown " + cp.Name + ".");
            FamilyBroadcast(p.Profile.Key, p.Name + " has disowned " + cp.Name + ".");
        }

        private void LineageRenounce(PlayerEntity p)
        {
            PlayerProfile pr = p.Profile;
            if (pr.Sire.Length == 0) { SystemMsg(p, "You have no sire to renounce."); return; }
            string sireKey = pr.Sire;
            SetSire(pr, "");
            PlayerEntity so = OnlineByKey(sireKey);
            if (so != null) Notice(so, NoticeLevel.Danger, p.Name + " has renounced you as their sire.");
            Notice(p, NoticeLevel.Warning, "You renounce " + NameOf(sireKey) + ". You stand alone.");
            FamilyBroadcast(sireKey, p.Name + " has renounced their sire, " + NameOf(sireKey) + ".");
        }

        // ------------------------------------------------------------------
        // Ties
        // ------------------------------------------------------------------

        private void LineageChat(PlayerEntity p, string text)
        {
            string root = RootOf(p.Profile.Key);
            if (root == p.Profile.Key && ChilderOf(root).Count == 0) { SystemMsg(p, "You have no kin. A sire or childe shares your lineage channel."); return; }
            byte[] data = SimpleMsg.ChatMsg(ChatChannel.Lineage, p.Name, text);
            foreach (PlayerEntity o in _players) if (RootOf(o.Profile.Key) == root) o.Session.Send(data);
        }

        private void FamilyBroadcast(string memberKey, string text)
        {
            string root = RootOf(memberKey);
            byte[] data = SimpleMsg.ChatMsg(ChatChannel.Lineage, "", text);
            foreach (PlayerEntity o in _players) if (RootOf(o.Profile.Key) == root) o.Session.Send(data);
        }

        /// <summary>The blood bond: sire and childe close together mend faster and take less harm.</summary>
        private void UpdateBond(PlayerEntity p)
        {
            PlayerEntity with = null;
            if (!p.InTorpor && (p.Profile.Sire.Length > 0 || ChilderOf(p.Profile.Key).Count > 0))
                foreach (PlayerEntity o in _players)
                    if (!o.InTorpor && IsDirectKin(p, o) && p.DistanceTo(o) <= GameRules.BondRange) { with = o; break; }
            if (with != null && !p.Bonded && _now >= p.BondNoticeAt)
            {
                p.BondNoticeAt = _now + 60;
                string rel = with.Profile.Key == p.Profile.Sire ? "your sire" : "your childe";
                Notice(p, NoticeLevel.Good, "The blood calls: " + rel + " " + with.Name + " is near.");
            }
            p.Bonded = with != null;
        }

        /// <summary>A sire answers for the crimes of a childe in tutelage.</summary>
        private void Shame(PlayerEntity childe, int amount, string deed)
        {
            PlayerProfile cp = childe.Profile;
            if (!InTutelage(cp)) return;
            PlayerProfile sp = Prof(cp.Sire);
            if (sp == null) return;
            int lost = Math.Min(amount, sp.Standing);
            sp.Standing -= lost;
            PlayerEntity so = OnlineByKey(sp.Key);
            if (so != null) Notice(so, NoticeLevel.Warning, "Your childe " + childe.Name + " " + deed + ". Their shame is yours. (-" + lost + " standing)");
            else _store.SaveProfile(sp);
            Notice(childe, NoticeLevel.Warning, "Your sire " + sp.Name + " answers for this before the court.");
        }

        /// <summary>Sire and childe feel each other fall.</summary>
        private void FeelTorpor(PlayerEntity victim, PlayerEntity killer)
        {
            foreach (PlayerEntity o in _players)
            {
                if (!IsDirectKin(victim, o)) continue;
                string rel = o.Profile.Sire == victim.Profile.Key ? "your sire" : "your childe";
                string where = GameData.Districts[GameData.DistrictAt(victim.X, victim.Z)].Name;
                Notice(o, NoticeLevel.Danger, "You feel " + rel + " " + victim.Name + " fall into torpor in " + where +
                                              (killer != null && killer != o ? ", at the hands of " + killer.Name + "." : "."));
            }
        }

        /// <summary>Standing cost of a gift for this vampire, and whether their sire is teaching it.</summary>
        private int GiftCostFor(PlayerEntity p, GiftDef gift, out PlayerProfile teacher)
        {
            teacher = null;
            PlayerProfile sp = InTutelage(p.Profile) ? Prof(p.Profile.Sire) : null;
            if (sp != null && sp.Bloodline == p.Profile.Bloodline && (sp.Gifts & (1 << gift.Slot)) != 0)
            {
                teacher = sp;
                return GameRules.TaughtGiftStandingCost[gift.Slot];
            }
            return gift.StandingCost;
        }

        // ------------------------------------------------------------------
        // Replication
        // ------------------------------------------------------------------

        private KinInfo KinOf(PlayerProfile k)
        {
            return new KinInfo { Name = k.Name, Online = OnlineByKey(k.Key) != null, Released = k.Released, GiftsKnown = (byte)GiftsKnown(k) };
        }

        private void SendLineage(PlayerEntity p)
        {
            p.LineageDirty = false;
            PlayerProfile pr = p.Profile;
            if (p.EmbraceOfferFrom.Length > 0 && (_now >= p.EmbraceOfferUntil || OnlineByKey(p.EmbraceOfferFrom) == null)) p.EmbraceOfferFrom = "";

            var m = new LineageMsg { Status = StatusOf(pr), CanSire = CannotSire(pr) == null };
            PlayerProfile sire = Prof(pr.Sire);
            if (sire != null)
            {
                m.Sire = KinOf(sire);
                m.Sire.Released = pr.Released;
                var anc = new List<string>();
                PlayerProfile up = Prof(sire.Sire);
                for (int guard = 0; up != null && guard < 32; guard++) { anc.Add(up.Name); up = Prof(up.Sire); }
                m.Ancestors = anc.ToArray();
                var sib = new List<string>();
                foreach (string c in ChilderOf(sire.Key)) if (c != pr.Key) sib.Add(NameOf(c));
                m.Siblings = sib.ToArray();
            }
            var childer = new List<KinInfo>();
            foreach (string c in ChilderOf(pr.Key)) { PlayerProfile cp = Prof(c); if (cp != null) childer.Add(KinOf(cp)); }
            m.Childer = childer.ToArray();

            var family = new List<string>();
            CollectFamily(RootOf(pr.Key), family);
            if (family.Count > 1)
            {
                var names = new string[family.Count];
                for (int i = 0; i < family.Count; i++)
                {
                    names[i] = NameOf(family[i]);
                    if (OnlineByKey(family[i]) != null) m.FamilyOnline++;
                }
                m.Family = names;
            }

            PlayerEntity offer = p.EmbraceOfferFrom.Length > 0 ? OnlineByKey(p.EmbraceOfferFrom) : null;
            if (offer != null)
            {
                m.OfferFrom = offer.Name;
                m.OfferBloodline = offer.Profile.Bloodline;
                m.OfferSeconds = (float)Math.Max(0, p.EmbraceOfferUntil - _now);
            }
            p.Session.Send(m.Write());
        }
    }
}
