# Asset build tools

`build_assets.py` downloads CC0 assets from Poly Haven and ambientCG and converts them into `Assets/Sanguine/Resources`:

- `Textures/`: albedo, normal, mask (metallic R, occlusion G, smoothness A) and emission maps;
- `Props/`: GLB files written by `props.mjs` with glTF-Transform;
- `Sky/`: HDR skies;
- `art_manifest.json`.

It also writes `Assets/Sanguine/Scripts/Core/Shared/PropCatalog.g.cs` and `CREDITS.md`.

```bash
cd Tools/assets && npm install && cd ../..
python3 Tools/assets/build_assets.py
```

Downloads are cached in `Tools/assets/.cache` (gitignored). To change what is used, edit the tables at the top of `build_assets.py` (textures, facades, skies) and `PROPS` in `props.mjs`.
