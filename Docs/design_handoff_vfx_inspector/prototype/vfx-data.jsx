/* ============================================================================
   VFX Graph data model — a believable "Visual Effect" component instance.
   One .vfx asset with exposed properties grouped by category. Shared across
   all three inspector variations so they show the same effect.
   ============================================================================ */

// category meta: id, label, a muted Unity-friendly accent dot color
const VFX_CATEGORIES = [
  { id: 'spawn',   label: 'Spawn',            dot: '#c98a3a' },
  { id: 'color',   label: 'Color & Lighting', dot: '#c95a4a' },
  { id: 'motion',  label: 'Shape & Motion',   dot: '#4a8ac9' },
  { id: 'size',    label: 'Size & Lifetime',  dot: '#7a9a4a' },
  { id: 'texture', label: 'Texture & Render', dot: '#8a6ac9' },
];

// type ∈ float | int | bool | color | gradient | vector3 | curve | object | enum
// each prop: id, label, cat, type, value, def, fav, [min,max], [enum opts]
const VFX_PROPS = [
  // Spawn
  { id: 'spawnRate', label: 'Spawn Rate',    cat: 'spawn', type: 'float', value: 240, def: 120, min: 0,  max: 500, fav: true,  unit: '/s' },
  { id: 'burst',     label: 'Burst Count',   cat: 'spawn', type: 'int',   value: 64,  def: 64,  min: 0,  max: 256, fav: false },
  { id: 'looping',   label: 'Looping',       cat: 'spawn', type: 'bool',  value: true, def: true, fav: false },
  { id: 'prewarm',   label: 'Prewarm',       cat: 'spawn', type: 'float', value: 0.5, def: 0,   min: 0,  max: 4,   fav: false, unit: 's' },

  // Color & Lighting
  { id: 'baseColor', label: 'Base Color',    cat: 'color', type: 'gradient', value: ['#3a1402','#ff7a1a','#ffd84a','#ffffff'], def: ['#3a1402','#ff7a1a','#ffd84a','#ffffff'], fav: false },
  { id: 'tint',      label: 'Tint',          cat: 'color', type: 'color', value: '#ff7a1a', def: '#ffffff', fav: true },
  { id: 'emission',  label: 'Emission',      cat: 'color', type: 'float', value: 6.0, def: 4.0, min: 0, max: 12, fav: true, unit: '×' },
  { id: 'useHDR',    label: 'HDR Bloom',     cat: 'color', type: 'bool',  value: true, def: true, fav: false },

  // Shape & Motion
  { id: 'radius',    label: 'Emitter Radius',cat: 'motion', type: 'float', value: 0.35, def: 0.5, min: 0, max: 3, fav: false, unit: 'm' },
  { id: 'cone',      label: 'Cone Angle',    cat: 'motion', type: 'float', value: 18,  def: 25,  min: 0, max: 90, fav: false, unit: '°' },
  { id: 'speed',     label: 'Initial Speed', cat: 'motion', type: 'float', value: 7.5, def: 6.0, min: 0, max: 20, fav: true,  unit: 'm/s' },
  { id: 'gravity',   label: 'Gravity',       cat: 'motion', type: 'vector3', value: [0, -2.4, 0], def: [0, -2, 0], fav: false },
  { id: 'turbulence',label: 'Turbulence',    cat: 'motion', type: 'float', value: 1.2, def: 1.0, min: 0, max: 5, fav: false },
  { id: 'drag',      label: 'Drag',          cat: 'motion', type: 'float', value: 0.4, def: 0.4, min: 0, max: 2, fav: false },

  // Size & Lifetime
  { id: 'lifetime',  label: 'Lifetime',      cat: 'size', type: 'float', value: 1.8, def: 2.0, min: 0.1, max: 6, fav: false, unit: 's' },
  { id: 'startSize', label: 'Start Size',    cat: 'size', type: 'float', value: 0.28, def: 0.25, min: 0, max: 2, fav: false, unit: 'm' },
  { id: 'sizeCurve', label: 'Size over Life',cat: 'size', type: 'curve', value: 'easeOut', def: 'easeOut', fav: false },
  { id: 'sizeVar',   label: 'Size Variation',cat: 'size', type: 'float', value: 0.35, def: 0.3, min: 0, max: 1, fav: false },

  // Texture & Render
  { id: 'flipbook',  label: 'Flipbook',      cat: 'texture', type: 'object', value: 'T_Fire_8x8', icon: 'material', def: 'T_Fire_8x8', fav: false },
  { id: 'fps',       label: 'Frames / sec',  cat: 'texture', type: 'float', value: 24, def: 24, min: 1, max: 60, fav: false },
  { id: 'blendMode', label: 'Blend Mode',    cat: 'texture', type: 'enum',  value: 'Additive', def: 'Alpha', opts: ['Alpha','Additive','Multiply'], fav: false },
  { id: 'softParts', label: 'Soft Particles',cat: 'texture', type: 'bool',  value: true, def: false, fav: false },
];

const VFX_ASSET = {
  name: 'VFX_Fireball',
  file: 'VFX_Fireball.vfx',
  initialEvent: 'OnPlay',
  seed: 48211,
  systems: [
    { name: 'Embers',  alive: 1840, cap: 4096 },
    { name: 'Flame',   alive: 612,  cap: 1024 },
    { name: 'Smoke',   alive: 318,  cap: 512 },
  ],
};

// live-ish debug stats
const VFX_STATS = {
  aliveParticles: 2770,
  capacity: 5632,
  spawnPerSec: 240,
  gpuTimeMs: 0.42,
  drawCalls: 3,
  textureMem: '5.1 MB',
  bounds: [2.4, 3.1, 2.4],
};

// custom events the artist can fire
const VFX_EVENTS = ['OnPlay', 'OnStop', 'Burst', 'Ignite', 'Extinguish'];

Object.assign(window, { VFX_CATEGORIES, VFX_PROPS, VFX_ASSET, VFX_STATS, VFX_EVENTS });
