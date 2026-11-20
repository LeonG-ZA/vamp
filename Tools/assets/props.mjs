// Converts Poly Haven glTF props into compact, game-ready GLB files.
//   node props.mjs <cacheDir> <outDir> <metadataOut.json>
// For each prop: keep the wanted variant nodes, recentre the pivot, make lamp bulbs
// emissive, optionally simplify heavy meshes, downsize textures, prune and write GLB.
import fs from 'node:fs';
import path from 'node:path';
import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS, KHRMaterialsEmissiveStrength } from '@gltf-transform/extensions';
import { prune, dedup, weld, simplify, textureCompress, getBounds } from '@gltf-transform/functions';
import { MeshoptSimplifier } from 'meshoptimizer';
import sharp from 'sharp';

// pivot: 'below' = centre on X/Z, bottom at Y=0 (floor props)
//        'wall'  = centre on X, bottom at Y=0, back face at Z=0 (front faces +Z, away from the wall)
export const PROPS = [
  { id: 'street_lamp', src: 'street_lamp_01', tex: 1024, pivot: 'below', simplify: 0.3 },
  { id: 'fire_hydrant', src: 'fire_hydrant', keep: n => n.endsWith('_aged'), tex: 512, pivot: 'below', simplify: 0.15, error: 0.002 },
  { id: 'trash_can', src: 'metal_trash_can', keep: n => !n.includes('rust'), tex: 512, pivot: 'below', simplify: 0.6 },
  { id: 'trash_can_rusty', src: 'metal_trash_can', keep: n => n.includes('rust'), tex: 512, pivot: 'below', simplify: 0.6 },
  { id: 'covered_car', src: 'covered_car', tex: 1024, pivot: 'below', simplify: 0.5 },
  { id: 'road_barrier', src: 'concrete_road_barrier_02', tex: 512, pivot: 'below', simplify: 0.5 },
  { id: 'aircon', src: 'exterior_aircon_unit', keep: n => n.includes('rusted'), tex: 512, pivot: 'wall', simplify: 0.4, error: 0.002 },
  { id: 'security_light', src: 'security_light', tex: 512, pivot: 'wall', simplify: 0.5 },
  { id: 'utility_box', src: 'utility_box_01', tex: 512, pivot: 'below' },
  { id: 'trash_bag', src: 'trashbag', tex: 512, pivot: 'below', simplify: 0.6 },
  { id: 'fire_barrel', src: 'barrel_stove', tex: 512, pivot: 'below', simplify: 0.5 },
  { id: 'shutter_door', src: 'rollershutter_door', keep: n => n === 'rollershutter_door', tex: 512, pivot: 'wall' },
  { id: 'shutter_door_graffiti', src: 'rollershutter_door', keep: n => n.includes('graffiti'), tex: 512, pivot: 'wall' },
  { id: 'fire_escape', src: 'modular_fire_escape', tex: 1024, pivot: 'wall' },
  { id: 'bench', src: 'painted_wooden_bench', tex: 512, pivot: 'below' },
  { id: 'crate', src: 'wooden_crate_01', tex: 512, pivot: 'below', simplify: 0.4, error: 0.002 },
  { id: 'barrel', src: 'Barrel_01', tex: 512, pivot: 'below' },
  { id: 'cardboard_box', src: 'cardboard_box_01', tex: 512, pivot: 'below', simplify: 0.35 },
  { id: 'manhole', src: 'water_manhole_cover', tex: 512, pivot: 'below', simplify: 0.15, error: 0.003 },
  { id: 'planter', src: 'planter_box_01', tex: 512, pivot: 'below', simplify: 0.5 },
];

function worldBoundsOfPrimitive(node, prim) {
  const pos = prim.getAttribute('POSITION');
  const min = pos.getMin([]), max = pos.getMax([]);
  const m = node.getWorldMatrix();
  const out = { min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] };
  for (let i = 0; i < 8; i++) {
    const p = [(i & 1) ? max[0] : min[0], (i & 2) ? max[1] : min[1], (i & 4) ? max[2] : min[2]];
    const w = [0, 1, 2].map(r => m[r] * p[0] + m[4 + r] * p[1] + m[8 + r] * p[2] + m[12 + r]);
    for (let a = 0; a < 3; a++) { out.min[a] = Math.min(out.min[a], w[a]); out.max[a] = Math.max(out.max[a], w[a]); }
  }
  return out;
}

async function processProp(io, spec, cacheDir, outDir) {
  const dir = path.join(cacheDir, 'models', spec.src, '1k');
  const gltfFile = fs.readdirSync(dir).find(f => f.endsWith('.gltf'));
  const doc = await io.read(path.join(dir, gltfFile));
  const root = doc.getRoot();
  const scene = root.listScenes()[0];

  // 1. Keep only the wanted variant.
  if (spec.keep) {
    for (const n of scene.listChildren()) if (!spec.keep(n.getName())) n.dispose();
  }

  // 2. Recentre the pivot.
  const b = getBounds(scene);
  const cx = (b.min[0] + b.max[0]) / 2, cz = (b.min[2] + b.max[2]) / 2;
  const shift = spec.pivot === 'wall' ? [-cx, -b.min[1], -b.min[2]] : [-cx, -b.min[1], -cz];
  for (const n of scene.listChildren()) {
    const t = n.getTranslation();
    n.setTranslation([t[0] + shift[0], t[1] + shift[1], t[2] + shift[2]]);
  }

  // 3. Lamp bulbs glow (picked up by bloom); record their positions for real light sources.
  const emissiveExt = doc.createExtension(KHRMaterialsEmissiveStrength);
  const lights = [];
  for (const mat of root.listMaterials()) {
    if (/bulb/i.test(mat.getName())) {
      mat.setEmissiveFactor([1.0, 0.78, 0.5]);
      mat.setExtension('KHR_materials_emissive_strength', emissiveExt.createEmissiveStrength().setEmissiveStrength(6));
    }
  }
  for (const node of root.listNodes()) {
    const mesh = node.getMesh();
    if (!mesh) continue;
    for (const prim of mesh.listPrimitives()) {
      const mat = prim.getMaterial();
      if (mat && /bulb/i.test(mat.getName())) {
        const wb = worldBoundsOfPrimitive(node, prim);
        lights.push(wb.min.map((v, i) => +(((v + wb.max[i]) / 2).toFixed(3))));
      }
    }
  }

  // 4. Geometry and texture optimisation.
  const transforms = [dedup()];
  if (spec.simplify) transforms.push(weld(), simplify({ simplifier: MeshoptSimplifier, ratio: spec.simplify, error: spec.error || 0.001 }));
  transforms.push(textureCompress({ encoder: sharp, targetFormat: 'jpeg', resize: [spec.tex, spec.tex], quality: 86 }));
  transforms.push(prune());
  await doc.transform(...transforms);

  const outFile = path.join(outDir, spec.id + '.glb');
  await io.write(outFile, doc);

  const fb = getBounds(doc.getRoot().listScenes()[0]);
  let tris = 0;
  for (const mesh of root.listMeshes())
    for (const prim of mesh.listPrimitives()) {
      const idx = prim.getIndices();
      tris += (idx ? idx.getCount() : prim.getAttribute('POSITION').getCount()) / 3;
    }
  const round = a => a.map(v => +v.toFixed(3));
  return { id: spec.id, source: spec.src, file: `Props/${spec.id}`, tris: Math.round(tris), bytes: fs.statSync(outFile).size,
           min: round(fb.min), max: round(fb.max), pivot: spec.pivot, lights };
}

async function main() {
  const [cacheDir, outDir, metaOut] = process.argv.slice(2);
  fs.mkdirSync(outDir, { recursive: true });
  await MeshoptSimplifier.ready;
  const io = new NodeIO().registerExtensions(ALL_EXTENSIONS);
  const results = [];
  for (const spec of PROPS) {
    const r = await processProp(io, spec, cacheDir, outDir);
    console.log(`${r.id.padEnd(24)} ${String(r.tris).padStart(7)} tris ${(r.bytes / 1e6).toFixed(2).padStart(6)} MB  size=${r.max.map((v, i) => (v - r.min[i]).toFixed(2)).join('x')}  lights=${r.lights.length}`);
    results.push(r);
  }
  fs.writeFileSync(metaOut, JSON.stringify(results, null, 1));
}

main().catch(e => { console.error(e); process.exit(1); });
