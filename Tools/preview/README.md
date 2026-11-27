# Preview renderer

Produces screenshots of Sanguine City without Unity.

1. `ExportScene.cs`:
   - starts the real game server and connects two bot players;
   - scripts a scene: a Sylphine feeds on a Midtown mortal, near club neon and in front of witnesses, while a hunter closes in;
   - scores the candidate spots (neon a few metres away, at least three witnesses, away from the city's edge); the optional `spot` argument picks the runner-up spots instead of the best one;
   - writes the live game state plus the complete `CityDressing` to `scene.json`.
2. `render.js`:
   - rebuilds the same city with three.js from the game's own art in `Assets/Sanguine/Resources`;
   - covers the buildings and their UV mapping, the props, the lights, the sky shader and the fallback figures;
   - approximates the Unity post-processing with ACES tonemapping and bloom.
3. `shoot.mjs` serves the page and screenshots it with headless Chromium (Playwright).

```bash
Tools/preview/make_preview.sh out.png [yaw|auto] [pitch] [distance] [pixel-ratio] [spot]

# The screenshot in the main README:
Tools/preview/make_preview.sh ../../Docs/screenshot-preview.png 315 12 7.5 2 3
```

The server runs in real time, so a rerun can differ slightly (mortals keep walking).

Requirements: Mono (`mcs`, `mono`), Node.js 18+, and Playwright with Chromium. It is an approximation; lighting in Unity differs.
