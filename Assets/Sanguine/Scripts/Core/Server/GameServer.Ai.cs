using System;
using System.Collections.Generic;

namespace Sanguine.Server
{
    // Mortal crowds (the herd) and the hunters who answer breaches of the Hush.
    public sealed partial class GameServer
    {
        private static float Heading(float dx, float dz)
        {
            return (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
        }

        /// <summary>Moves an entity toward a point. Returns true when it arrived.</summary>
        private static bool MoveToward(Entity e, float tx, float tz, float speed, float dt, float arriveDist = 0.3f)
        {
            float dx = tx - e.X, dz = tz - e.Z;
            float d = (float)Math.Sqrt(dx * dx + dz * dz);
            if (d <= arriveDist) return true;
            float step = Math.Min(d, speed * dt);
            e.X += dx / d * step;
            e.Z += dz / d * step;
            e.Rot = Heading(dx, dz);
            return d - step <= arriveDist;
        }

        // ------------------------------------------------------------------
        // Mortals
        // ------------------------------------------------------------------

        private void SpawnInitialMortals()
        {
            for (int d = 0; d < GameData.Districts.Length; d++)
                for (int i = 0; i < _cfg.MortalsPerDistrict; i++)
                    SpawnMortal(d);
        }

        private int RandomNodeInDistrict(int district)
        {
            for (int tries = 0; tries < 50; tries++)
            {
                int n = _rand.Next(_city.Nodes.Length);
                if (_city.Nodes[n].District == district) return n;
            }
            return _rand.Next(_city.Nodes.Length);
        }

        private void SpawnMortal(int district)
        {
            DistrictDef dd = GameData.Districts[district];
            var m = new Mortal
            {
                Id = _nextEntityId++, Kind = EntityKind.Mortal, HomeDistrict = district,
                Name = dd.MortalTitles[_rand.Next(dd.MortalTitles.Length)],
                MaxHealth = GameRules.MortalMaxHealth
            };
            PlaceMortal(m);
            _mortals.Add(m);
            _entities[m.Id] = m;
        }

        private void PlaceMortal(Mortal m)
        {
            int node = RandomNodeInDistrict(m.HomeDistrict);
            // Pedestrians walk on the sidewalks, clear of parked cars and curb-side furniture.
            m.OffsetX = (RandF() < 0.5f ? -1f : 1f) * (6.1f + RandF() * 0.4f);
            m.OffsetZ = (RandF() < 0.5f ? -1f : 1f) * (6.1f + RandF() * 0.4f);
            m.X = _city.Nodes[node].X + m.OffsetX;
            m.Z = _city.Nodes[node].Z + m.OffsetZ;
            m.Health = m.MaxHealth;
            m.Blood = GameRules.MortalMaxBlood;
            m.Mode = MortalMode.Walking;
            m.FedUponBy = 0;
            PickNextNode(m, node);
        }

        private void PickNextNode(Mortal m, int fromNode)
        {
            _city.Neighbors(fromNode, _scratchNodes);
            var home = new List<int>(4);
            foreach (int n in _scratchNodes) if (_city.Nodes[n].District == m.HomeDistrict) home.Add(n);
            List<int> pool = home.Count > 0 ? home : _scratchNodes;
            m.TargetNode = pool[_rand.Next(pool.Count)];
        }

        private void Alarm(Mortal m, float fromX, float fromZ)
        {
            if (m.Mode == MortalMode.Dead || m.FedUponBy != 0) return;
            bool wasFleeing = m.Mode == MortalMode.Fleeing;
            m.Mode = MortalMode.Fleeing;
            m.ModeUntil = _now + 10;
            m.FleeFromX = fromX; m.FleeFromZ = fromZ;
            if (!wasFleeing) m.TargetNode = FleeNode(m);
        }

        private int FleeNode(Mortal m)
        {
            int near = _city.NearestNode(m.X, m.Z);
            _city.Neighbors(near, _scratchNodes);
            _scratchNodes.Add(near);
            int best = near;
            float bestD = -1;
            foreach (int n in _scratchNodes)
            {
                float d = Entity.Dist(_city.Nodes[n].X, _city.Nodes[n].Z, m.FleeFromX, m.FleeFromZ);
                if (d > bestD) { bestD = d; best = n; }
            }
            return best;
        }

        private void UpdateMortals(float dt)
        {
            foreach (var m in _mortals)
            {
                if (m.Mode == MortalMode.Dead)
                {
                    if (_now >= m.RespawnAt) PlaceMortal(m);
                    continue;
                }

                if (m.Blood < GameRules.MortalMaxBlood && _now >= m.NextBloodRegen)
                {
                    m.NextBloodRegen = _now + 30;
                    m.Blood = Math.Min(GameRules.MortalMaxBlood, m.Blood + 1f);
                }

                if (m.IsStunned(_now)) continue;

                switch (m.Mode)
                {
                    case MortalMode.Walking:
                    {
                        // Mortals sense the Beast in those who have lost their conscience.
                        foreach (var p in _players)
                        {
                            if (!p.InTorpor && p.Conscience <= GameRules.ConscienceBeastThreshold && _now >= p.GlamourUntil && p.DistanceTo(m) < 8f)
                            { Alarm(m, p.X, p.Z); break; }
                        }
                        if (m.Mode != MortalMode.Walking) break;
                        StreetNode n = _city.Nodes[m.TargetNode];
                        if (MoveToward(m, n.X + m.OffsetX, n.Z + m.OffsetZ, GameRules.MortalWalkSpeed, dt))
                            PickNextNode(m, m.TargetNode);
                        break;
                    }
                    case MortalMode.Fleeing:
                    {
                        StreetNode n = _city.Nodes[m.TargetNode];
                        if (MoveToward(m, n.X + m.OffsetX, n.Z + m.OffsetZ, GameRules.MortalRunSpeed, dt))
                            m.TargetNode = FleeNode(m);
                        if (_now >= m.ModeUntil)
                        {
                            m.Mode = MortalMode.Walking;
                            m.TargetNode = _city.NearestNode(m.X, m.Z);
                        }
                        break;
                    }
                    case MortalMode.Charmed:
                    {
                        var charmer = Get<PlayerEntity>(m.CharmedBy);
                        if (charmer == null || charmer.InTorpor || _now >= m.ModeUntil)
                        {
                            m.Mode = MortalMode.Walking;
                            m.TargetNode = _city.NearestNode(m.X, m.Z);
                            break;
                        }
                        float wx, wz;
                        _collision.NextWaypoint(m.X, m.Z, charmer.X, charmer.Z, out wx, out wz);
                        bool direct = wx == charmer.X && wz == charmer.Z;
                        if (_collision.LineOfSight(m.X, m.Z, wx, wz, 0.25f))
                            MoveToward(m, wx, wz, GameRules.MortalWalkSpeed * 1.5f, dt, direct ? 1.5f : 0.3f);
                        break;
                    }
                    case MortalMode.Entranced:
                    {
                        var feeder = Get<PlayerEntity>(m.FedUponBy);
                        if (feeder == null || feeder.FeedTarget != m.Id)
                        {
                            m.FedUponBy = 0;
                            m.Mode = MortalMode.Walking;
                            m.TargetNode = _city.NearestNode(m.X, m.Z);
                        }
                        else m.Rot = Heading(feeder.X - m.X, feeder.Z - m.Z);
                        break;
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Hunters
        // ------------------------------------------------------------------

        private int HuntersOn(PlayerEntity p)
        {
            int n = 0;
            foreach (var h in _hunters) if (h.TargetPlayer == p.Id) n++;
            return n;
        }

        private void MaybeSpawnHunters(PlayerEntity p)
        {
            if (p.InTorpor || p.Exposure < GameRules.ExposureHunterThreshold || _now < p.MistUntil) return;
            if (ZoneKindAt(p.X, p.Z) != ZoneKind.None) return;
            int desired = p.Exposure >= GameRules.ExposureMarkedThreshold ? 3 : 1;
            if (HuntersOn(p) >= desired) return;

            // Appear from a street corner 25-50m away, out of sight.
            int best = -1;
            foreach (var n in _city.Nodes)
            {
                float d = Entity.Dist(n.X, n.Z, p.X, p.Z);
                if (d >= 25f && d <= 50f && ZoneKindAt(n.X, n.Z) == ZoneKind.None)
                {
                    if (best < 0 || _rand.Next(3) == 0) best = n.Index;
                }
            }
            if (best < 0) best = _city.NearestNode(p.X + 30f, p.Z);

            var h = new Hunter
            {
                Id = _nextEntityId++, Kind = EntityKind.Hunter, Name = "Hunter",
                X = _city.Nodes[best].X, Z = _city.Nodes[best].Z,
                Health = GameRules.HunterMaxHealth, MaxHealth = GameRules.HunterMaxHealth,
                TargetPlayer = p.Id, NextAttack = _now + 1
            };
            _hunters.Add(h);
            _entities[h.Id] = h;
            Notice(p, NoticeLevel.Danger, "A hunter has picked up your trail!");
        }

        private void RemoveHunter(Hunter h)
        {
            _hunters.Remove(h);
            _entities.Remove(h.Id);
        }

        private void UpdateHunters(float dt)
        {
            for (int i = _hunters.Count - 1; i >= 0; i--)
            {
                Hunter h = _hunters[i];
                var target = Get<PlayerEntity>(h.TargetPlayer);
                if (target == null || target.InTorpor || (target.Exposure < 20f && !IsMarked(target)))
                {
                    RemoveHunter(h);
                    continue;
                }

                if (ZoneKindAt(target.X, target.Z) != ZoneKind.None)
                {
                    // Hunters will not storm a haven or sanctum; they wait, then give up.
                    if (h.LostTargetSince < 0) h.LostTargetSince = _now;
                    if (_now - h.LostTargetSince > 15) { RemoveHunter(h); continue; }
                    continue;
                }
                h.LostTargetSince = -1;

                if (h.IsStunned(_now)) continue;

                float dist = h.DistanceTo(target);
                if (_now < target.MajestyUntil && dist < 6.5f)
                {
                    // Awed by Majesty: the hunter keeps its distance and does not strike.
                    h.Rot = Heading(target.X - h.X, target.Z - h.Z);
                    continue;
                }
                if (dist > 1.6f)
                {
                    // Chase directly when the target is in sight, otherwise follow the streets around buildings.
                    if (_now >= h.NextRepath)
                    {
                        h.NextRepath = _now + 0.3;
                        _collision.NextWaypoint(h.X, h.Z, target.X, target.Z, out h.WayX, out h.WayZ);
                    }
                    bool direct = _collision.LineOfSight(h.X, h.Z, target.X, target.Z);
                    if (direct) MoveToward(h, target.X, target.Z, GameRules.HunterSpeed, dt, 1.5f);
                    else if (_collision.LineOfSight(h.X, h.Z, h.WayX, h.WayZ, 0.25f))
                    {
                        if (MoveToward(h, h.WayX, h.WayZ, GameRules.HunterSpeed, dt, 0.3f)) h.NextRepath = 0;
                    }
                }
                else h.Rot = Heading(target.X - h.X, target.Z - h.Z);

                if (dist <= 2.0f && _now >= h.NextAttack)
                {
                    h.NextAttack = _now + GameRules.HunterAttackCooldown;
                    Notice(target, NoticeLevel.Danger, "A hunter strikes you with a blessed blade!");
                    Damage(target, GameRules.HunterDamage, null);
                }
            }
        }
    }
}
