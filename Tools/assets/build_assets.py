#!/usr/bin/env python3
"""Builds Sanguine City's art assets from CC0 sources.

Downloads textures, skies and props from Poly Haven and building facades from
ambientCG (all CC0), converts them to game-ready sizes and writes them to the
Unity project's Resources folder together with a manifest and credits file.

    python3 Tools/assets/build_assets.py            # from the repository root
    python3 Tools/assets/build_assets.py --skip-props

Requirements: Python 3.9+, numpy, Pillow; Node.js 18+ for the props step
(`npm install` inside Tools/assets first). Downloads are cached in
Tools/assets/.cache so re-runs are fast.
"""
import argparse
import io
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
CACHE = os.path.join(HERE, '.cache')
RES = os.path.join(REPO, 'Assets', 'Sanguine', 'Resources')
UA = {'User-Agent': 'sanguine-city-asset-build/1.0'}

# ---------------------------------------------------------------------------
# What to build. Tile sizes are the real-world size (metres) of one texture repeat.
# ---------------------------------------------------------------------------

# Poly Haven texture sets: name -> (role notes, albedo res, normal res, smoothness scale)
POLYHAVEN_TEXTURES = {
    'asphalt_02':               dict(albedo=1024, normal=1024, gloss=1.15, use='roads'),
    'concrete_pavement':        dict(albedo=1024, normal=1024, gloss=1.0, use='sidewalks'),
    'cobblestone_floor_08':     dict(albedo=1024, normal=1024, gloss=1.1, use='Old Quarter roads'),
    'square_cobblestone':       dict(albedo=1024, normal=512, gloss=1.0, use='Old Quarter sidewalks'),
    'herringbone_pavement_03':  dict(albedo=1024, normal=512, gloss=1.0, use='haven plazas'),
    'floor_pavement':           dict(albedo=1024, normal=512, gloss=1.0, use='sanctum plazas'),
    'leafy_grass':              dict(albedo=1024, normal=512, gloss=0.8, use='parks'),
    'tarred_gravel':            dict(albedo=512, normal=512, gloss=1.0, use='roofs'),
    'corrugated_iron':          dict(albedo=1024, normal=512, gloss=1.0, use='warehouses'),
    'rusty_corrugated_iron':    dict(albedo=1024, normal=512, gloss=1.0, use='warehouses'),
    'factory_brick':            dict(albedo=1024, normal=512, gloss=1.0, use='warehouses'),
    'container_side':           dict(albedo=1024, normal=512, gloss=1.0, use='shipping containers'),
    'large_sandstone_blocks_01': dict(albedo=1024, normal=1024, gloss=1.0, use='crypts, sanctums'),
    'dark_brick_wall':          dict(albedo=1024, normal=512, gloss=1.0, use='Midtown ground floors, clubs'),
    'medieval_red_brick':       dict(albedo=1024, normal=512, gloss=1.0, use='Old Quarter ground floors'),
    'concrete_wall_004':        dict(albedo=1024, normal=512, gloss=1.0, use='Financial Row ground floors, curbs'),
}

# ambientCG facades: name -> tile size in metres (measured from the window grid)
AMBIENTCG_FACADES = {
    'Facade018B': dict(tile=18.0, use='brick apartments (lit windows)'),
    'Facade019B': dict(tile=18.0, use='modern offices (lit windows)'),
    'Facade020B': dict(tile=18.0, use='brick tower (lit windows)'),
    'Facade017':  dict(tile=42.0, use='concrete office block (lit windows)'),
    'Facade013':  dict(tile=110.0, use='skyscraper (lit windows)'),
    'Facade014':  dict(tile=110.0, use='skyscraper (sparse lit windows)'),
    'Facade016':  dict(tile=110.0, use='skyscraper (dark, lit windows)'),
}

HDRIS = {
    'night': ('qwantani_moon_noon_puresky', '2k'),
    'day': ('kloofendal_48d_partly_cloudy_puresky', '1k'),
}

# ---------------------------------------------------------------------------
# Download helpers
# ---------------------------------------------------------------------------

def http_get(url, tries=4):
    for i in range(tries):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=180) as r:
                return r.read()
        except Exception:
            if i == tries - 1:
                raise
            time.sleep(2 * (i + 1))


def cached(url, rel):
    path = os.path.join(CACHE, rel)
    if not os.path.exists(path) or os.path.getsize(path) == 0:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        data = http_get(url)
        with open(path + '.part', 'wb') as f:
            f.write(data)
        os.replace(path + '.part', path)
    return path


def ph_json(kind, asset):
    return json.load(open(cached(f'https://api.polyhaven.com/{kind}/{asset}', f'ph/api/{kind}_{asset}.json')))


def ph_texture_files(asset, res='1k'):
    f = ph_json('files', asset)
    out = {}
    for m in ('Diffuse', 'nor_gl', 'arm'):
        e = f[m][res]['jpg']
        out[m] = cached(e['url'], f'ph/textures/{asset}/{os.path.basename(e["url"])}')
    return out


def ph_model(asset, res='1k'):
    g = ph_json('files', asset)['gltf'][res]['gltf']
    base = f'ph/models/{asset}/{res}'
    cached(g['url'], f'{base}/{os.path.basename(g["url"])}')
    for rel, v in g.get('include', {}).items():
        cached(v['url'], f'{base}/{rel}')


def ph_hdri(asset, res):
    e = ph_json('files', asset)['hdri'][res]['hdr']
    return cached(e['url'], f'ph/hdris/{asset}_{res}.hdr')


def acg_zip(asset):
    return cached(f'https://ambientcg.com/get?file={asset}_1K-JPG.zip', f'acg/{asset}_1K-JPG.zip')

# ---------------------------------------------------------------------------
# Image helpers
# ---------------------------------------------------------------------------

def load(path_or_bytes, mode):
    src = io.BytesIO(path_or_bytes) if isinstance(path_or_bytes, bytes) else path_or_bytes
    return Image.open(src).convert(mode)


def save_jpg(img, path, size, quality=87):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.resize((size, size), Image.LANCZOS).save(path, 'JPEG', quality=quality, optimize=True, progressive=True)


def save_mask(path, size, rough, metal=None, ao=None):
    """Unity Standard packing: R = metallic, G = occlusion, A = smoothness (1 - roughness)."""
    def arr(img, default):
        if img is None:
            return np.full((size, size), default, np.uint8)
        return np.asarray(img.resize((size, size), Image.LANCZOS), np.uint8)
    r = arr(metal, 0)
    g = arr(ao, 255)
    a = 255 - arr(rough, 128)
    out = np.dstack([r, g, np.zeros_like(r), a])
    Image.fromarray(out, 'RGBA').save(path, 'PNG', optimize=True)


def real_size_m(info, default):
    dims = info.get('dimensions')
    if dims and len(dims) >= 2 and dims[0] > 0:
        return round(dims[0] / 1000.0, 2)  # Poly Haven dimensions are in millimetres
    return default

# ---------------------------------------------------------------------------
# Build steps
# ---------------------------------------------------------------------------

def build_polyhaven_textures(manifest, credits):
    for name, cfg in POLYHAVEN_TEXTURES.items():
        files = ph_texture_files(name)
        info = ph_json('info', name)
        out = os.path.join(RES, 'Textures', name)
        save_jpg(load(files['Diffuse'], 'RGB'), os.path.join(out, f'{name}_albedo.jpg'), cfg['albedo'])
        save_jpg(load(files['nor_gl'], 'RGB'), os.path.join(out, f'{name}_normal.jpg'), cfg['normal'], quality=90)
        arm = load(files['arm'], 'RGB')
        ao, rough, metal = arm.split()
        save_mask(os.path.join(out, f'{name}_mask.png'), 512, rough, metal, ao)
        tile = real_size_m(info, 4.0)
        manifest['textures'].append(dict(name=name, tile=tile, emission=False, gloss=cfg['gloss'], use=cfg['use']))
        credits.append(dict(kind='Texture', name=info.get('name', name), id=name, use=cfg['use'],
                            url=f'https://polyhaven.com/a/{name}', authors=', '.join(info.get('authors', {}).keys()),
                            source='Poly Haven', license='CC0 1.0'))
        print(f'  texture {name:28s} tile {tile} m')


def build_ambientcg_facades(manifest, credits):
    for name, cfg in AMBIENTCG_FACADES.items():
        z = zipfile.ZipFile(acg_zip(name))
        maps = {n.rsplit('_', 1)[1].split('.')[0]: n for n in z.namelist() if n.endswith('.jpg')}
        rd = lambda key, mode: load(z.read(maps[key]), mode) if key in maps else None
        out = os.path.join(RES, 'Textures', name)
        save_jpg(rd('Color', 'RGB'), os.path.join(out, f'{name}_albedo.jpg'), 1024, quality=86)
        save_jpg(rd('NormalGL', 'RGB'), os.path.join(out, f'{name}_normal.jpg'), 512, quality=88)
        save_mask(os.path.join(out, f'{name}_mask.png'), 512, rd('Roughness', 'L'), rd('Metalness', 'L'), rd('AmbientOcclusion', 'L'))
        has_emission = 'Emission' in maps
        if has_emission:
            save_jpg(rd('Emission', 'RGB'), os.path.join(out, f'{name}_emission.jpg'), 1024, quality=86)
        manifest['textures'].append(dict(name=name, tile=cfg['tile'], emission=has_emission, gloss=1.0, use=cfg['use']))
        credits.append(dict(kind='Facade texture', name=name, id=name, use=cfg['use'],
                            url=f'https://ambientcg.com/view?id={name}', authors='Lennart Demes',
                            source='ambientCG', license='CC0 1.0'))
        print(f'  facade  {name:28s} tile {cfg["tile"]} m emission={has_emission}')


def build_skies(manifest, credits):
    out = os.path.join(RES, 'Sky')
    os.makedirs(out, exist_ok=True)
    for key, (asset, res) in HDRIS.items():
        shutil.copyfile(ph_hdri(asset, res), os.path.join(out, f'{key}_sky.hdr'))
        info = ph_json('info', asset)
        manifest['sky'][key] = f'Sky/{key}_sky'
        if key == 'night':
            manifest['sky']['moonU'], manifest['sky']['moonV'] = moon_direction(os.path.join(out, f'{key}_sky.hdr'))
        credits.append(dict(kind='HDRI sky', name=info.get('name', asset), id=asset, use=f'{key} sky',
                            url=f'https://polyhaven.com/a/{asset}', authors=', '.join(info.get('authors', {}).keys()),
                            source='Poly Haven', license='CC0 1.0'))
        print(f'  sky     {key:6s} <- {asset} ({res})')


def build_props(manifest, credits):
    node_dir = HERE
    if not os.path.isdir(os.path.join(node_dir, 'node_modules')):
        subprocess.check_call(['npm', 'install', '--no-audit', '--no-fund'], cwd=node_dir)
    # The prop list lives in props.mjs; read the source ids from it to download them first.
    import re
    src = open(os.path.join(HERE, 'props.mjs'), encoding='utf-8').read()
    sources = sorted(set(re.findall(r"src: '([^']+)'", src)))
    for s in sources:
        ph_model(s)
    meta_path = os.path.join(CACHE, 'props_meta.json')
    subprocess.check_call(['node', os.path.join(HERE, 'props.mjs'), os.path.join(CACHE, 'ph'),
                           os.path.join(RES, 'Props'), meta_path], cwd=node_dir)
    props = json.load(open(meta_path))
    for p in props:  # JsonUtility-friendly light anchors
        p['lights'] = [dict(x=l[0], y=l[1], z=l[2]) for l in p['lights']]
    manifest['props'] = props
    write_prop_catalog(props)
    for s in sources:
        info = ph_json('info', s)
        credits.append(dict(kind='3D model', name=info.get('name', s), id=s,
                            use=', '.join(p['id'] for p in props if p['source'] == s),
                            url=f'https://polyhaven.com/a/{s}', authors=', '.join(info.get('authors', {}).keys()),
                            source='Poly Haven', license='CC0 1.0'))


def write_prop_catalog(props):
    """Writes prop metrics into the shared (engine-free) core so the server-side tools and
    CityDressing can place props without loading any art. glTF is right-handed; Unity (and
    glTFast) mirror the X axis, so X values are negated here."""
    f = lambda v: f'{v:.3f}f'
    lines = ['// <auto-generated> by Tools/assets/build_assets.py from the processed Poly Haven props. Do not edit.',
             'namespace Sanguine', '{', '    public static partial class PropCatalog', '    {',
             '        static readonly PropInfo[] Generated =', '        {']
    for p in props:
        mn, mx = p['min'], p['max']
        lights = ', '.join(f"{f(-l['x'])}, {f(l['y'])}, {f(l['z'])}" for l in p['lights'])
        lights_expr = f'new float[] {{ {lights} }}' if lights else 'new float[0]'
        lines.append(f'            new PropInfo("{p["id"]}", {f(-mx[0])}, {f(mn[1])}, {f(mn[2])}, {f(-mn[0])}, {f(mx[1])}, {f(mx[2])}, {p["tris"]}, {lights_expr}),')
    lines += ['        };', '    }', '}', '']
    path = os.path.join(REPO, 'Assets', 'Sanguine', 'Scripts', 'Core', 'Shared', 'PropCatalog.g.cs')
    with open(path, 'w', encoding='utf-8') as fh:
        fh.write('\n'.join(lines))


def moon_direction(hdr_path):
    """Finds the brightest spot of an equirectangular HDR (u, v in Unity texture space, v=1 at the top)."""
    try:
        pfm = os.path.join(CACHE, 'moon_probe.pfm')
        subprocess.check_call(['convert', hdr_path, '-resize', '512x256!', '-colorspace', 'gray', pfm],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        with open(pfm, 'rb') as fh:
            def line():
                while True:
                    l = fh.readline()
                    if not l.startswith(b'#'):
                        return l
            header = line(); dims = line().split(); scale = float(line())
            w, h = int(dims[0]), int(dims[1])
            data = np.frombuffer(fh.read(), dtype='<f4' if scale < 0 else '>f4').reshape(h, w)
        data = data[::-1]  # PFM rows are stored bottom-up; flip to top-down image rows
        y, x = np.unravel_index(np.argmax(data), data.shape)
        return round((x + 0.5) / w, 4), round(1.0 - (y + 0.5) / h, 4)
    except Exception as e:
        print('  (moon detection skipped:', e, ')')
        return 0.5, 0.85


def write_outputs(manifest, credits):
    with open(os.path.join(RES, 'art_manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=1)
    lines = ['# Art credits', '',
             'All third-party art in Sanguine City is released under **CC0 1.0** (public domain).',
             'Attribution is not required, but these creators deserve the credit.',
             'Files are generated by `Tools/assets/build_assets.py`, which downloads the originals and converts them.', '',
             '| Asset | Type | Used for | Author(s) | Source |', '|---|---|---|---|---|']
    for c in credits:
        lines.append(f"| [{c['name']}]({c['url']}) | {c['kind']} | {c['use']} | {c['authors'] or '-'} | {c['source']} ({c['license']}) |")
    with open(os.path.join(REPO, 'CREDITS.md'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--skip-props', action='store_true')
    args = ap.parse_args()
    manifest = dict(version=1, textures=[], props=[], sky={})
    credits = []
    print('Poly Haven textures'); build_polyhaven_textures(manifest, credits)
    print('ambientCG facades'); build_ambientcg_facades(manifest, credits)
    print('Skies'); build_skies(manifest, credits)
    if not args.skip_props:
        print('Props'); build_props(manifest, credits)
    elif os.path.exists(os.path.join(RES, 'art_manifest.json')):
        manifest['props'] = json.load(open(os.path.join(RES, 'art_manifest.json'))).get('props', [])
    write_outputs(manifest, credits)
    total = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(RES) for f in fs)
    print(f'Done. Resources folder is {total / 1e6:.1f} MB.')


if __name__ == '__main__':
    sys.exit(main())
