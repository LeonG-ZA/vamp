# Preview renderer

Produces screenshots of Sanguine City without Unity.

1. `ExportScene.cs`:
   - starts the real game server and connects two bot players;
   - scripts a scene: a Sylphine feeds next to a Midtown club while a hunter closes in;
   - writes the live game state plus the complete `CityDressing` to `scene.json`.
2. `render.js`:
   - rebuilds the same city with three.js from the game's own art in `Assets/Sanguine/Resources`;
   - covers the buildings and their UV mapping, the props, the lights, the sky shader and the fallback figures;
   - approximates the Unity post-processing with ACES tonemapping and bloom.
3. `shoot.mjs` serves the page and screenshots it with headless Chromium (Playwright).

```bash
Tools/preview/make_preview.sh out.png [yaw|auto] [pitch] [distance] [pixel-ratio]
```

Requirements: Mono (`mcs`, `mono`), Node.js 18+, and Playwright with Chromium. It is an approximation; lighting in Unity differs.
