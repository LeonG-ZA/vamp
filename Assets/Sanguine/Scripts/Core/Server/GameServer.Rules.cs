using System;
using System.Collections.Generic;

namespace Sanguine.Server
{
    // Core vampire rules: blood, feeding, the Hush (secrecy), conscience, combat, powers, sunlight, torpor.
    public sealed partial class GameServer
    {
        private double CycleLength { get { return _cfg.NightSeconds + _cfg.DaySeconds; } }
        private bool IsDay { get { return _cycleTime >= _cfg.NightSeconds; } }

        private bool IsRegent(PlayerEntity p)
        {
            return _world.Regent.Length > 0 && Store.KeyOf(_world.Regent) == p.Profile.Key;
        }

        /// <summary>Marked vampires may be destroyed by anyone without penalty.</summary>
        private bool IsMarked(PlayerEntity p)
        {
            return p.Exposure >= GameRules.ExposureMarkedThreshold
                || p.Conscience <= GameRules.ConscienceBeastThreshold
                || _now < p.OutlawUntil
                || _now < p.BloodHuntUntil;
        }

        private ZoneKind ZoneKindAt(float x, float z)
        {
            ZoneDef zd = _city.ZoneAt(x, z);
            return zd == null ? ZoneKind.None : zd.Kind;
        }

        private bool SameCircle(PlayerEntity a, PlayerEntity b)
        {
            return a.Profile.Circle.Length > 0 && Store.KeyOf(a.Profile.Circle) == Store.KeyOf(b.Profile.Circle);
        }

        // ------------------------------------------------------------------
        // Night / day cycle
        // ------------------------------------------------------------------

        private void UpdateCycle(float dt)
        {
            _cycleTime += dt;
            if (_cycleTime >= CycleLength)
            {
                _cycleTime -= CycleLength;
                _world.Night++;
                _regentHuntUsed = false;
                BroadcastNotice(NoticeLevel.Good, "Night " + _world.Night + " falls over Sanguine City. The hunt begins.");
            }
            bool day = IsDay;
            if (day && !_wasDay) OnDawn();
            _wasDay = day;
        }

        /// <summary>Forces the next tick to be dawn (debug / tests).</summary>
        private void ForceDawn()
        {
            _cycleTime = _cfg.NightSeconds;
        }

        private void OnDawn()
        {
            BroadcastNotice(NoticeLevel.Danger, "Dawn breaks! Seek your haven or a sanctum - the sun burns.");

            // Election: the candidate with the most standing-weighted votes becomes Regent.
            var tally = Tally();
            if (tally.Count > 0)
            {
                string winnerKey = null;
                int best = -1;
                foreach (var kv in tally)
                {
                    PlayerProfile prof;
                    _profiles.TryGetValue(kv.Key, out prof);
                    int standing = prof != null ? prof.Standing : 0;
                    PlayerProfile bestProf = null;
                    if (winnerKey != null) _profiles.TryGetValue(winnerKey, out bestProf);
                    int bestStanding = bestProf != null ? bestProf.Standing : 0;
                    if (kv.Value > best || (kv.Value == best && standing > bestStanding) ||
                        (kv.Value == best && standing == bestStanding && string.CompareOrdinal(kv.Key, winnerKey) < 0))
                    {
                        best = kv.Value;
                        winnerKey = kv.Key;
                    }
                }
                PlayerProfile winner;
                if (winnerKey != null && _profiles.TryGetValue(winnerKey, out winner))
                {
                    bool changed = Store.KeyOf(_world.Regent) != winnerKey;
                    _world.Regent = winner.Name;
                    if (changed) _world.Edict = "";
                    BroadcastSystem("The court has spoken: " + winner.Name + " is Regent of Sanguine City (" + best + " weighted votes).", null);
                }
                _votes.Clear();
            }

            // District control pays out standing; influence fades by 10%.
            for (int d = 0; d < 4; d++)
            {
                string ctrl = DistrictController(d);
                if (ctrl.Length > 0)
                {
                    Circle c = FindCircle(ctrl);
                    if (c != null)
                    {
                        foreach (string mk in c.Members)
                        {
                            PlayerProfile mp;
                            if (_profiles.TryGetValue(mk, out mp)) mp.Standing += 1;
                        }
                    }
                }
                var keys = new List<string>(_world.Influence[d].Keys);
                foreach (string k in keys)
                {
                    int v = _world.Influence[d][k] * 9 / 10;
                    if (v <= 0) _world.Influence[d].Remove(k); else _world.Influence[d][k] = v;
                }
            }
            _store.SaveWorld(_world);
        }

        // ------------------------------------------------------------------
        // Per-player upkeep
        // ------------------------------------------------------------------

        private void UpdatePlayers(float dt)
        {
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                PlayerEntity p = _players[i];

                if (_now >= p.NextBondCheck) { p.NextBondCheck = _now + 0.5; UpdateBond(p); }

                if (p.InTorpor)
                {
                    if (_now >= p.TorporUntil) Respawn(p);
                    continue;
                }

                // Exposure decays when the vampire has kept a low profile for a few seconds.
                if (_now - p.LastExposureGain > 5 && p.Exposure > 0)
                    p.Exposure = Math.Max(0, p.Exposure - GameRules.ExposureDecayPerSecond * dt);

                // Blood slowly fades; halved in a district your circle controls.
                if (_now >= p.NextBloodDecay)
                {
                    int d = GameData.DistrictAt(p.X, p.Z);
                    bool controlled = p.Profile.Circle.Length > 0 && Store.KeyOf(DistrictController(d)) == Store.KeyOf(p.Profile.Circle);
                    p.NextBloodDecay = _now + GameRules.BloodDecaySeconds * (controlled ? 2 : 1);
                    if (p.FeedTarget == 0 && p.Blood > 0) p.Blood = Math.Max(0, p.Blood - 1);
                }

                // Starvation.
                if (p.Blood <= 0 && _now >= p.NextStarve)
                {
                    p.NextStarve = _now + GameRules.StarvingDamageInterval;
                    Notice(p, NoticeLevel.Danger, "The Hunger gnaws at you. Feed!");
                    Damage(p, 1f, null);
                    if (p.InTorpor) continue;
                }

                // Sunlight.
                ZoneKind zone = ZoneKindAt(p.X, p.Z);
                if (IsDay && zone == ZoneKind.None && _now >= p.NextSun)
                {
                    p.NextSun = _now + 2.0;
                    Notice(p, NoticeLevel.Danger, "The sun sears your flesh!");
                    Damage(p, 1f, null);
                    if (p.InTorpor) continue;
                }

                // Mending: spend blood to heal when out of combat.
                if (p.Health < p.MaxHealth && p.Blood > 2f && _now - p.LastCombat > 4 && _now >= p.NextMend)
                {
                    p.NextMend = _now + (p.Bonded ? GameRules.BondMendInterval : 2.0);
                    p.Blood -= 0.5f;
                    p.Health = Math.Min(p.MaxHealth, p.Health + 1f);
                }

                UpdateFeeding(p, dt);

                // The Hush: exposure attracts hunters.
                if (_now >= p.NextHunterCheck)
                {
                    p.NextHunterCheck = _now + 4.0;
                    MaybeSpawnHunters(p);
                }
            }
        }

        private void Respawn(PlayerEntity p)
        {
            p.TorporUntil = -1;
            p.Health = p.MaxHealth;
            p.Blood = Math.Max(p.Blood, 3f);
            p.Exposure = 0;
            p.SurgeUntil = p.SlowUntil = p.RootUntil = 0;
            p.IronSkinUntil = p.GlamourUntil = p.MistUntil = p.MajestyUntil = p.SilencedUntil = 0;
            float x, z;
            CityLayout.SpawnPoint(_city.HavenFor(p.Profile.Bloodline), out x, out z);
            TeleportPlayer(p, x, z);
            Notice(p, NoticeLevel.Info, "You awaken from torpor in your haven.");
        }

        // ------------------------------------------------------------------
        // Exposure and witnesses
        // ------------------------------------------------------------------

        /// <summary>Counts and alarms mortals who can see the given spot.</summary>
        private int AlarmWitnesses(float x, float z, Entity exclude, float range)
        {
            int n = 0;
            foreach (var m in _mortals)
            {
                if (m == exclude || m.Mode == MortalMode.Dead || m.Mode == MortalMode.Charmed || m.Mode == MortalMode.Entranced) continue;
                if (Entity.Dist(m.X, m.Z, x, z) > range) continue;
                n++;
                Alarm(m, x, z);
            }
            return n;
        }

        private void AddExposure(PlayerEntity p, float amount, string reason)
        {
            if (amount <= 0) return;
            if (_now < p.GlamourUntil) return; // Glamour: witnesses do not remember the face
            float before = p.Exposure;
            p.Exposure = Math.Min(100f, p.Exposure + amount);
            p.LastExposureGain = _now;
            if (before < GameRules.ExposureHunterThreshold && p.Exposure >= GameRules.ExposureHunterThreshold)
                Notice(p, NoticeLevel.Warning, "The Hush is breaking (" + reason + "). Hunters are taking notice.");
            if (before < GameRules.ExposureMarkedThreshold && p.Exposure >= GameRules.ExposureMarkedThreshold)
            {
                Notice(p, NoticeLevel.Danger, "You are MARKED. Any Kindred may destroy you without penalty.");
                BroadcastSystem(p.Name + " has broken the Hush and is marked for destruction.", p);
                Shame(p, GameRules.ShameBrokeHush, "has broken the Hush");
            }
        }

        private void LoseConscience(PlayerEntity p, float amount, string reason)
        {
            float before = p.Conscience;
            p.Conscience = Math.Max(0f, p.Conscience - amount);
            Notice(p, NoticeLevel.Danger, "Your conscience darkens: " + reason + " (" + p.Conscience.ToString("0.0") + ")");
            if (before > GameRules.ConscienceBeastThreshold && p.Conscience <= GameRules.ConscienceBeastThreshold)
                SystemMsg(p, "The Beast has the upper hand. Mortals flee from you and you are marked until you regain your conscience.");
        }

        // ------------------------------------------------------------------
        // Feeding
        // ------------------------------------------------------------------

        private void StartFeeding(PlayerEntity p, int targetId)
        {
            if (p.InTorpor || p.FeedTarget != 0) return;
            if (_now < p.MistUntil) { Notice(p, NoticeLevel.Info, "You have no lips while you are mist."); return; }
            var m = Get<Mortal>(targetId);
            if (m == null || m.Mode == MortalMode.Dead) { Notice(p, NoticeLevel.Info, "No living vessel there."); return; }
            if (p.DistanceTo(m) > GameRules.FeedRange) { Notice(p, NoticeLevel.Info, "Get closer to feed."); return; }
            if (ZoneKindAt(p.X, p.Z) == ZoneKind.Sanctum) { Notice(p, NoticeLevel.Warning, "Feeding is forbidden in a sanctum."); return; }
            if (m.Mode == MortalMode.Fleeing) { Notice(p, NoticeLevel.Info, "The mortal is too alarmed. Calm them first."); return; }
            if (m.FedUponBy != 0) { Notice(p, NoticeLevel.Info, "Another Kindred is already feeding there."); return; }
            if (p.Blood >= p.MaxBlood) { Notice(p, NoticeLevel.Info, "You are sated."); return; }

            p.FeedTarget = m.Id;
            p.FeedStartX = p.X; p.FeedStartZ = p.Z;
            p.FedThisFeeding = 0;
            p.WarnedFaltering = false;
            m.Mode = MortalMode.Entranced;
            m.FedUponBy = p.Id;
            Notice(p, NoticeLevel.Info, "You sink your fangs into the " + m.Name + "...");

            // Being seen starting the kiss is the riskiest moment.
            int w = AlarmWitnesses(m.X, m.Z, m, GameRules.WitnessRange);
            if (w > 0) AddExposure(p, Math.Min(30f, 8f * w), w + " witness" + (w > 1 ? "es" : ""));
        }

        private void UpdateFeeding(PlayerEntity p, float dt)
        {
            if (p.FeedTarget == 0) return;
            var m = Get<Mortal>(p.FeedTarget);
            if (m == null || m.Mode == MortalMode.Dead || p.DistanceTo(m) > GameRules.FeedRange + 1f) { StopFeeding(p, false); return; }

            float amount = Math.Min(GameRules.FeedRatePerSecond * dt, m.Blood);
            m.Blood -= amount;
            p.Blood = Math.Min(p.MaxBlood, p.Blood + amount);
            p.FedThisFeeding += amount;

            // Ongoing witnesses keep raising exposure.
            int w = AlarmWitnesses(m.X, m.Z, m, GameRules.WitnessRange);
            if (w > 0) AddExposure(p, Math.Min(25f, 5f * w) * dt, "seen feeding");

            if (!p.WarnedFaltering && m.Blood <= 2f)
            {
                p.WarnedFaltering = true;
                Notice(p, NoticeLevel.Warning, "Their heartbeat falters... release them (F) or they will die.");
            }

            if (m.Blood <= 0.001f)
            {
                KillMortal(m, p, "drained");
                p.FeedTarget = 0;
                return;
            }
            if (p.Blood >= p.MaxBlood - 0.001f)
            {
                Notice(p, NoticeLevel.Good, "You are sated.");
                StopFeeding(p, true);
            }
        }

        private void StopFeeding(PlayerEntity p, bool voluntary)
        {
            if (p.FeedTarget == 0) return;
            var m = Get<Mortal>(p.FeedTarget);
            p.FeedTarget = 0;
            if (m == null) return;
            m.FedUponBy = 0;
            if (m.Mode == MortalMode.Dead) return;
            m.Mode = MortalMode.Walking;     // dazed, remembers nothing
            p.Profile.Feedings++;
            if (voluntary && p.FedThisFeeding >= 2.5f && m.Blood >= 1f)
            {
                // Restraint slowly restores conscience.
                float before = p.Conscience;
                p.Conscience = Math.Min(10f, p.Conscience + 0.25f);
                if (p.Conscience > before) Notice(p, NoticeLevel.Good, "You show restraint. The mortal stumbles away, dazed but alive.");
            }
        }

        // ------------------------------------------------------------------
        // Combat
        // ------------------------------------------------------------------

        private string CanHarm(PlayerEntity attacker, Entity target)
        {
            if (attacker.InTorpor) return "You are in torpor.";
            if (target == null || target.IsDown) return "No valid target.";
            if (target == attacker) return "You cannot target yourself.";
            if (ZoneKindAt(attacker.X, attacker.Z) == ZoneKind.Sanctum || ZoneKindAt(target.X, target.Z) == ZoneKind.Sanctum)
                return "Violence is forbidden in a sanctum.";
            if (_now < attacker.MistUntil) return "You are mist and cannot strike.";
            var tp = target as PlayerEntity;
            if (tp != null)
            {
                if (_now < tp.MistUntil) return "Your blow passes through crimson mist.";
                if (ZoneKindAt(tp.X, tp.Z) == ZoneKind.Haven) return "They are safe within a haven.";
                if (ZoneKindAt(attacker.X, attacker.Z) == ZoneKind.Haven) return "You cannot attack from within a haven.";
                if (SameCircle(attacker, tp)) return "They are of your circle.";
                if (IsDirectKin(attacker, tp))
                    return "Your blood will not let you raise a hand against your " + (attacker.Profile.Sire == tp.Profile.Key ? "sire." : "childe.");
                if (_now < tp.MajestyUntil) return "You cannot bring yourself to strike " + tp.Name + ".";
            }
            return null;
        }

        private void Attack(PlayerEntity p, int targetId)
        {
            if (_now < p.AttackReadyAt) return;
            Entity t;
            _entities.TryGetValue(targetId, out t);
            string err = CanHarm(p, t);
            if (err != null) { Notice(p, NoticeLevel.Info, err); return; }
            if (p.DistanceTo(t) > GameRules.MeleeRange) { Notice(p, NoticeLevel.Info, "Out of reach."); return; }
            if (p.FeedTarget != 0) StopFeeding(p, false);

            p.AttackReadyAt = _now + GameRules.MeleeCooldown;
            p.LastCombat = _now;
            float dmg = p.Def.MeleeDamage * (_now < p.SurgeUntil ? 2f : 1f);

            if (t.Kind != EntityKind.Hunter)
            {
                int w = AlarmWitnesses(t.X, t.Z, t, GameRules.WitnessRange);
                if (w > 0) AddExposure(p, Math.Min(15f, 3f * w), "violence in public");
            }
            Damage(t, dmg, p);
        }

        // ------------------------------------------------------------------
        // Gifts
        // ------------------------------------------------------------------

        private bool IsIronSkinned(PlayerEntity p) { return _now < p.IronSkinUntil; }

        /// <summary>Crowd control on a vampire; Iron Skin shrugs it off.</summary>
        private bool Root(PlayerEntity p, float seconds, string by)
        {
            if (IsIronSkinned(p)) { Notice(p, NoticeLevel.Info, "Your iron flesh will not kneel to " + by + "."); return false; }
            p.RootUntil = Math.Max(p.RootUntil, _now + seconds);
            return true;
        }

        private bool Slow(PlayerEntity p, float seconds, string by)
        {
            if (IsIronSkinned(p)) return false;
            p.SlowUntil = Math.Max(p.SlowUntil, _now + seconds);
            Notice(p, NoticeLevel.Warning, by + " clouds your limbs.");
            return true;
        }

        private bool Stun(PlayerEntity p, float seconds)
        {
            if (IsIronSkinned(p)) return false;
            p.StunnedUntil = Math.Max(p.StunnedUntil, _now + seconds);
            return true;
        }

        /// <summary>Area gifts affect rival vampires: not yourself, your circle, or anyone in a safe zone or in mist.</summary>
        private bool IsRivalInReach(PlayerEntity caster, PlayerEntity other, float radius)
        {
            return other != caster && !other.InTorpor && !SameCircle(caster, other) && !IsDirectKin(caster, other) && _now >= other.MistUntil &&
                   ZoneKindAt(other.X, other.Z) == ZoneKind.None && caster.DistanceTo(other) <= radius;
        }

        private void UsePower(PlayerEntity p, int slot, int targetId)
        {
            if (p.InTorpor || slot < 0 || slot >= GameRules.GiftSlots) return;
            GiftDef gift = p.Def.Gifts[slot];
            if (!p.HasGift(slot)) { Notice(p, NoticeLevel.Info, "You have not learned " + gift.Name + ". (/learn " + (slot + 1) + ", " + gift.StandingCost + " standing)"); return; }
            if (_now < p.SilencedUntil) { Notice(p, NoticeLevel.Warning, "Your blood is silenced; your gifts will not answer."); return; }
            if (_now < p.MistUntil) { Notice(p, NoticeLevel.Info, "You are mist; your gifts must wait."); return; }
            if (_now < p.GiftReadyAt[slot]) { Notice(p, NoticeLevel.Info, gift.Name + " is not ready."); return; }
            if (p.Blood < gift.Cost) { Notice(p, NoticeLevel.Warning, "Not enough blood for " + gift.Name + "."); return; }

            Entity t = null;
            if (gift.Target == PowerTarget.Other)
            {
                _entities.TryGetValue(targetId, out t);
                string err = CanHarm(p, t);
                if (err != null) { Notice(p, NoticeLevel.Info, err); return; }
                if (p.DistanceTo(t) > gift.Range) { Notice(p, NoticeLevel.Info, "Out of range (" + gift.Range + "m)."); return; }
            }
            if (ZoneKindAt(p.X, p.Z) == ZoneKind.Sanctum && (gift.Id == GiftId.Earthshaker || gift.Id == GiftId.Rapture))
            {
                Notice(p, NoticeLevel.Info, "Violence is forbidden in a sanctum.");
                return;
            }

            p.Blood -= gift.Cost;
            p.GiftReadyAt[slot] = _now + gift.Cooldown;
            p.LastCombat = _now;

            // Rapture enthrals the crowd first, so the entranced do not count as witnesses.
            if (gift.Id == GiftId.Rapture) CastRapture(p, gift);

            // Overt use of the Blood breaches the Hush if mortals see it; subtle gifts do not.
            if (!gift.Subtle)
            {
                int w = AlarmWitnesses(p.X, p.Z, t, GameRules.WitnessRange);
                if (w > 0) AddExposure(p, Math.Min(25f, 8f * w), "overt use of the Blood");
            }

            var tm = t as Mortal;
            var tp = t as PlayerEntity;
            var th = t as Hunter;
            switch (gift.Id)
            {
                case GiftId.Surge:
                    p.SurgeUntil = _now + gift.Duration;
                    Notice(p, NoticeLevel.Good, "Blood surges through your limbs!");
                    break;

                case GiftId.IronSkin:
                    p.IronSkinUntil = _now + gift.Duration;
                    p.RootUntil = p.SlowUntil = p.StunnedUntil = 0;
                    Notice(p, NoticeLevel.Good, "Your flesh hardens like cast iron.");
                    break;

                case GiftId.Earthshaker:
                {
                    int hit = 0;
                    foreach (PlayerEntity o in _players.ToArray())
                    {
                        if (!IsRivalInReach(p, o, gift.Range)) continue;
                        Stun(o, 1.5f);
                        Damage(o, 3f, p);
                        hit++;
                    }
                    foreach (Hunter h in _hunters.ToArray())
                    {
                        if (p.DistanceTo(h) > gift.Range) continue;
                        h.StunnedUntil = _now + 2f;
                        Damage(h, 3f, p);
                        hit++;
                    }
                    foreach (Mortal m in _mortals)
                        if (m.Mode != MortalMode.Dead && p.DistanceTo(m) <= gift.Range * 2f) Alarm(m, p.X, p.Z);
                    Notice(p, NoticeLevel.Good, "The street cracks beneath your fist." + (hit > 0 ? " (" + hit + " struck)" : ""));
                    break;
                }

                case GiftId.Allure:
                    if (tm != null)
                    {
                        if (tm.FedUponBy != 0) break;
                        Charm(tm, p, 15);
                        Notice(p, NoticeLevel.Good, "The " + tm.Name + " is enthralled and drifts toward you.");
                    }
                    else if (tp != null) Slow(tp, 4, p.Name + "'s Allure");
                    else if (th != null) th.StunnedUntil = _now + 3;
                    break;

                case GiftId.Glamour:
                    p.GlamourUntil = _now + gift.Duration;
                    Notice(p, NoticeLevel.Good, "Eyes slide off you. For now, no one remembers your face.");
                    break;

                case GiftId.Rapture:
                    break; // applied above

                case GiftId.BloodLash:
                    Notice(p, NoticeLevel.Good, "A lash of blood strikes your target.");
                    Damage(t, 3f, p);
                    break;

                case GiftId.Siphon:
                    CastSiphon(p, t);
                    break;

                case GiftId.CrimsonMist:
                {
                    p.MistUntil = _now + gift.Duration;
                    StopFeeding(p, false);
                    int lost = 0;
                    foreach (Hunter h in _hunters.ToArray()) if (h.TargetPlayer == p.Id) { RemoveHunter(h); lost++; }
                    Notice(p, NoticeLevel.Good, "You dissolve into crimson mist." + (lost > 0 ? " The hunters lose your trail." : ""));
                    break;
                }

                case GiftId.Command:
                    if (tm != null)
                    {
                        tm.Mode = MortalMode.Walking;
                        p.Exposure = Math.Max(0f, p.Exposure - 15f);
                        Notice(p, NoticeLevel.Good, "\"You saw nothing.\" The " + tm.Name + " forgets. (-15 exposure)");
                    }
                    else if (tp != null) { if (Root(tp, 2, p.Name)) Notice(tp, NoticeLevel.Warning, p.Name + " Commands you: \"Kneel.\""); }
                    else if (th != null) th.StunnedUntil = _now + 4;
                    break;

                case GiftId.Majesty:
                    p.MajestyUntil = _now + gift.Duration;
                    Notice(p, NoticeLevel.Good, "Your Majesty fills the street. None dare raise a hand to you.");
                    break;

                case GiftId.Dominate:
                    CastDominate(p, t, gift);
                    break;
            }
        }

        private void Charm(Mortal m, PlayerEntity by, double seconds)
        {
            m.Mode = MortalMode.Charmed;
            m.CharmedBy = by.Id;
            m.ModeUntil = _now + seconds;
        }

        private void CastRapture(PlayerEntity p, GiftDef gift)
        {
            int charmed = 0;
            foreach (Mortal m in _mortals)
            {
                if (m.Mode == MortalMode.Dead || m.FedUponBy != 0 || p.DistanceTo(m) > gift.Range) continue;
                Charm(m, p, 12);
                charmed++;
            }
            foreach (Hunter h in _hunters) if (p.DistanceTo(h) <= gift.Range) h.StunnedUntil = _now + 3;
            foreach (PlayerEntity o in _players) if (IsRivalInReach(p, o, 10f)) Slow(o, 4, p.Name + "'s Rapture");
            Notice(p, NoticeLevel.Good, "Rapture! " + charmed + " mortal" + (charmed == 1 ? " is" : "s are") + " yours.");
        }

        private void CastSiphon(PlayerEntity p, Entity t)
        {
            float room = Math.Max(0f, p.MaxBlood - p.Blood);
            float drawn = 0f;
            var tm = t as Mortal;
            var tp = t as PlayerEntity;
            var th = t as Hunter;
            if (tm != null)
            {
                drawn = Math.Min(2f, tm.Blood);
                tm.Blood -= drawn;
                if (tm.Blood <= 0.001f) KillMortal(tm, p, "drained from afar");
                else Alarm(tm, p.X, p.Z);
            }
            else if (tp != null)
            {
                drawn = Math.Min(2f, tp.Blood);
                tp.Blood -= drawn;
                tp.LastCombat = _now;
                Notice(tp, NoticeLevel.Warning, p.Name + " draws the blood out of your veins!");
            }
            else if (th != null)
            {
                drawn = 1f;
                Damage(th, 2f, p);
            }
            p.Blood = Math.Min(p.MaxBlood, p.Blood + Math.Min(drawn, room));
            Notice(p, NoticeLevel.Good, "Blood streams through the air into you. (+" + Math.Min(drawn, room).ToString("0.#") + ")");
        }

        private void CastDominate(PlayerEntity p, Entity t, GiftDef gift)
        {
            var tm = t as Mortal;
            var tp = t as PlayerEntity;
            var th = t as Hunter;
            if (th != null)
            {
                RemoveHunter(th);
                Notice(p, NoticeLevel.Good, "\"Go home.\" The hunter lowers his blade and walks away.");
            }
            else if (tp != null)
            {
                StopFeeding(tp, false);
                Root(tp, 3, p.Name);
                tp.SilencedUntil = _now + gift.Duration;
                Notice(tp, NoticeLevel.Danger, p.Name + " Dominates you. Your blood falls silent.");
                Notice(p, NoticeLevel.Good, tp.Name + " bends to your will.");
            }
            else if (tm != null)
            {
                int calmed = 0;
                foreach (Mortal m in _mortals)
                {
                    if (m.Mode == MortalMode.Dead || Entity.Dist(m.X, m.Z, tm.X, tm.Z) > 10f) continue;
                    if (m.Mode == MortalMode.Fleeing) m.Mode = MortalMode.Walking;
                    calmed++;
                }
                p.Exposure = Math.Max(0f, p.Exposure - 30f);
                Notice(p, NoticeLevel.Good, "Through the " + tm.Name + ", " + calmed + " witness" + (calmed == 1 ? "" : "es") + " forget. (-30 exposure)");
            }
        }

        private void Damage(Entity target, float amount, PlayerEntity source)
        {
            if (target.IsDown) return;
            var tp = target as PlayerEntity;
            if (tp != null && IsIronSkinned(tp)) amount *= 0.5f;
            if (tp != null && tp.Bonded) amount *= GameRules.BondDamageTaken;
            target.Health -= amount;

            if (tp != null)
            {
                tp.LastCombat = _now;
                if (tp.FeedTarget != 0) StopFeeding(tp, false);
                if (source != null) Notice(tp, NoticeLevel.Warning, source.Name + " strikes you!");
            }

            var tm = target as Mortal;
            if (tm != null && source != null && tm.Mode != MortalMode.Dead) Alarm(tm, source.X, source.Z);

            if (target.Health > 0) return;
            target.Health = 0;

            if (tm != null) { KillMortal(tm, source, "slain"); return; }
            var th = target as Hunter;
            if (th != null)
            {
                if (source != null) { source.Standing += 1; Notice(source, NoticeLevel.Good, "You destroyed a hunter. (+1 standing)"); }
                RemoveHunter(th);
                return;
            }
            if (tp != null) EnterTorpor(tp, source);
        }

        private void KillMortal(Mortal m, PlayerEntity killer, string how)
        {
            m.Mode = MortalMode.Dead;
            m.Health = 0;
            m.FedUponBy = 0;
            m.RespawnAt = _now + 60;
            if (killer == null) return;
            if (killer.FeedTarget == m.Id) killer.FeedTarget = 0;
            killer.Profile.Kills++;
            LoseConscience(killer, 1f, "a mortal " + how + " by your hand");
            Shame(killer, GameRules.ShameMortalKill, "has killed a mortal");
            int w = AlarmWitnesses(m.X, m.Z, m, GameRules.WitnessRange);
            AddExposure(killer, 20f + 10f * w, "a body in the street");
        }

        private void EnterTorpor(PlayerEntity victim, PlayerEntity killer)
        {
            bool victimMarked = IsMarked(victim);
            StopFeeding(victim, false);
            victim.TorporUntil = _now + GameRules.TorporSeconds;
            victim.Health = 0;
            victim.Blood = Math.Max(0, victim.Blood - 2f);
            victim.Standing = Math.Max(0, victim.Standing - 1);
            Notice(victim, NoticeLevel.Danger, "You fall into torpor...");
            foreach (var h in _hunters) if (h.TargetPlayer == victim.Id) h.TargetPlayer = 0;
            FeelTorpor(victim, killer);

            if (killer == null) { BroadcastSystem(victim.Name + " has fallen into torpor.", null); return; }

            if (victimMarked)
            {
                killer.Standing += 3;
                Notice(killer, NoticeLevel.Good, "Justice is served on " + victim.Name + ". (+3 standing)");
                BroadcastSystem(killer.Name + " has brought the marked " + victim.Name + " to justice.", null);
            }
            else
            {
                killer.OutlawUntil = _now + 180;
                killer.Standing = Math.Max(0, killer.Standing - 5);
                Notice(killer, NoticeLevel.Danger, "You struck down " + victim.Name + " without cause. You are an OUTLAW for 3 minutes. (-5 standing)");
                BroadcastSystem(killer.Name + " has struck down " + victim.Name + " and is declared outlaw!", null);
                Shame(killer, GameRules.ShameOutlaw, "is an outlaw for murder");
            }
        }
    }
}
