# Sanguine City

An online vampire sandbox built in **Unity**. It draws on what is publicly known about CCP Games'
cancelled *World of Darkness* MMO (2006–2014): a politics-first sandbox with open PvP, a secrecy
mechanic, feeding on humans and ghoul-style minion missions. The setting and rules are original.

![Sanguine City preview](Docs/screenshot-preview.png)

*Preview: Selene feeds on a cab driver outside a Midtown club. Fleeing witnesses have pushed her exposure to 84%, so she's MARKED, and a hunter has picked up her trail. The game state and the city layout come from the real server and the shared `CityDressing` code, and the image uses the game's own art. It is a browser re-creation (three.js), not a Unity render, so lighting is approximate. Characters are shown with the built-in fallback figure (see [Characters](#characters)).*

- **[Docs/RESEARCH.md](Docs/RESEARCH.md)**: what design material exists for the cancelled game, what it says, and sources.
- **[Docs/DESIGN.md](Docs/DESIGN.md)**: the game design document for Sanguine City.

## Features

- **Four bloodlines**, each with a gift: Vorlen *Surge*, Sylphine *Allure*, Ashborn *Blood Lash*, Aurum *Command*.
- **Feeding** on a living crowd of mortals in four districts, and the choice to release them in time or let them die.
- **The Hush:** witnesses raise your Exposure. Hunters come at 50%, and at 80% any vampire may kill you.
- **Conscience:** killing mortals lets the Beast take over.
- **Open PvP** with consequences (Outlaw / Marked), plus safe **havens** and neutral **sanctums**.
- **Night/day cycle.** Be indoors at dawn or burn.
- **Circles** (player factions), **thrall missions** that win **district control**, a nightly **Regent election**,
  **edicts** and **blood hunts**.
- **Authoritative server** with persistence (characters, circles, influence, regent).
- **Realistic CC0 art:**
  - photoscanned PBR textures for roads, sidewalks and walls;
  - building facades with lit-window emission maps;
  - 20 Poly Haven street props (lampposts, parked cars, hydrants, trash cans, roller shutters, fire escapes, aircon units, fire barrels, harbour freight);
  - HDR night and day skies.
  All of it is listed in [CREDITS.md](CREDITS.md).
- **Night-city rendering:**
  - deferred HDR camera with about 390 real lights; the nearest lamps cast shadows;
  - wet streets with realtime reflections;
  - bloom, ambient occlusion, screen-space reflections and ACES grading.
- **Still runs without any art:** if the art or packages are missing, the game falls back to flat colours and simple shapes. Open the project and press Play.

## Quick start (Unity)

1. Open this folder with **Unity 6 (6000.0 LTS)** or Unity 2022.3.67+, using the Built-in render pipeline.
   - The Package Manager installs **glTFast** (it imports the props) and **Post Processing**.
   - On first load, the project switches itself to Linear colour space.
2. Open any scene (an empty one is fine) and press **Play**. Alternatively, run **Sanguine ▸ Create Main Scene** first.
3. Enter a name and password, pick a bloodline, and click **Host & Play**.
   - This starts the embedded server on port 7777 and joins it.
   - Friends can then click **Join Server** with your IP address.
   - Saves go to `Application.persistentDataPath/server-save`.

Make builds from **Sanguine ▸ Build ▸ …**:
- Windows, macOS and Linux clients.
- A Linux **Dedicated Server**. Run it with `-server -batchmode -nographics --port 7777`.

## Art pipeline

All third-party art is CC0. It comes from [Poly Haven](https://polyhaven.com) and [ambientCG](https://ambientcg.com) and is generated reproducibly:

```bash
cd Tools/assets && npm install && cd ../..
python3 Tools/assets/build_assets.py      # downloads, converts and writes Assets/Sanguine/Resources
```

The script:
- packs the PBR maps the way Unity's Standard shader expects them;
- splits, recentres and simplifies the props, then writes them as GLB;
- generates `PropCatalog.g.cs` and `CREDITS.md`.

`CityDressing` (shared, engine-free code) decides every building's facade and where each lamp, car, door, fire escape and light goes. The Unity client and the preview tool therefore build the same city.

## Characters

Characters are data-driven:
- Drop rigged human models into `Assets/Sanguine/Resources/Characters/{Vampires,Mortals,Hunters,Any}/`.
- Drop animation clips (Idle, Walk, Run, optionally Feed/Bite and Death) into `Assets/Sanguine/Resources/Animations/`.

The importer sets them up as Humanoid, so any humanoid rig works, for example Mixamo, Microsoft Rocketbox or MakeHuman exports. They are animated with the Playables API, so no animator controller is needed. Without such assets, an articulated fallback figure with a procedural walk and run cycle is used.

The repository doesn't ship realistic human models yet. None of the CC0 sources used here have any, and licensed sources need your decision, so check the license before committing characters to a public repository. Mixamo assets, for example, can be used in your game but not redistributed as files.

## Preview screenshots without Unity

`Tools/preview/make_preview.sh` produces a screenshot like the one above. It:
1. runs the real server and scripts a short scene with bot players;
2. exports the live state and the full `CityDressing`;
3. renders that with three.js in headless Chromium, using the same art.

## Controls

| Key | Action |
|---|---|
| WASD / arrows | Move |
| Right-drag, Q/E, mouse wheel | Orbit and zoom the camera |
| Left click / Tab | Select a target / cycle through nearby targets |
| F | Feed on the targeted (or nearest) mortal / release them |
| 1 | Strike |
| 2 | Use your bloodline gift |
| T | Thrall panel |
| Enter | Chat (`/help` for commands) |
| H / F1 | Help |
| Esc | Clear target, close panels |

Chat commands: `/help /who /stats /districts /say /c /city /circle … /vote /thrall /hunt /edict`.

## Dedicated server without Unity

The server is plain C# with no UnityEngine dependency. The same source files compile into Unity and into a console app:

```bash
# .NET 8 SDK
dotnet run --project Server/SanguineServer.csproj -- --port 7777 --save ./save

# or Mono
Server/build.sh
mono Server/bin/SanguineServer.exe --port 7777 --save ./save
```

Server options: `--port --seed --save --night --day --mortals --max-players --debug`.
`--debug` enables the test commands `/tp x z`, `/dawn`, `/setexposure n`, `/setblood n` and `/pos`. Never enable it on a public server.

## Tests

`Server/Tests/IntegrationTest.cs` starts a real server and connects three scripted bot clients over TCP. It exercises:
- login, duplicate login and password checks;
- circles and invitations;
- feeding and restraint;
- thrall missions;
- voting, dawn, the Regent and blood hunts;
- PvP rules (circle immunity, Outlaw, torpor and respawn) and sanctums;
- persistence across a server restart.

```bash
Server/build.sh && mono Server/bin/SanguineTests.exe     # 36 passed, 0 failed
```

## Project layout

```
Assets/Sanguine/Scripts/Core/      Sanguine.Core asmdef (no UnityEngine): shared + server
  Shared/  GameData (rules, bloodlines, districts), CityLayout (deterministic city), Protocol, Connection (TCP framing)
  Server/  GameServer (+ .Rules / .Ai / .Politics), Entities, Persistence, ServerConfig
Assets/Sanguine/Scripts/Client/    Sanguine.Client asmdef: Bootstrap, GameClient, LocalPlayer, EntityView,
                                   CityBuilder, Atmosphere, ClientUI (IMGUI), Mats
Assets/Sanguine/Scripts/Editor/    Menu items: create scene, build clients / dedicated server
Server/                            Standalone dedicated server (csproj + Mono build script) and integration test
Docs/                              Research notes and the design document
Tools/assets/                      CC0 art download + conversion pipeline (Python + glTF-Transform)
Tools/preview/                     Live-server scene export + three.js preview renderer
Assets/Sanguine/Resources/         Generated art: Textures/, Props/ (GLB), Sky/ (HDR), templates, sky shader
```

## Notes and limitations

- This is a playable prototype, not a production MMO.
  - Movement is client-predicted and speed-checked by the server, but the server does not collide against buildings.
  - Transport is TCP.
  - Persistence uses flat files.
  - See the roadmap in `Docs/DESIGN.md`.
- WebGL is not supported, because it has no threads or raw sockets.
- IP notice: *World of Darkness* and *Vampire: The Masquerade* are trademarks of Paradox Interactive.
  - This project is not affiliated with them or with CCP Games.
  - It uses none of their names, characters, clans, setting text or assets.
