# Sanguine City: Game Design Document

*An online sandbox of blood, secrecy and night-time politics.*
Prototype version 0.1. Every number below lives in `GameData.cs` / `GameRules.cs` / `ServerConfig.cs`.

## 1. Pitch

You are a newly made vampire in **Sanguine City**, a rain-slick harbour city run at night by the undead.
Feed without being seen, keep your conscience, build a circle of allies, send thralls to corrupt the
city's institutions, and fight for the throne of **Regent**. The design follows what is known of CCP's
cancelled *World of Darkness* MMO (see `RESEARCH.md`): a **politics-first sandbox with open PvP**, a
**secrecy** mechanic and **minion missions**. The setting, names and rules are original.

## 2. Pillars

1. **The predator's dilemma:** you must feed, but every feeding risks a death on your conscience or a witness.
2. **Secrecy as a resource:** the Hush (Exposure) is shared risk. Breaking it brings hunters and makes you fair game.
3. **Player-made order:** circles, district control, elections and blood hunts are run by players, not by NPCs.
4. **The night is short:** the day/night cycle sets the rhythm of play, so plan around dawn.

## 3. Setting

- **The city** is 410 m square: a 9×9 street grid with 8×8 blocks, in four districts, each tied to an institution:

| # | District | Influence | Feel |
|---|---|---|---|
| 1 | The Harbor (SW) | Underworld | warehouses, cranes, smugglers |
| 2 | Midtown (NE) | Media | neon clubs, newsroom |
| 3 | Old Quarter (NW) | Occult | crooked stone streets, chapels |
| 4 | Financial Row (SE) | Finance | glass towers |

- **Havens** (one per bloodline): no PvP, no sun damage, the respawn point.
- **Sanctums** (*The Gilded Opera*, *Chapel of Saint Veran*): neutral ground. No violence, no feeding, sheltered from the sun.

## 4. Bloodlines

| Bloodline | Epithet | Gift | Blood/Health/Melee | Favoured |
|---|---|---|---|---|
| **Vorlen** | the Iron Kin | *Surge*: +60% speed and 2× melee for 6 s (2 blood, 15 s cd) | 10 / 13 / 2 | Harbor |
| **Sylphine** | the Velvet Choir | *Allure* (12 m): a mortal follows you entranced; a rival is slowed 4 s; a hunter is stunned 3 s (1 blood, 10 s cd) | 11 / 10 / 1 | Midtown |
| **Ashborn** | the Grave Choir | *Blood Lash*: 3 damage at 15 m (2 blood, 6 s cd) | 12 / 9 / 1 | Old Quarter |
| **Aurum** | the Gilded Court | *Command* (9 m): a mortal forgets (−15 exposure, never raises it); a rival is rooted 2 s; a hunter is stunned 4 s (2 blood, 12 s cd) | 10 / 11 / 1 | Financial Row |

## 5. Core systems

### 5.1 Blood
- Blood drops by 1 every 90 s. In a district your circle controls, it drops only every 180 s.
- At 0 blood you are **starving** and lose 1 health every 5 s.
- **Mending:** out of combat for 4 s with more than 2 blood, 0.5 blood heals 1 health every 2 s.

### 5.2 Feeding
- Press F within 2.4 m of a calm mortal. You drink 1 blood/s, and the mortal holds 6.
- Moving more than 1 m, being hit, or attacking breaks the embrace.
- At 2 blood left, the game warns *"their heartbeat falters"*. Draining them kills them.
- **Restraint:** release a mortal alive after drinking at least 2.5 blood and you gain +0.25 Conscience.
- You cannot feed in a sanctum, on an alarmed mortal, or on one another vampire is already feeding on.

### 5.3 The Hush (Exposure 0–100)
| Event | Exposure |
|---|---|
| Starting to feed with witnesses within 15 m | +8 per witness (max 30) |
| Feeding while watched | +5 per witness per second (max 25/s) |
| Overt power use (all bloodlines except Aurum) | +8 per witness (max 25) |
| Public violence against a mortal or vampire | +3 per witness (max 15) |
| A mortal killed | +20, plus 10 per witness |
| A thrall caught | +10 |

- Witnesses become alarmed, run away along the streets, and cannot be fed on.
- Exposure decays by 1.5/s once you have gone 5 s without gaining any.
- At **50**, hunters spawn 25–50 m away. At **80** you are **Marked**, up to three hunters come, and other vampires may kill you without penalty.

### 5.4 Conscience (0–10, start 7)
- Killing a mortal costs −1. Restraint earns +0.25.
- At **3 or less: the Beast.** You are permanently Marked, and mortals within 8 m flee on sight.

### 5.5 Combat and PvP
- Melee: 2.4 m range, 1 s cooldown. Gifts are described in §4.
- PvP is open outside havens and sanctums. Members of the same circle cannot harm each other.
- At 0 health you fall into **torpor** for 10 s and lose 2 blood and 1 standing. You then wake in your haven with full health and at least 3 blood.
- **Justice:** destroying a Marked vampire gives +3 standing.
- **Murder:** destroying an unmarked vampire makes you an **Outlaw** (Marked) for 3 minutes, at −5 standing.
- **Hunters** (PvE) have 8 health and move at 5.2 m/s, a little slower than a vampire. They hit for 2 every 1.5 s. They will not enter a haven or sanctum and give up after waiting 15 s. Killing one gives +1 standing.

### 5.6 Night and day
- The default cycle is 15 minutes of night and 4 minutes of day.
- **During the day, anyone outside a haven or sanctum takes 1 sun damage every 2 s.**
- Dawn settles politics: the election, district payouts and influence decay.

## 6. Social and political systems

### 6.1 Circles
`/circle create|invite|join|leave|kick|info`. A circle has a founder, invitations only, and no friendly fire.
Circle chat is `/c`. A circle dissolves when its last member leaves.

### 6.2 Thralls and districts
- Each vampire has two thralls. `/thrall <district>` sends one on a 90 s mission.
- Success chance is 65%. Add 20% in your favoured district and 10% if your circle already controls it; subtract 15% if you are at 50+ exposure.
- **Success:** +2 standing, and your circle gains +10 influence in that district. A vampire with no circle gets +1 extra standing instead.
- **Failure:** the thrall is caught (+10 exposure) and needs 180 s to recover.
- A circle with at least 30 influence and the top total **controls** the district. Its members get slower blood loss there and +1 standing every dawn.
- At dawn, all influence decays by 10%.

### 6.3 The Regent
- `/vote <name>` during the night. Each vote's weight is 1 + standing/10.
- At dawn the top candidate becomes **Regent**. Ties go to higher standing, then to name order.
- The Regent's powers: `/edict <text>` (broadcast, persistent) and **one `/hunt <name>` per night**. The target is Marked for 10 minutes and the whole city is told.

## 7. Technical design

- **Authoritative server** in plain C# (`Assets/Sanguine/Scripts/Core`, `noEngineReferences`). It runs:
  - inside Unity, for *Host & Play*, on a background thread;
  - inside a headless Unity *Dedicated Server* build;
  - as a standalone .NET/Mono console app (`Server/`).
- **Transport:** TCP with length-prefixed binary frames (`Protocol.cs`, `Connection.cs`).
  - The server ticks at 20 Hz and sends snapshots at 10 Hz, limited to a 90 m interest radius.
  - Self-state goes out at 5 Hz and world-state every 2 s.
  - Entity names and circles go in an `EntityInfo` message the first time an entity is seen, and again when they change.
- **Movement:** the client moves locally and sends its position at 15 Hz.
  - The server checks each move against the speed limit and the root/torpor/feeding states. A failed move gets a `Teleport` correction.
  - The server has no building collision yet (see roadmap).
- **City:** a deterministic layout from a shared seed (`CityLayout.cs`, xorshift RNG), so server AI and client visuals agree.
- **Persistence:** plain-text files (profiles, plus world state: circles, influence, regent, edict).
  - Passwords are stored as salted SHA-256.
  - Saves are atomic, happen every 60 s, and also run on logout and shutdown.
- **Client:** creates everything at runtime (`Bootstrap` uses `RuntimeInitializeOnLoadMethod`): the procedural city, primitive avatars and an IMGUI HUD. No art assets or prefabs are needed.

## 8. Roadmap (beyond the prototype)

- Server-side building collision and lag compensation; move to UDP (e.g. Unity Transport) for scale.
- Sire/childe lineage (the Embrace as a player action), with brood-style ties.
- More gifts per bloodline, unlocked by spending standing; animated character models.
- Hunting-ground ownership inside districts; NPC informants; police and media reacting to exposure.
- Database persistence (PostgreSQL), account service, server sharding by city.
- Mobile and WebGL clients over WebSockets.
