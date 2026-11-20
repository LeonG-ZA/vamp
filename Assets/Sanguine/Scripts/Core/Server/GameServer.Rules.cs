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
                    p.NextMend = _now + 2.0;
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
            float before = p.Exposure;
            p.Exposure = Math.Min(100f, p.Exposure + amount);
            p.LastExposureGain = _now;
            if (before < GameRules.ExposureHunterThreshold && p.Exposure >= GameRules.ExposureHunterThreshold)
                Notice(p, NoticeLevel.Warning, "The Hush is breaking (" + reason + "). Hunters are taking notice.");
            if (before < GameRules.ExposureMarkedThreshold && p.Exposure >= GameRules.ExposureMarkedThreshold)
            {
                Notice(p, NoticeLevel.Danger, "You are MARKED. Any Kindred may destroy you without penalty.");
                BroadcastSystem(p.Name + " has broken the Hush and is marked for destruction.", p);
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
            var tp = target as PlayerEntity;
            if (tp != null)
            {
                if (ZoneKindAt(tp.X, tp.Z) == ZoneKind.Haven) return "They are safe within a haven.";
                if (ZoneKindAt(attacker.X, attacker.Z) == ZoneKind.Haven) return "You cannot attack from within a haven.";
                if (SameCircle(attacker, tp)) return "They are of your circle.";
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

        private void UsePower(PlayerEntity p, int targetId)
        {
            BloodlineDef def = p.Def;
            if (p.InTorpor) return;
            if (_now < p.PowerReadyAt) { Notice(p, NoticeLevel.Info, def.PowerName + " is not ready."); return; }
            if (p.Blood < def.PowerCost) { Notice(p, NoticeLevel.Warning, "Not enough blood for " + def.PowerName + "."); return; }

            Entity t = null;
            if (def.PowerTarget == PowerTarget.Other)
            {
                _entities.TryGetValue(targetId, out t);
                string err = CanHarm(p, t);
                if (err != null) { Notice(p, NoticeLevel.Info, err); return; }
                if (p.DistanceTo(t) > def.PowerRange) { Notice(p, NoticeLevel.Info, "Out of range (" + def.PowerRange + "m)."); return; }
            }

            p.Blood -= def.PowerCost;
            p.PowerReadyAt = _now + def.PowerCooldown;
            p.LastCombat = _now;

            // Overt use of the Blood is a breach of the Hush if mortals see it.
            int w = AlarmWitnesses(p.X, p.Z, t, GameRules.WitnessRange);
            if (w > 0 && def.Id != Bloodline.Aurum) AddExposure(p, Math.Min(25f, 8f * w), "overt use of the Blood");

            var tm = t as Mortal;
            var tp = t as PlayerEntity;
            var th = t as Hunter;
            switch (def.Id)
            {
                case Bloodline.Vorlen:
                    p.SurgeUntil = _now + GameRules.SurgeDuration;
                    Notice(p, NoticeLevel.Good, "Blood surges through your limbs!");
                    break;

                case Bloodline.Sylphine:
                    if (tm != null)
                    {
                        if (tm.FedUponBy != 0) break;
                        tm.Mode = MortalMode.Charmed; tm.CharmedBy = p.Id; tm.ModeUntil = _now + 15;
                        Notice(p, NoticeLevel.Good, "The " + tm.Name + " is enthralled and drifts toward you.");
                    }
                    else if (tp != null) { tp.SlowUntil = _now + 4; Notice(tp, NoticeLevel.Warning, p.Name + "'s Allure clouds your limbs."); }
                    else if (th != null) th.StunnedUntil = _now + 3;
                    break;

                case Bloodline.Ashborn:
                    Notice(p, NoticeLevel.Good, "A lash of blood strikes your target.");
                    Damage(t, 3f, p);
                    break;

                case Bloodline.Aurum:
                    if (tm != null)
                    {
                        tm.Mode = MortalMode.Walking;
                        p.Exposure = Math.Max(0f, p.Exposure - 15f);
                        Notice(p, NoticeLevel.Good, "\"You saw nothing.\" The " + tm.Name + " forgets. (-15 exposure)");
                    }
                    else if (tp != null) { tp.RootUntil = _now + 2; Notice(tp, NoticeLevel.Warning, p.Name + " Commands you: \"Kneel.\""); }
                    else if (th != null) th.StunnedUntil = _now + 4;
                    break;
            }
        }

        private void Damage(Entity target, float amount, PlayerEntity source)
        {
            if (target.IsDown) return;
            target.Health -= amount;

            var tp = target as PlayerEntity;
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
            }
        }
    }
}
