// Browser preview of Sanguine City. Renders the exact CityDressing + live server state exported by
// ExportScene.cs, using the same generated art as the Unity client (texture sets, Poly Haven GLB
// props, HDR sky). It approximates Unity's Built-in deferred pipeline + post-processing with three.js.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { RGBELoader } from 'three/addons/loaders/RGBELoader.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';

const P = Object.assign({ yaw: 'auto', pitch: 14, dist: 8, dpr: 1, radius: 120, lights: 14 }, window.PARAMS || {});
const W = 1600, H = 900, RES = 'res/';
const S = await (await fetch('scene.json')).json();
const ART = await (await fetch(RES + 'art_manifest.json')).json();
const D = S.dressing;

// Unity (left-handed) -> three (right-handed): mirror Z.
const V = (x, y, z) => new THREE.Vector3(x, y, -z);
const col = (r, g, b) => new THREE.Color().setRGB(r, g, b, THREE.SRGBColorSpace);

// ---- Rng: exact port of Sanguine.Rng (xorshift32) ----
function Rng(seed) {
  let st = ((seed | 0) ^ 0x9E3779B9) >>> 0; if (st === 0) st = 0xDEADBEEF;
  this.nextUInt = () => { let x = st; x = (x ^ (x << 13)) >>> 0; x = (x ^ (x >>> 17)) >>> 0; x = (x ^ (x << 5)) >>> 0; st = x; return x; };
  for (let i = 0; i < 4; i++) this.nextUInt();
  this.nextFloat = () => (this.nextUInt() >>> 8) * (1 / 16777216);
  this.rangeF = (a, b) => a + (b - a) * this.nextFloat();
}

const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
renderer.setPixelRatio(P.dpr);
renderer.setSize(W, H);
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.46; // PPv2 ColorGrading postExposure +0.55 EV in PostFx.cs
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
document.getElementById('view').appendChild(renderer.domElement);

const scene = new THREE.Scene();
const fogColor = col(0.045, 0.05, 0.075);
scene.fog = new THREE.FogExp2(fogColor, 0.0085);

// ---------------------------------------------------------------- textures and materials
const texLoader = new THREE.TextureLoader();
const texCache = {};
const texPending = [];
function tex(path, srgb) {
  if (texCache[path] !== undefined) return texCache[path];
  let done; texPending.push(new Promise(r => { done = r; }));
  const t = texLoader.load(path, () => done(), undefined, () => { console.log('missing texture ' + path); done(); });
  t.wrapS = t.wrapT = THREE.RepeatWrapping; t.anisotropy = 8;
  if (srgb) t.colorSpace = THREE.SRGBColorSpace;
  texCache[path] = t; return t;
}
// Unity's packed mask (R metallic, G occlusion, A smoothness) is read raw (no canvas: browsers
// premultiply alpha, which would destroy RGB wherever smoothness is 0) and decoded in the shader.
function useUnityMask(m, mask, gloss) {
  m.roughnessMap = mask; m.metalnessMap = mask; m.aoMap = mask; m.roughness = 1; m.metalness = 1;
  m.onBeforeCompile = sh => {
    sh.fragmentShader = sh.fragmentShader
      .replace('roughnessFactor *= texelRoughness.g;', `roughnessFactor *= 1.0 - clamp(texelRoughness.a * ${gloss.toFixed(3)}, 0.0, 1.0);`)
      .replace('metalnessFactor *= texelMetalness.b;', 'metalnessFactor *= texelMetalness.r;')
      .replace('texture2D( aoMap, vAoMapUv ).r', 'texture2D( aoMap, vAoMapUv ).g');
  };
  m.customProgramCacheKey = () => 'unity-mask-' + gloss.toFixed(3);
}
const sets = {}; for (const t of ART.textures) sets[t.name] = t;
const tile = n => (sets[n] && sets[n].tile) || 2;
const matCache = {};
const ENV_NIGHT = 0.1; // night sky exposure (0.06) x ambient intensity (1.6), as in Atmosphere.cs
async function surface(name, tint, emission) {
  const key = name + '|' + tint.getHexString() + '|' + emission;
  if (matCache[key]) return matCache[key];
  let m;
  if (name === '#glow') {
    m = new THREE.MeshStandardMaterial({ color: tint.clone().multiplyScalar(0.2), emissive: tint, emissiveIntensity: emission * 1.0, roughness: 0.4 });
  } else if (name === '#paint') {
    m = new THREE.MeshStandardMaterial({ color: col(0.72, 0.72, 0.68), roughness: 0.65 });
  } else if (sets[name]) {
    const e = sets[name], p = `${RES}Textures/${name}/${name}`;
    let gloss = e.gloss; if (name === 'asphalt_02' || name === 'cobblestone_floor_08') gloss *= 2.4;
    m = new THREE.MeshStandardMaterial({ map: tex(p + '_albedo.jpg', true), normalMap: tex(p + '_normal.jpg', false), color: tint, envMapIntensity: ENV_NIGHT });
    useUnityMask(m, tex(p + '_mask.png', false), gloss);
    if (e.emission) { m.emissiveMap = tex(p + '_emission.jpg', true); m.emissive = new THREE.Color(1, 1, 1); m.emissiveIntensity = emission > 0 ? emission : 0.5; }
  } else m = new THREE.MeshStandardMaterial({ color: tint, roughness: 0.8 });
  matCache[key] = m; return m;
}

// ---------------------------------------------------------------- MeshKit port
class Kit {
  constructor() { this.groups = new Map(); }
  bucket(m, tint, e) { const k = m + '|' + tint.getHexString() + '|' + e; if (!this.groups.has(k)) this.groups.set(k, { m, tint, e, pos: [], uv: [] }); return this.groups.get(k); }
  quad(m, tint, e, a, b, c, d, ua, ub, uc, ud) {
    const g = this.bucket(m, tint, e);
    for (const [p, u] of [[a, ua], [b, ub], [c, uc], [a, ua], [c, uc], [d, ud]]) { const t = V(p[0], p[1], p[2]); g.pos.push(t.x, t.y, t.z); g.uv.push(u[0], u[1]); }
  }
  wall(m, tint, e, p0, p1, y0, y1, u0, vBase, uo = 0, vo = 0) {
    const t = tile(m), len = Math.hypot(p1[0] - p0[0], p1[1] - p0[1]);
    const ua = u0 / t + uo, ub = (u0 + len) / t + uo, va = (y0 - vBase) / t + vo, vb = (y1 - vBase) / t + vo;
    this.quad(m, tint, e, [p0[0], y0, p0[1]], [p0[0], y1, p0[1]], [p1[0], y1, p1[1]], [p1[0], y0, p1[1]], [ua, va], [ua, vb], [ub, vb], [ub, va]);
  }
  floor(m, tint, e, x0, z0, x1, z1, y, down = false) {
    const t = tile(m), a = [x0, y, z0], b = [x0, y, z1], c = [x1, y, z1], d = [x1, y, z0];
    const ua = [x0 / t, z0 / t], ub = [x0 / t, z1 / t], uc = [x1 / t, z1 / t], ud = [x1 / t, z0 / t];
    if (down) this.quad(m, tint, e, a, d, c, b, ua, ud, uc, ub); else this.quad(m, tint, e, a, b, c, d, ua, ub, uc, ud);
  }
  ring(m, tint, e, x0, z0, x1, z1, y0, y1, vBase, uo = 0, vo = 0, inward = false) {
    const c0 = [x0, z0], c1 = [x1, z0], c2 = [x1, z1], c3 = [x0, z1];
    const pts = inward ? [c1, c0, c3, c2, c1] : [c0, c1, c2, c3, c0];
    let u = 0;
    for (let i = 0; i < 4; i++) { this.wall(m, tint, e, pts[i], pts[i + 1], y0, y1, u, vBase, uo, vo); u += Math.hypot(pts[i + 1][0] - pts[i][0], pts[i + 1][1] - pts[i][1]); }
  }
  box(m, tint, e, c, s, yaw) {
    const t = tile(m), h = [s[0] / 2, s[1] / 2, s[2] / 2], a = yaw * Math.PI / 180, cs = Math.cos(a), sn = Math.sin(a);
    const P = (x, y, z) => [c[0] + x * cs + z * sn, c[1] + y, c[2] - x * sn + z * cs];
    const f = [[P(-h[0], -h[1], -h[2]), P(-h[0], h[1], -h[2]), P(h[0], h[1], -h[2]), P(h[0], -h[1], -h[2])],
               [P(h[0], -h[1], -h[2]), P(h[0], h[1], -h[2]), P(h[0], h[1], h[2]), P(h[0], -h[1], h[2])],
               [P(h[0], -h[1], h[2]), P(h[0], h[1], h[2]), P(-h[0], h[1], h[2]), P(-h[0], -h[1], h[2])],
               [P(-h[0], -h[1], h[2]), P(-h[0], h[1], h[2]), P(-h[0], h[1], -h[2]), P(-h[0], -h[1], -h[2])]];
    const w = [s[0], s[2], s[0], s[2]];
    for (let i = 0; i < 4; i++) { const uw = w[i] / t, uh = s[1] / t; this.quad(m, tint, e, f[i][0], f[i][1], f[i][2], f[i][3], [0, 0], [0, uh], [uw, uh], [uw, 0]); }
    const tw = s[0] / t, td = s[2] / t;
    this.quad(m, tint, e, P(-h[0], h[1], -h[2]), P(-h[0], h[1], h[2]), P(h[0], h[1], h[2]), P(h[0], h[1], -h[2]), [0, 0], [0, td], [tw, td], [tw, 0]);
  }
  async build(parent, shadows = true) {
    for (const g of this.groups.values()) {
      const geo = new THREE.BufferGeometry();
      geo.setAttribute('position', new THREE.Float32BufferAttribute(g.pos, 3));
      geo.setAttribute('uv', new THREE.Float32BufferAttribute(g.uv, 2));
      geo.computeVertexNormals();
      const mesh = new THREE.Mesh(geo, await surface(g.m, g.tint, g.e));
      mesh.castShadow = shadows; mesh.receiveShadow = true; parent.add(mesh);
    }
  }
}

// ---------------------------------------------------------------- city
const W1 = new THREE.Color(1, 1, 1);
const city = new THREE.Group(); scene.add(city);
const kit = new Kit(), roadKit = new Kit(), markKit = new Kit();
for (const s of D.surfaces) (s.y > 0.01 ? kit : roadKit).floor(s.m, W1, 0, s.x0, s.z0, s.x1, s.z1, s.y);
for (const st of D.stripes) markKit.floor('#paint', W1, 0, st[0] - st[2] / 2, st[1] - st[3] / 2, st[0] + st[2] / 2, st[1] + st[3] / 2, 0.012);
for (const s of D.slabs) { kit.floor(s.top, W1, 0, s.x0, s.z0, s.x1, s.z1, s.h); kit.ring(s.side, W1, 0, s.x0, s.z0, s.x1, s.z1, 0, s.h, 0); }
for (const d of D.buildings) {
  const x0 = d.cx - d.sx / 2, x1 = d.cx + d.sx / 2, z0 = d.cz - d.sz / 2, z1 = d.cz + d.sz / 2, y0 = d.base, top = d.base + d.h;
  const band = d.gm ? d.band : 0;
  if (band > 0) kit.ring(d.gm, W1, 0, x0, z0, x1, z1, y0, y0 + band, y0, d.uo);
  const up = y0 + band, wt = top + d.parapet;
  kit.ring(d.um, W1, 0, x0, z0, x1, z1, up, wt, up, d.uo, d.vo);
  if (d.parapet > 0) {
    const i = 0.3;
    kit.ring(d.tm, W1, 0, x0 + i, z0 + i, x1 - i, z1 - i, top, wt, top, 0, 0, true);
    kit.floor(d.tm, W1, 0, x0, z0, x1, z0 + i, wt); kit.floor(d.tm, W1, 0, x0, z1 - i, x1, z1, wt);
    kit.floor(d.tm, W1, 0, x0, z0 + i, x0 + i, z1 - i, wt); kit.floor(d.tm, W1, 0, x1 - i, z0 + i, x1, z1 - i, wt);
    kit.floor(d.rm, W1, 0, x0 + i, z0 + i, x1 - i, z1 - i, top);
  } else kit.floor(d.rm, W1, 0, x0, z0, x1, z1, top);
  if (d.fascia && band > 0) {
    const fy0 = y0 + band - 0.55, fy1 = y0 + band, o = 0.22;
    kit.ring(d.tm, W1, 0, x0 - o, z0 - o, x1 + o, z1 + o, fy0, fy1, fy0);
    kit.floor(d.tm, W1, 0, x0 - o, z0 - o, x1 + o, z1 + o, fy1); kit.floor(d.tm, W1, 0, x0 - o, z0 - o, x1 + o, z1 + o, fy0, true);
  }
}
for (const b of D.boxes) kit.box(b.m, new THREE.Color(b.r, b.g, b.b), b.e, [b.cx, b.cy, b.cz], [b.sx, b.sy, b.sz], b.yaw);
await kit.build(city); await roadKit.build(city, false); await markKit.build(city, false);

// ---------------------------------------------------------------- sky (port of SanguineSky.shader)
const hdr = await new RGBELoader().setDataType(THREE.FloatType).loadAsync(RES + 'Sky/night_sky.hdr');
hdr.mapping = THREE.EquirectangularReflectionMapping;
{ const a = hdr.image.data; for (let i = 0; i < a.length; i++) if ((i & 3) !== 3) a[i] = Math.min(a[i], 40); hdr.needsUpdate = true; } // same clamp as SanguineSky.shader
const pmrem = new THREE.PMREMGenerator(renderer);
scene.environment = pmrem.fromEquirectangular(hdr).texture;
const skyMat = new THREE.ShaderMaterial({
  uniforms: { tex: { value: hdr }, exposure: { value: 0.06 }, glow: { value: new THREE.Vector3(0.06, 0.035, 0.02) }, fogColor: { value: fogColor }, fogAmt: { value: 0.85 } },
  vertexShader: `varying vec3 vDir; void main(){ vDir = position; gl_Position = projectionMatrix * modelViewMatrix * vec4(position,1.0); gl_Position.z = gl_Position.w; }`,
  fragmentShader: `uniform sampler2D tex; uniform float exposure; uniform vec3 glow; uniform vec3 fogColor; uniform float fogAmt; varying vec3 vDir;
    void main(){ vec3 d = normalize(vDir); d.z = -d.z; /* back to Unity space */
      float u = 0.5 + atan(d.x, d.z) / 6.28318530718; float v = 0.5 + asin(clamp(d.y,-1.0,1.0)) / 3.14159265359;
      vec3 c = texture2D(tex, vec2(u, v)).rgb * exposure;
      c += glow * pow(clamp(1.0 - abs(d.y) * 3.0, 0.0, 1.0), 3.0);
      c = mix(c, fogColor, fogAmt * pow(clamp(1.0 - abs(d.y) * 5.0, 0.0, 1.0), 2.0));
      gl_FragColor = vec4(c, 1.0); }`,
  side: THREE.BackSide, depthWrite: false, fog: false
});
const sky = new THREE.Mesh(new THREE.SphereGeometry(800, 64, 32), skyMat); sky.renderOrder = -1; scene.add(sky);

// ---------------------------------------------------------------- camera (LocalPlayer orbit)
const me = S.me;
const yaw = P.yaw * Math.PI / 180, pitch = P.pitch * Math.PI / 180;
const ground = (x, z) => { const lx = x + 200, lz = z + 200; if (lx < 0 || lz < 0) return 0; const fx = lx % 50, fz = lz % 50; return (fx >= 5 && fx <= 45 && fz >= 5 && fz <= 45 && lx < 400 && lz < 400) ? 0.15 : 0; };
const pivotU = [me.x, ground(me.x, me.z) + 1.8, me.z];
const insideBuilding = (x, z) => D.buildings.some(b => Math.abs(x - b.cx) < b.sx / 2 + 0.3 && Math.abs(z - b.cz) < b.sz / 2 + 0.3);
// Auto framing: stand on the street side of the player and look along the street towards the most lights.
function autoYaw() {
  const fx = ((me.x + 200) % 50 + 50) % 50, fz = ((me.z + 200) % 50 + 50) % 50;
  const dx = Math.min(fx, 50 - fx), dz = Math.min(fz, 50 - fz);
  let side, along;
  if (dz <= dx) { side = [0, fz < 25 ? -1 : 1]; along = [[1, 0], [-1, 0]]; } else { side = [fx < 25 ? -1 : 1, 0]; along = [[0, 1], [0, -1]]; }
  let best = null;
  for (const a of along) {
    const f = [a[0] * 0.8 - side[0] * 0.6, a[1] * 0.8 - side[1] * 0.6];
    const y = Math.atan2(f[0], f[1]);
    const score = D.lights.filter(l => { const vx = l.x - me.x, vz = l.z - me.z, d = Math.hypot(vx, vz); return d > 2 && d < 45 && (vx * a[0] + vz * a[1]) > 0; }).length;
    if (!best || score > best.score) best = { y, score };
  }
  return best.y;
}
const yawR = P.yaw === 'auto' ? autoYaw() : yaw;
const fwd = [Math.sin(yawR) * Math.cos(pitch), -Math.sin(pitch), Math.cos(yawR) * Math.cos(pitch)];
let dist = P.dist;
while (dist > 1.5 && insideBuilding(pivotU[0] - fwd[0] * dist, pivotU[2] - fwd[2] * dist)) dist -= 0.25; // camera collision
const camU = [pivotU[0] - fwd[0] * dist, pivotU[1] - fwd[1] * dist, pivotU[2] - fwd[2] * dist];
console.log('camera yaw', (yawR * 180 / Math.PI).toFixed(1), 'dist', dist.toFixed(2));
const cam = new THREE.PerspectiveCamera(60, W / H, 0.15, 900);
cam.position.copy(V(...camU)); cam.lookAt(V(...pivotU));
sky.position.copy(cam.position);
const camU2 = new THREE.Vector2(camU[0], camU[2]);

// ---------------------------------------------------------------- moonlight
const moonU = ART.sky.moonU, moonV = ART.sky.moonV;
const lon = (moonU - 0.5) * 2 * Math.PI, lat = (moonV - 0.5) * Math.PI;
const moonDirU = [Math.sin(lon) * Math.cos(lat), Math.sin(lat), Math.cos(lon) * Math.cos(lat)];
const moon = new THREE.DirectionalLight(col(0.55, 0.65, 0.95), 0.32 * 3.0);
moon.position.copy(V(me.x + moonDirU[0] * 150, moonDirU[1] * 150, me.z + moonDirU[2] * 150));
moon.target.position.copy(V(me.x, 0, me.z));
moon.castShadow = true; Object.assign(moon.shadow.camera, { left: -60, right: 60, top: 60, bottom: -60, near: 1, far: 400 });
moon.shadow.mapSize.set(4096, 4096); moon.shadow.bias = -0.0004; moon.shadow.normalBias = 0.03;
scene.add(moon, moon.target);
// Trilight ambient (Atmosphere.cs): sky / equator / ground; three's hemisphere light covers sky + ground.
scene.add(new THREE.HemisphereLight(col(0.13, 0.14, 0.19), col(0.045, 0.04, 0.038), 2.4));
scene.add(new THREE.AmbientLight(col(0.12, 0.105, 0.095), 0.9));

// ---------------------------------------------------------------- props (Poly Haven GLB)
const gltf = new GLTFLoader(); const propCache = {};
async function prop(id) { if (!(id in propCache)) propCache[id] = gltf.loadAsync(RES + 'Props/' + id + '.glb').then(g => { g.scene.traverse(o => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; if (o.material) o.material.envMapIntensity = ENV_NIGHT; } }); return g.scene; }).catch(() => null); return propCache[id]; }
for (const p of D.props) {
  if (Math.hypot(p.x - camU[0], p.z - camU[2]) > P.radius) continue;
  const src = await prop(p.id); if (!src) continue;
  const outer = new THREE.Group(); outer.position.copy(V(p.x, p.y, p.z)); outer.rotation.y = -p.yaw * Math.PI / 180;
  const inner = src.clone(); inner.rotation.y = Math.PI; inner.scale.set(p.sx, p.sy, p.sz); // glTF -> Unity (glTFast mirrors X) -> three
  if (p.id === 'street_lamp' || p.id === 'security_light') inner.traverse(o => { if (o.isMesh) o.castShadow = false; }); // as CityBuilder: lights inside lanterns
  outer.add(inner); city.add(outer);
}

// ---------------------------------------------------------------- lights (nearest N, as CityLights keeps nearby lights active)
const lights = D.lights.map(l => ({ l, d: Math.hypot(l.x - me.x, l.z - me.z) })).sort((a, b) => a.d - b.d).slice(0, P.lights);
lights.forEach(({ l }, i) => {
  const pl = new THREE.PointLight(col(l.r, l.g, l.b), l.i * 22, l.range * 1.4, 1.6);
  pl.position.copy(V(l.x, l.y, l.z));
  if (i < 2 && (l.k === 0 || l.k === 1)) { pl.castShadow = true; pl.shadow.mapSize.set(1024, 1024); pl.shadow.bias = -0.002; }
  scene.add(pl);
});

// ---------------------------------------------------------------- characters (port of ProceduralFigure)
const bloodlines = [[0.75, 0.25, 0.18], [0.85, 0.35, 0.65], [0.45, 0.3, 0.8], [0.9, 0.75, 0.3], [0.3, 0.62, 0.66], [0.47, 0.68, 0.3]];
const G = { cap: new THREE.CapsuleGeometry(0.5, 1, 6, 12), sph: new THREE.SphereGeometry(0.5, 20, 14), cyl: new THREE.CylinderGeometry(0.5, 0.5, 2, 16), cube: new THREE.BoxGeometry(1, 1, 1) };
const flat = {}; const fm = (c, s = 0.25, em = false) => { const k = c.getHexString() + s + em; if (!flat[k]) flat[k] = new THREE.MeshStandardMaterial({ color: c, roughness: 1 - s, emissive: em ? c : 0x000000, emissiveIntensity: em ? 2 : 0, envMapIntensity: ENV_NIGHT }); return flat[k]; };
function prim(geo, parent, pos, scale, c, s, em) { const m = new THREE.Mesh(G[geo], fm(c, s, em)); m.position.set(pos[0], pos[1], -pos[2]); m.scale.set(...scale); m.castShadow = true; m.receiveShadow = true; parent.add(m); return m; }
const joint = (parent, pos) => { const g = new THREE.Group(); g.position.set(pos[0], pos[1], -pos[2]); parent.add(g); return g; };
const seg = (j, len, r, c) => prim('cap', j, [0, -len / 2, 0], [r * 2, len * 0.5 + r * 0.3, r * 2], c, 0.25);
const hsv = (h, s, v) => { const i = Math.floor(h * 6), f = h * 6 - i, p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s); const r = [[v, t, p], [q, v, p], [p, v, t], [p, q, v], [t, p, v], [v, p, q]][i % 6]; return col(r[0], r[1], r[2]); };
const lerpC = (a, b, t) => a.clone().lerp(b, t);
// Euler X rotation in Unity (top toward +Z) -> three after Z mirror: negate the angle.
const rx = (o, deg) => { o.rotation.x = -deg * Math.PI / 180; };
function figure(role, seed, bl, pose) {
  const rng = new Rng(seed * 31 + 7);
  let skin, top, legs, coat = null, hair; const shoes = col(0.05, 0.05, 0.055);
  const build = rng.rangeF(0.92, 1.08), height = rng.rangeF(0.95, 1.06);
  if (role === 'vampire') { const bc = col(...bloodlines[bl]); skin = col(0.86, 0.82, 0.84); top = lerpC(bc, col(0.05, 0.04, 0.05), 0.65); legs = col(0.06, 0.06, 0.07); coat = lerpC(col(0.04, 0.035, 0.04), bc, 0.18); hair = col(0.04, 0.03, 0.03); }
  else if (role === 'hunter') { skin = lerpC(col(0.9, 0.74, 0.62), col(0.45, 0.3, 0.2), rng.nextFloat()); top = col(0.2, 0.22, 0.18); legs = col(0.12, 0.13, 0.11); coat = col(0.24, 0.18, 0.11); hair = col(0.12, 0.09, 0.06); }
  else { skin = lerpC(col(0.95, 0.8, 0.7), col(0.36, 0.24, 0.16), rng.nextFloat()); top = hsv(rng.nextFloat(), rng.rangeF(0.15, 0.55), rng.rangeF(0.18, 0.6)); legs = hsv(rng.rangeF(0.55, 0.7), rng.rangeF(0.1, 0.5), rng.rangeF(0.08, 0.35)); if (rng.nextFloat() < 0.35) coat = hsv(rng.nextFloat(), rng.rangeF(0.1, 0.35), rng.rangeF(0.12, 0.3)); const t = rng.nextFloat() * rng.nextFloat(); hair = lerpC(col(0.06, 0.04, 0.03), col(0.6, 0.45, 0.25), t); }
  const root = new THREE.Group(); root.scale.set(build, height, build);
  const hips = joint(root, [0, 0.93, 0]); prim('cap', hips, [0, 0.02, 0], [0.34, 0.12, 0.22], legs);
  const spine = joint(hips, [0, 0.08, 0]); prim('cap', spine, [0, 0.25, 0], [0.38, 0.27, 0.23], top);
  const neck = joint(spine, [0, 0.52, 0]); prim('cyl', neck, [0, 0.04, 0], [0.1, 0.05, 0.1], skin);
  const head = joint(neck, [0, 0.17, 0.01]); prim('sph', head, [0, 0, 0], [0.19, 0.235, 0.21], skin, 0.35); prim('sph', head, [0, 0.045, -0.015], [0.2, 0.17, 0.21], hair);
  const eye = role === 'vampire' ? col(0.9, 0.08, 0.08) : col(0.06, 0.05, 0.05);
  prim('sph', head, [-0.038, 0.015, 0.092], [0.026, 0.026, 0.026], eye, 0.3, role === 'vampire'); prim('sph', head, [0.038, 0.015, 0.092], [0.026, 0.026, 0.026], eye, 0.3, role === 'vampire');
  const sleeve = coat || top;
  const lU = joint(spine, [-0.22, 0.47, 0]); seg(lU, 0.29, 0.055, sleeve); const lF = joint(lU, [0, -0.29, 0]); seg(lF, 0.26, 0.048, sleeve); prim('sph', lF, [0, -0.3, 0], [0.07, 0.09, 0.05], skin);
  const rU = joint(spine, [0.22, 0.47, 0]); seg(rU, 0.29, 0.055, sleeve); const rF = joint(rU, [0, -0.29, 0]); seg(rF, 0.26, 0.048, sleeve); prim('sph', rF, [0, -0.3, 0], [0.07, 0.09, 0.05], skin);
  const rHand = joint(rF, [0, -0.31, 0.02]);
  const lT = joint(hips, [-0.095, 0, 0]); seg(lT, 0.45, 0.075, legs); const lS = joint(lT, [0, -0.45, 0]); seg(lS, 0.43, 0.058, legs); prim('cube', lS, [0, -0.45, 0.05], [0.1, 0.07, 0.26], shoes);
  const rT = joint(hips, [0.095, 0, 0]); seg(rT, 0.45, 0.075, legs); const rS = joint(rT, [0, -0.45, 0]); seg(rS, 0.43, 0.058, legs); prim('cube', rS, [0, -0.45, 0.05], [0.1, 0.07, 0.26], shoes);
  if (coat) { prim('cap', spine, [0, 0.24, 0], [0.41, 0.29, 0.26], coat); prim('cyl', hips, [0, -0.26, 0], [0.42, 0.27, 0.3], coat); prim('cube', spine, [0, 0.5, -0.02], [0.3, 0.1, 0.2], coat); }
  // Pose (same formulas as ProceduralFigure.Animate at a sampled phase).
  const speed = pose.speed, feeding = pose.feeding, move = Math.min(1, speed / 1.3), running = speed > 3;
  const ph = pose.phase, s = Math.sin(ph), c = Math.cos(ph);
  const legAmp = (running ? 42 : 28) * move, armAmp = (running ? 50 : 24) * move;
  const lean = feeding ? 28 : (running ? 12 : 3 * move);
  rx(lT, -s * legAmp + (feeding ? -12 : 0)); rx(rT, s * legAmp + (feeding ? 8 : 0));
  rx(lS, Math.max(0, c) * legAmp * 1.3 + (feeding ? 18 : 0)); rx(rS, Math.max(0, -c) * legAmp * 1.3 + (feeding ? 10 : 0));
  // Unity Euler order is Z, X, Y (= three 'YXZ'); the Z mirror flips X/Y angles but keeps Z angles.
  lU.rotation.order = rU.rotation.order = 'YXZ';
  if (feeding) { rx(lU, -70); lU.rotation.z = 14 * Math.PI / 180; rx(rU, -65); rU.rotation.z = -14 * Math.PI / 180; rx(lF, -35); rx(rF, -40); }
  else { rx(lU, s * armAmp); lU.rotation.z = 6 * Math.PI / 180; rx(rU, -s * armAmp); rU.rotation.z = -6 * Math.PI / 180; rx(lF, -(running ? 70 : 12 + 10 * move)); rx(rF, -(running ? 70 : 12 + 10 * move)); }
  rx(spine, lean); rx(neck, feeding ? 22 : -lean * 0.4);
  hips.position.y = 0.93 - (feeding ? 0.1 : 0) + Math.abs(s) * (running ? 0.05 : 0.025) * move;
  if (pose.down) { rx(root, -88); root.position.y = 0.16; }
  return { root, head, rHand };
}
function placeFigure(f, x, z, rotDeg) { const g = new THREE.Group(); g.position.copy(V(x, ground(x, z), z)); g.rotation.y = -rotDeg * Math.PI / 180; g.add(f.root); scene.add(g); return g; }

const views = [];
for (const e of S.entities) {
  const role = e.kind === 0 ? 'vampire' : (e.kind === 2 ? 'hunter' : 'mortal');
  const alarmed = (e.flags & 4) !== 0, fed = (e.flags & 2) !== 0, down = (e.flags & 1) !== 0;
  const speed = role === 'hunter' ? 5.2 : (role === 'vampire' ? 0 : (alarmed ? 4.2 : (fed ? 0 : 1.4)));
  const f = figure(role, e.id, e.bloodline, { speed, feeding: false, down, phase: (e.id * 1.7) % 6.283 });
  placeFigure(f, e.x, e.z, e.rot);
  if (role === 'hunter') { // flashlight
    const spot = new THREE.SpotLight(col(0.9, 0.95, 1), 60, 28, 38 * Math.PI / 360, 0.4, 1.6);
    const base = V(e.x, 1.35, e.z); spot.position.copy(base);
    const a = e.rot * Math.PI / 180; spot.target.position.copy(V(e.x + Math.sin(a) * 6, 0, e.z + Math.cos(a) * 6));
    scene.add(spot, spot.target);
  }
  views.push(e);
}
// Local player feeding (Selene), with the red "marked" light when MARKED.
const selene = figure('vampire', me.id, me.bloodline, { speed: 0, feeding: (S.self.flags & 1024) !== 0, down: false, phase: 0 });
placeFigure(selene, me.x, me.z, me.rot);
if (S.self.flags & 1) { const ml = new THREE.PointLight(0xff0000, 12, 6, 1.6); ml.position.copy(V(me.x, ground(me.x, me.z) + 2.6, me.z)); scene.add(ml); }
// Target ring (GameClient.UpdateTargetRing).
const tgt = S.entities.find(e => e.id === me.target);
if (tgt) { const ring = new THREE.Mesh(G.cyl, new THREE.MeshStandardMaterial({ color: 0x220000, emissive: col(0.9, 0.1, 0.15), emissiveIntensity: 1.6 })); ring.scale.set(1.6, 0.01, 1.6); ring.position.copy(V(tgt.x, ground(tgt.x, tgt.z) + 0.03, tgt.z)); scene.add(ring); }

// ---------------------------------------------------------------- render with bloom
const composer = new EffectComposer(renderer);
composer.addPass(new RenderPass(scene, cam));
composer.addPass(new UnrealBloomPass(new THREE.Vector2(W, H), 0.32, 0.5, 0.9));
composer.addPass(new OutputPass());
await Promise.all(texPending); // render only once every texture has loaded
composer.render();

// ---------------------------------------------------------------- HUD (ClientUI layout)
const hud = document.getElementById('hud');
const el = (html, style) => { const d = document.createElement('div'); d.innerHTML = html; d.style.cssText = 'position:absolute;' + style; hud.appendChild(d); return d; };
const project = (x, y, z) => { const v = V(x, y, z).project(cam); return { x: (v.x + 1) / 2 * W, y: (1 - v.y) / 2 * H, ok: v.z < 1 }; };
const hex = c => '#' + c.getHexString();
for (const e of views) {
  const dd = Math.hypot(e.x - me.x, e.z - me.z); if (dd > 35 && e.kind !== 0) continue;
  const sp = project(e.x, ground(e.x, e.z) + 2.7, e.z); if (!sp.ok) continue;
  let text = e.name; if (e.kind === 0 && e.circle) text += ' &lt;' + e.circle + '&gt;';
  const c = e.kind === 2 ? 'rgb(255,153,51)' : e.kind === 1 ? ((e.flags & 4) ? 'rgb(255,230,102)' : 'rgb(204,204,204)') : (e.flags & 8 ? 'rgb(255,64,64)' : hex(lerpC(col(...bloodlines[e.bloodline]), new THREE.Color(1, 1, 1), 0.35)));
  el(text, `left:${sp.x - 120}px;top:${sp.y - 10}px;width:240px;text-align:center;font:bold 12px Arial;color:${c};text-shadow:1px 1px 0 #000;white-space:nowrap`);
  if (e.id === me.target || e.hp < 255) el('', `left:${sp.x - 25}px;top:${sp.y + 10}px;width:${50 * e.hp / 255}px;height:3px;background:rgb(230,38,38)`);
}
const s = S.self, w = S.world;
const bar = (x, y, wd, h, v, max, color, text) => el(`<div style="position:absolute;left:1px;top:1px;height:${h - 2}px;width:${(wd - 2) * Math.max(0, Math.min(1, v / max))}px;background:${color}"></div><div style="position:absolute;width:100%;top:0;line-height:${h}px;text-align:center">${text}</div>`, `left:${x}px;top:${y}px;width:${wd}px;height:${h}px;background:rgba(0,0,0,.6)`);
el('', 'left:10px;top:10px;width:290px;height:248px;background:rgba(0,0,0,.5)');
el(`<b>${me.name}</b>&nbsp; <span style="color:${hex(col(...bloodlines[me.bloodline]))}">Sylphine</span><div style="font-size:12px;margin-top:3px">Midtown</div><div style="font-size:12px;margin-top:3px"><span style="color:#ffd080">Fledgling</span> <span style="color:#999">(L: lineage)</span></div>`, 'left:18px;top:16px;width:274px');
const y0 = 80;
bar(18, y0, 274, 18, s.blood, s.maxBlood, 'rgb(166,5,13)', `Blood ${s.blood.toFixed(1)} / ${s.maxBlood}`);
bar(18, y0 + 22, 274, 18, s.health, s.maxHealth, 'rgb(64,128,77)', `Health ${Math.ceil(s.health)} / ${s.maxHealth}`);
bar(18, y0 + 44, 274, 18, s.exposure, 100, s.exposure >= 80 ? 'red' : s.exposure >= 50 ? 'rgb(255,140,0)' : 'rgb(179,153,51)', `Exposure ${Math.round(s.exposure)}%`);
el('', `left:155px;top:${y0 + 44}px;width:1px;height:18px;background:#fff`); el('', `left:237px;top:${y0 + 44}px;width:1px;height:18px;background:red`);
bar(18, y0 + 66, 274, 18, s.conscience, 10, 'rgb(77,102,191)', `Conscience ${s.conscience.toFixed(1)} / 10`);
el(`Standing <b>${s.standing}</b>`, `left:18px;top:${y0 + 88}px`);
const flags = []; if (s.flags & 1) flags.push('<span style="color:#ff4040">MARKED</span>'); if (s.flags & 1024) flags.push('<span style="color:#ff9090">Feeding</span>');
el(flags.join('&nbsp; '), `left:18px;top:${y0 + 108}px;font-size:12px`);
const secs = Math.round(w.toChange), tt = Math.floor(secs / 60) + ':' + String(secs % 60).padStart(2, '0');
el(`<b>Night ${w.night}</b> - dawn in ${tt}<br>${w.regent ? 'Regent: <span style="color:#ffcc33">' + w.regent + '</span>' : 'The city has no Regent'}`, `left:${W / 2 - 220}px;top:8px;width:440px;height:64px;background:rgba(0,0,0,.45);text-align:center;padding-top:6px;box-sizing:border-box`);
const dn = ['The Harbor', 'Midtown', 'Old Quarter', 'Financial Row'];
el(`<b>Districts</b><br>${dn.map((n, i) => `${i + 1}. ${n}: ${w.controllers[i] ? '<span style="color:#ffd080">' + w.controllers[i] + '</span>' : '<span style="color:#888">contested</span>'}`).join('<br>')}<br><b>Circle:</b> ${w.circle || '<span style="color:#888">none</span>'}<br><b>Election</b> (at dawn): no votes yet`, `left:${W - 270}px;top:10px;width:260px;height:190px;background:rgba(0,0,0,.5);font-size:12px;padding:4px 8px;box-sizing:border-box;line-height:17px`);
if (tgt) { el(`<div style="text-align:center;margin-top:3px"><b>${tgt.name}</b> (Mortal) ${Math.hypot(tgt.x - me.x, tgt.z - me.z).toFixed(1)}m</div>`, `left:${W / 2 - 160}px;top:80px;width:320px;height:54px;background:rgba(0,0,0,.5)`); bar(W / 2 - 150, 108, 300, 16, tgt.hp, 255, 'rgb(179,26,26)', ''); }
const levelColor = n => /MARKED|hunter/i.test(n) ? 'rgb(255,77,77)' : 'rgb(242,242,242)';
S.notices.slice(-3).forEach((n, i) => el(n, `left:0;top:${H * 0.28 + i * 26}px;width:${W}px;text-align:center;font:bold 18px Arial;color:${levelColor(n)};text-shadow:1px 1px 0 #000`));
// Action bar (ClientUI.DrawActionBar): feed, strike, the bloodline's three gifts, gifts, thralls, lineage, help.
const GIFTS = [[['Surge', 2, 0], ['Iron Skin', 2, 5], ['Earthshaker', 3, 12]], [['Allure', 1, 0], ['Glamour', 2, 5], ['Rapture', 3, 12]],
               [['Blood Lash', 2, 0], ['Siphon', 1, 5], ['Crimson Mist', 3, 12]], [['Command', 2, 0], ['Majesty', 2, 5], ['Dominate', 3, 12]],
               [['Shroud', 2, 0], ['Whispers', 2, 5], ['Undercroft', 3, 12]], [['Pounce', 2, 0], ['Pack Howl', 2, 5], ['Unchained', 3, 12]]][me.bloodline];
const learned = S.self.gifts || 1;
const bar8 = ['[F] Release', '[1] Strike',
  ...GIFTS.map((g, i) => `[${i + 2}] ${g[0]}<br><span style="font-size:10px">${(learned >> i) & 1 ? g[1] + ' blood' : '<span style="color:#ff9060">locked: ' + g[2] + ' standing</span>'}</span>`),
  '[G] Gifts', '[T] Thralls', '[L] Lineage', '[H] Help'];
bar8.forEach((b, i) => el(`<div class="btn">${b}</div>`, `left:${W / 2 - (104 * 9 + 5 * 8) / 2 + i * 109}px;top:${H - 58}px;width:104px;height:48px`));
el('', `left:10px;top:${H - 300}px;width:460px;height:220px;background:rgba(0,0,0,.35)`);
el(S.chat.slice(-11).map(c => { const m = c.match(/^([A-Za-z0-9_]+): (.*)$/); return m ? `<span style="color:#e0e0e0"><b>${m[1]}</b>: ${m[2]}</span>` : `<span style="color:#c0a0ff">${c.replace(/</g, '&lt;')}</span>`; }).join('<br>'), `left:16px;top:${H - 296}px;width:448px;font-size:12px;line-height:16px`);
el('<span style="color:#999">Press Enter to chat. /help for commands.</span>', `left:10px;top:${H - 76}px;font-size:12px`);
window.DONE = true;
