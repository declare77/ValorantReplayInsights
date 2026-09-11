'use strict';

/*
 * VALORANT 2D Replay Viewer
 *
 * Reads the JSON this project's analysis pipeline already produces (match.json, movement.json,
 * and friends) and plays it back on the real minimap. Everything here runs entirely in your
 * browser against files you pick from disk — nothing is uploaded anywhere, and this page never
 * touches a .vrf file or vrfkit itself.
 *
 * Two things worth knowing before trusting what's drawn here:
 *  - The coordinate transform (world X/Y -> minimap pixel) uses Riot's own published per-map
 *    xMultiplier/yMultiplier/xScalarToAdd/yScalarToAdd fields (from valorant-api.com), exactly as
 *    published. That formula is documented, not guessed -- but it hasn't been independently
 *    checked here pixel-by-pixel against a real replay overlaid on a real minimap image.
 *  - Utility shapes (smoke/molly radius, wall length) are fixed visual approximations -- the
 *    replay doesn't carry an effect's actual size, only its spawn point (and, for an actor with
 *    one, its spawn yaw). Treat them as "roughly here, roughly this categoryof thing," not
 *    measured geometry.
 */

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const REQUIRED_FILES = ['match.json', 'movement.json'];

const PLAYER_COLORS = [
  '#4d9fff', '#ff5c5c', '#4ade80', '#facc15', '#c084fc',
  '#22d3ee', '#fb923c', '#f472b6', '#a3e635', '#94a3b8',
];

const UTILITY_COLORS = {
  Smoke: 'rgba(200,200,200,0.55)',
  IncendiaryOrMolly: 'rgba(255,120,40,0.65)',
  Wall: 'rgba(180,120,255,0.9)',
  TrapOrMine: 'rgba(255,220,60,0.9)',
  Turret: 'rgba(255,70,70,0.9)',
  ProjectileOrGrenade: 'rgba(80,220,220,0.9)',
  DroneOrDeployable: 'rgba(60,200,160,0.9)',
};

// Visual approximations only -- see file header. Units are VALORANT/Unreal world units.
const UTILITY_AREA_RADIUS_UNITS = 350;   // smokes / mollies
const WALL_HALF_LENGTH_UNITS = 400;
const ABILITY_MARKER_FADE_MS = 1500;
const AGENT_ICON_RADIUS_PX = 12;
const FACING_LOOKAHEAD_UNITS = 220;

// Nudge via the "Facing offset" control in the UI if a facing arrow looks rotated/mirrored on a
// particular map -- it hasn't been needed on any export this project has been checked against.
let facingOffsetDeg = 0;

// Map orientation correction --------------------------------------------------------------
// Riot's published xMultiplier/yMultiplier/xScalarToAdd/yScalarToAdd formula (see file header)
// hasn't been independently checked pixel-by-pixel against every map's actual minimap art, and in
// practice a map's locally-downloaded image isn't always oriented the way that formula assumes --
// this shows up as everything appearing rotated 90°/180° from reality (e.g. two teams' spawns
// landing on the left/right of the image when the map itself has them on the top/bottom). Rather
// than guess which maps need what, this is a live, per-map-remembered correction (same idea as
// the facing-offset control above) applied in screen space, after the world->uv transform, so it
// never has to touch the documented formula itself.
// scale/offsetX/offsetY exist for a related but distinct problem: rotate/flip alone assume the
// downloaded image's content fills the exact same [0,1] square Riot's multiplier/scalar formula
// was calibrated against. If this specific image has different padding/cropping around the actual
// playable area (very plausible -- valorant-api.com's "displayIcon" is a separate asset from
// whatever Riot's own client renders internally, not guaranteed pixel-identical), everything can
// end up pointed the right general direction after rotate/flip but still land off-center relative
// to the actual rooms -- scale zooms in/out around the image center, offsetX/offsetY pan, both in
// normalized [0,1] units, to compensate.
const mapOrientation = { rotate: 0, flipH: false, scale: 1, offsetX: 0, offsetY: 0 };

function orientationStorageKey(mapUuid) { return 'vrf-map-orientation:' + mapUuid; }

function defaultOrientation() { return { rotate: 0, flipH: false, scale: 1, offsetX: 0, offsetY: 0 }; }

function loadMapOrientation(mapUuid) {
  const fallback = defaultOrientation();
  try {
    const raw = mapUuid ? localStorage.getItem(orientationStorageKey(mapUuid)) : null;
    if (!raw) return fallback;
    const parsed = JSON.parse(raw);
    return {
      rotate: Number(parsed.rotate) || 0,
      flipH: !!parsed.flipH,
      scale: Number(parsed.scale) || 1,
      offsetX: Number(parsed.offsetX) || 0,
      offsetY: Number(parsed.offsetY) || 0,
    };
  } catch { return fallback; }
}

function saveMapOrientation(mapUuid) {
  if (!mapUuid) return;
  try { localStorage.setItem(orientationStorageKey(mapUuid), JSON.stringify(mapOrientation)); }
  catch { /* private-mode / storage disabled -- orientation just won't be remembered next time */ }
}

/** Rotates/flips/scales/pans a normalized (u,v) point in [0,1]. Screen-space only -- independent
 * of worldToUv's world-unit math, so it corrects mismatched minimap art without needing to touch
 * (or understand) Riot's own coordinate formula. Order matters: flip and rotate first (they're
 * about the image's orientation), then scale and pan (they're about the image's crop/framing). */
function applyOrientation(u, v) {
  let x = u - 0.5, y = v - 0.5;
  if (mapOrientation.flipH) x = -x;
  switch (((mapOrientation.rotate % 360) + 360) % 360) {
    case 90: { const nx = -y, ny = x; x = nx; y = ny; break; }
    case 180: { x = -x; y = -y; break; }
    case 270: { const nx = y, ny = -x; x = nx; y = ny; break; }
    default: break;
  }
  const scale = mapOrientation.scale || 1;
  x *= scale;
  y *= scale;
  x += mapOrientation.offsetX || 0;
  y += mapOrientation.offsetY || 0;
  return { u: x + 0.5, v: y + 0.5 };
}

// Map calibration --------------------------------------------------------------------------
// An alternative to guessing rotate/flip/scale/pan by hand: click a few spots on the map where
// you can positively identify a player's real location, and fit a general affine transform
// (world x,y -> normalized u,v) directly from those correspondences via least squares. This is
// strictly more capable than the manual sliders above (it also corrects shear/non-uniform
// scaling, which rotate+scale+pan can't) and, being a property of the map image/data pairing
// rather than any one replay, only has to be done once per map -- saved per map in localStorage,
// same as the manual controls, and takes priority over them in toPixel() whenever present.
let mapCalibration = null;       // { A,B,C,D,E,F, points: [...] } for the current map, or null
let calibrationPoints = [];      // points collected in the current (possibly not-yet-saved) session
let calibratePicking = false;    // true while armed, waiting for the next canvas click

function calibrationStorageKey(mapUuid) { return 'vrf-map-calibration:' + mapUuid; }

function loadMapCalibration(mapUuid) {
  if (!mapUuid) return null;
  try {
    const raw = localStorage.getItem(calibrationStorageKey(mapUuid));
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (typeof parsed.A !== 'number' || !Array.isArray(parsed.points)) return null;
    return parsed;
  } catch { return null; }
}

function saveMapCalibration(mapUuid, calibration) {
  if (!mapUuid) return;
  try { localStorage.setItem(calibrationStorageKey(mapUuid), JSON.stringify(calibration)); }
  catch { /* private-mode / storage disabled -- calibration just won't be remembered next time */ }
}

function clearMapCalibrationStorage(mapUuid) {
  if (!mapUuid) return;
  try { localStorage.removeItem(calibrationStorageKey(mapUuid)); } catch { /* ignore */ }
}

function det3(m) {
  return m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
       - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
       + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]);
}

/** Solves the 3x3 linear system M*p = b via Cramer's rule. Returns null if M is singular (e.g.
 * every point fed to solveAffine lies on the same line, or all points are the same). */
function solve3x3(M, b) {
  const det = det3(M);
  if (Math.abs(det) < 1e-9) return null;
  const withCol = (col) => M.map((row, i) => row.map((v, j) => (j === col ? b[i] : v)));
  return [det3(withCol(0)) / det, det3(withCol(1)) / det, det3(withCol(2)) / det];
}

/** Least-squares fit of world (x,y) -> normalized (u,v) as an affine transform
 * (u = A*x + B*y + C, v = D*x + E*y + F) from >=3 point correspondences. Returns null if the
 * points are degenerate (collinear, or fewer than 3 distinct locations). */
function solveAffine(points) {
  if (points.length < 3) return null;

  // World coordinates (PosX/PosY) are typically several thousand units, and feeding those
  // straight into the normal-equations matrix below (which involves their squares and a lone "1"
  // per point for the constant term) makes it extremely poorly conditioned -- entries spanning
  // many orders of magnitude in the same matrix. That can produce a wildly wrong fit even though
  // it satisfies the exact points used to compute it (most visible right at the 3-point minimum,
  // where the fit passes through those points exactly no matter how unstable it is elsewhere).
  // Centering on the points' own mean and scaling to roughly [-1,1] first keeps the matrix
  // well-conditioned; the result is converted back to plain world-unit coefficients before
  // returning, so callers never see the normalized space.
  const n = points.length;
  let mx = 0, my = 0;
  for (const p of points) { mx += p.x; my += p.y; }
  mx /= n; my /= n;

  let s = 1; // avoid divide-by-zero; a true zero-spread case is already caught as degenerate below
  for (const p of points) {
    s = Math.max(s, Math.abs(p.x - mx), Math.abs(p.y - my));
  }

  let Sxx = 0, Sxy = 0, Sx = 0, Syy = 0, Sy = 0;
  let Sxu = 0, Syu = 0, Su = 0, Sxv = 0, Syv = 0, Sv = 0;
  for (const p of points) {
    const nx = (p.x - mx) / s, ny = (p.y - my) / s;
    Sxx += nx * nx; Sxy += nx * ny; Sx += nx;
    Syy += ny * ny; Sy += ny;
    Sxu += nx * p.u; Syu += ny * p.u; Su += p.u;
    Sxv += nx * p.v; Syv += ny * p.v; Sv += p.v;
  }
  const M = [[Sxx, Sxy, Sx], [Sxy, Syy, Sy], [Sx, Sy, n]];
  const abc = solve3x3(M, [Sxu, Syu, Su]);
  const def = solve3x3(M, [Sxv, Syv, Sv]);
  if (!abc || !def) return null;

  // Undo the centering/scaling: with nx=(x-mx)/s, ny=(y-my)/s,
  // u = A'*nx + B'*ny + C' = (A'/s)*x + (B'/s)*y + (C' - (A'*mx + B'*ny... )/s), expanded below.
  const [Ap, Bp, Cp] = abc;
  const [Dp, Ep, Fp] = def;
  return {
    A: Ap / s, B: Bp / s, C: Cp - (Ap * mx + Bp * my) / s,
    D: Dp / s, E: Ep / s, F: Fp - (Dp * mx + Ep * my) / s,
  };
}

/** World units -> screen pixels. Uses the calibrated fit above when one exists for the current
 * map; otherwise falls back to Riot's own formula plus the manual orientation correction. Every
 * drawing function should go through this rather than calling worldToUv directly. */
function toPixel(x, y, w, h) {
  let u, v;
  if (mapCalibration) {
    u = mapCalibration.A * x + mapCalibration.B * y + mapCalibration.C;
    v = mapCalibration.D * x + mapCalibration.E * y + mapCalibration.F;
  } else {
    const uv = worldToUv(x, y, state.map);
    const oriented = applyOrientation(uv.u, uv.v);
    u = oriented.u; v = oriented.v;
  }
  return { x: u * w, y: v * h };
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

const state = {
  match: null,
  tracks: [],
  utility: [],
  abilityCasts: [],
  visionCones: null,

  map: null,       // normalized {uuid, displayName, xMultiplier, yMultiplier, xScalarToAdd, yScalarToAdd}
  mapImage: null,

  durationMs: 0,
  currentTimeMs: 0,
  playing: false,
  speed: 1,
  showVision: false,

  playerColor: new Map(),
  playerAgentImage: new Map(),
};

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

function playerKey(player) {
  return player.Subject || ('actor_' + player.ActorNetGuid);
}

function normalizeMapInfo(m) {
  if (!m) return null;
  const xMultiplier = m.xMultiplier ?? m.XMultiplier;
  const yMultiplier = m.yMultiplier ?? m.YMultiplier;
  const xScalarToAdd = m.xScalarToAdd ?? m.XScalarToAdd;
  const yScalarToAdd = m.yScalarToAdd ?? m.YScalarToAdd;
  if (xMultiplier == null || yMultiplier == null || xScalarToAdd == null || yScalarToAdd == null) {
    return null;
  }
  return {
    uuid: m.uuid || m.Uuid || null,
    displayName: m.displayName || m.DisplayName || 'Unknown map',
    xMultiplier, yMultiplier, xScalarToAdd, yScalarToAdd,
  };
}

function worldToUv(x, y, map) {
  return { u: x * map.xMultiplier + map.xScalarToAdd, v: y * map.yMultiplier + map.yScalarToAdd };
}

function lerp(a, b, t) { return a + (b - a) * t; }

function lerpAngleDeg(a, b, t) {
  const delta = ((b - a + 540) % 360) - 180;
  return a + delta * t;
}

function colorWithAlpha(hex, alpha) {
  const n = parseInt(hex.replace('#', ''), 16);
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
}

function formatClock(ms) {
  const total = Math.max(0, Math.floor(ms / 1000));
  const m = Math.floor(total / 60);
  const s = total % 60;
  return m + ':' + String(s).padStart(2, '0');
}

function findRoundAt(rounds, timeMs) {
  for (const r of rounds) {
    if (timeMs >= r.StartTimeMs && (r.EndTimeMs == null || timeMs < r.EndTimeMs)) return r;
  }
  return null;
}

/** Index of the last entry with TimeMs <= t, via binary search. -1 if t is before every entry. */
function findTimedIndex(items, t) {
  let lo = 0, hi = items.length - 1, ans = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (items[mid].TimeMs <= t) { ans = mid; lo = mid + 1; } else { hi = mid - 1; }
  }
  return ans;
}

function interpolateSample(samples, t) {
  if (!samples || samples.length === 0) return null;
  const i = findTimedIndex(samples, t);
  if (i < 0) return samples[0];
  if (i >= samples.length - 1) return samples[samples.length - 1];
  const a = samples[i], b = samples[i + 1];
  const span = b.TimeMs - a.TimeMs;
  const frac = span > 0 ? (t - a.TimeMs) / span : 0;
  return {
    PosX: lerp(a.PosX, b.PosX, frac),
    PosY: lerp(a.PosY, b.PosY, frac),
    Yaw: lerpAngleDeg(a.Yaw, b.Yaw, frac),
  };
}

function readJsonFile(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      try { resolve(JSON.parse(reader.result)); }
      catch (err) { reject(new Error(file.name + ': ' + err.message)); }
    };
    reader.onerror = () => reject(new Error(file.name + ': could not be read'));
    reader.readAsText(file);
  });
}

// ---------------------------------------------------------------------------
// DOM references
// ---------------------------------------------------------------------------

const fileInput = document.getElementById('fileInput');
const loadStatus = document.getElementById('loadStatus');
const loadPanel = document.getElementById('loadPanel');
const viewerRoot = document.getElementById('viewerRoot');
const controls = document.getElementById('controls');
const btnLoadDifferent = document.getElementById('btnLoadDifferent');

const stage = document.getElementById('stage');
const canvas = document.getElementById('mapCanvas');
const ctx = canvas.getContext('2d');
const mapOverlay = document.getElementById('mapOverlayMessage');
const mapPicker = document.getElementById('mapPicker');
const mapSelect = document.getElementById('mapSelect');
const roster = document.getElementById('roster');

const chaptersEl = document.getElementById('chapters');
const scrubber = document.getElementById('scrubber');
const btnPlayPause = document.getElementById('btnPlayPause');
const btnRestartRound = document.getElementById('btnRestartRound');
const btnNextRound = document.getElementById('btnNextRound');
const speedSelect = document.getElementById('speedSelect');
const timeLabel = document.getElementById('timeLabel');
const roundLabel = document.getElementById('roundLabel');
const visionToggle = document.getElementById('visionToggle');
const facingOffsetInput = document.getElementById('facingOffset');
const facingOffsetValue = document.getElementById('facingOffsetValue');
const mapScaleValue = document.getElementById('mapScaleValue');
const mapOffsetXValue = document.getElementById('mapOffsetXValue');
const mapOffsetYValue = document.getElementById('mapOffsetYValue');
const mapRotateSelect = document.getElementById('mapRotate');
const mapFlipCheckbox = document.getElementById('mapFlip');
const mapScaleInput = document.getElementById('mapScale');
const mapOffsetXInput = document.getElementById('mapOffsetX');
const mapOffsetYInput = document.getElementById('mapOffsetY');
const btnResetOrientation = document.getElementById('btnResetOrientation');
const debugPanel = document.getElementById('debugPanel');
const debugText = document.getElementById('debugText');
const btnCopyDebug = document.getElementById('btnCopyDebug');
const copyDebugStatus = document.getElementById('copyDebugStatus');
const fitModeStatus = document.getElementById('fitModeStatus');

const calibratePanel = document.getElementById('calibratePanel');
const calibratePlayerSelect = document.getElementById('calibratePlayerSelect');
const btnCalibratePick = document.getElementById('btnCalibratePick');
const calibrateStatus = document.getElementById('calibrateStatus');
const calibratePointsBody = document.getElementById('calibratePointsBody');
const btnCalibrateCompute = document.getElementById('btnCalibrateCompute');
const btnCalibrateClear = document.getElementById('btnCalibrateClear');
const calibrateResult = document.getElementById('calibrateResult');

// ---------------------------------------------------------------------------
// Map calibration (DOM-dependent part -- state/math live earlier, near toPixel)
// ---------------------------------------------------------------------------

function updateFitModeStatus() {
  fitModeStatus.textContent = mapCalibration
    ? 'Using: calibrated fit (' + mapCalibration.points.length + ' point(s)) -- sliders above are ignored'
    : 'Using: manual sliders above';
}

/** Reloads calibration state for whichever map is now current -- call whenever state.map
 * changes (initial resolve, or the map dropdown override). */
function refreshCalibrationForCurrentMap() {
  const uuid = state.map && state.map.uuid;
  mapCalibration = loadMapCalibration(uuid);
  calibrationPoints = mapCalibration ? mapCalibration.points.slice() : [];
  renderCalibratePointsTable();
  updateFitModeStatus();
}

function buildCalibratePlayerList() {
  calibratePlayerSelect.innerHTML = '';
  (state.match.Players || []).forEach((p) => {
    const opt = document.createElement('option');
    opt.value = playerKey(p);
    opt.textContent = p.AgentName || playerKey(p);
    calibratePlayerSelect.appendChild(opt);
  });
}

/** Per-point error against a fit, as % of the map -- the whole point of having more than the
 * bare-minimum 3 points: with exactly 3, the fit passes through all of them exactly (residual
 * ~0) no matter how wrong a mis-clicked point is, so nothing here would ever flag it. With 4+,
 * one bad point among otherwise-good ones stands out as a visibly larger error than the rest. */
function calibrationResidualPct(fit, p) {
  const pu = fit.A * p.x + fit.B * p.y + fit.C;
  const pv = fit.D * p.x + fit.E * p.y + fit.F;
  return Math.hypot(pu - p.u, pv - p.v) * 100;
}

function renderCalibratePointsTable() {
  calibratePointsBody.innerHTML = '';
  // Only meaningful once there's a saved fit computed from (in general) more points than any one
  // point can perfectly satisfy -- see calibrationResidualPct's remarks.
  const fitForResiduals = mapCalibration;
  calibrationPoints.forEach((p, idx) => {
    const tr = document.createElement('tr');
    const cells = [
      p.player,
      formatClock(p.timeMs),
      p.x.toFixed(0),
      p.y.toFixed(0),
      (p.u * 100).toFixed(1) + '%, ' + (p.v * 100).toFixed(1) + '%',
      fitForResiduals ? calibrationResidualPct(fitForResiduals, p).toFixed(1) + '%' : '—',
    ];
    for (const text of cells) {
      const td = document.createElement('td');
      td.textContent = text;
      tr.appendChild(td);
    }
    const actionTd = document.createElement('td');
    const removeBtn = document.createElement('button');
    removeBtn.type = 'button';
    removeBtn.textContent = '✕';
    removeBtn.title = 'Remove this point';
    removeBtn.onclick = () => { calibrationPoints.splice(idx, 1); renderCalibratePointsTable(); };
    actionTd.appendChild(removeBtn);
    tr.appendChild(actionTd);
    calibratePointsBody.appendChild(tr);
  });
}

btnCalibratePick.addEventListener('click', () => {
  if (!calibratePlayerSelect.value) {
    calibrateStatus.textContent = 'Pick a player first.';
    return;
  }
  calibratePicking = true;
  btnCalibratePick.classList.add('armed');
  btnCalibratePick.textContent = 'Click the map now...';
  canvas.classList.add('calibrating');
  calibrateStatus.textContent = 'Click the exact spot on the map where that player really is right now.';
});

canvas.addEventListener('click', (e) => {
  if (!calibratePicking) return;
  calibratePicking = false;
  btnCalibratePick.classList.remove('armed');
  btnCalibratePick.textContent = 'Pick location on map';
  canvas.classList.remove('calibrating');

  const key = calibratePlayerSelect.value;
  const track = state.tracks.find((t) => playerKey(t.Player) === key);
  const sample = track && interpolateSample(track.Samples, state.currentTimeMs);
  if (!sample) {
    calibrateStatus.textContent = "Couldn't find that player's position at the current time -- try again.";
    return;
  }

  const rect = canvas.getBoundingClientRect();
  const scaleX = canvas.width / rect.width, scaleY = canvas.height / rect.height;
  const px = (e.clientX - rect.left) * scaleX;
  const py = (e.clientY - rect.top) * scaleY;

  calibrationPoints.push({
    player: calibratePlayerSelect.options[calibratePlayerSelect.selectedIndex].textContent,
    timeMs: state.currentTimeMs,
    x: sample.PosX,
    y: sample.PosY,
    u: px / canvas.width,
    v: py / canvas.height,
  });
  renderCalibratePointsTable();
  calibrateStatus.textContent = calibrationPoints.length + ' point(s) recorded so far.';
});

btnCalibrateCompute.addEventListener('click', () => {
  // 3 points exactly determine an affine transform, which means the fit passes through all 3
  // perfectly no matter what -- there's no redundancy to catch one bad/mis-clicked point, and the
  // resulting fit can be wildly wrong anywhere else on the map despite reporting "0% error". 4+
  // points make the system over-determined, so a bad point actually shows up as a worse residual
  // than the others instead of hiding perfectly.
  if (calibrationPoints.length < 4) {
    calibrateResult.textContent = 'Need at least 4 points to compute a fit that can catch a bad ' +
      'click -- with only 3, the fit passes through them exactly even if one is wrong, and you\'d ' +
      'have no way to tell. Add at least one more (6 or more is even better) and try again.';
    return;
  }
  const fit = solveAffine(calibrationPoints);
  if (!fit) {
    calibrateResult.textContent = "Those points are too close together or in a line to solve -- pick points spread across different, well-separated areas of the map.";
    return;
  }

  const residuals = calibrationPoints.map((p) => calibrationResidualPct(fit, p));
  const worstErrorPct = Math.max(...residuals);
  const avgErrorPct = residuals.reduce((a, b) => a + b, 0) / residuals.length;

  mapCalibration = Object.assign({}, fit, { points: calibrationPoints.slice() });
  saveMapCalibration(state.map && state.map.uuid, mapCalibration);
  updateFitModeStatus();
  renderCalibratePointsTable();

  let message = 'Saved -- average error ' + avgErrorPct.toFixed(1) + '% of the map, worst point ' +
    worstErrorPct.toFixed(1) + '% (see the Error column in the table above).';
  if (worstErrorPct > avgErrorPct * 2.5 && worstErrorPct > 3) {
    message += ' One point stands out as much worse than the rest -- that\'s usually a mis-click ' +
      'or wrong player/moment on that one specific point. Remove it (✕) and add a fresh one, then compute again.';
  } else if (worstErrorPct > 5) {
    message += ' That looks high across the board -- try re-picking a couple of points more precisely, or add more spread-out ones.';
  }
  calibrateResult.textContent = message;
});

btnCalibrateClear.addEventListener('click', () => {
  mapCalibration = null;
  calibrationPoints = [];
  clearMapCalibrationStorage(state.map && state.map.uuid);
  renderCalibratePointsTable();
  updateFitModeStatus();
  calibrateResult.textContent = 'Calibration cleared -- back to the manual sliders above.';
});

// ---------------------------------------------------------------------------
// Loading
// ---------------------------------------------------------------------------

fileInput.addEventListener('change', async (e) => {
  const files = Array.from(e.target.files || []);
  if (files.length === 0) return;

  const byName = new Map();
  for (const f of files) byName.set(f.name.toLowerCase(), f);

  const missing = REQUIRED_FILES.filter((n) => !byName.has(n));
  if (missing.length > 0) {
    loadStatus.textContent = 'Missing required file(s): ' + missing.join(', ') +
      ' — select every file in your analysis output folder.';
    return;
  }

  loadStatus.textContent = 'Loading...';

  async function readOptional(name, fallback) {
    return byName.has(name) ? await readJsonFile(byName.get(name)) : fallback;
  }

  try {
    state.match = await readJsonFile(byName.get('match.json'));
    state.tracks = await readJsonFile(byName.get('movement.json'));
    state.utility = await readOptional('utility.json', []);
    state.abilityCasts = await readOptional('ability_casts.json', []);
    state.visionCones = await readOptional('vision_cones.json', null);
  } catch (err) {
    loadStatus.textContent = "Couldn't read one of those files as JSON: " + err.message;
    return;
  }

  loadStatus.textContent = '';
  initializeFromLoadedData();
});

btnLoadDifferent.addEventListener('click', () => {
  state.playing = false;
  fileInput.value = '';
  loadStatus.textContent = '';
  loadPanel.hidden = false;
  viewerRoot.hidden = true;
  controls.hidden = true;
  btnLoadDifferent.hidden = true;
  mapPicker.hidden = true;
});

function initializeFromLoadedData() {
  state.durationMs = state.match.DurationMs || 0;
  state.currentTimeMs = 0;
  state.playing = false;
  btnPlayPause.textContent = '▶';

  assignPlayerColors();
  resolveMap();
  buildRoster();
  buildChapters();
  buildCalibratePlayerList();
  logSpawnDebugInfo();
  calibratePanel.hidden = false;
  calibrateStatus.textContent = '';
  calibrateResult.textContent = '';

  visionToggle.disabled = !state.visionCones;
  visionToggle.checked = false;
  state.showVision = false;

  loadPanel.hidden = true;
  viewerRoot.hidden = false;
  controls.hidden = false;
  btnLoadDifferent.hidden = false;

  requestAnimationFrame(() => { resizeCanvas(); updateTimeUi(); });
  if (!rafStarted) { rafStarted = true; requestAnimationFrame(frameLoop); }
}

function assignPlayerColors() {
  state.playerColor.clear();
  state.playerAgentImage.clear();
  const players = state.match.Players || [];
  players.forEach((p, idx) => {
    const key = playerKey(p);
    state.playerColor.set(key, PLAYER_COLORS[idx % PLAYER_COLORS.length]);
    state.playerAgentImage.set(key, agentImageFor(p));
  });
}

function agentImageFor(player) {
  const catalog = window.VRF_CATALOG;
  if (!catalog || !player.CharacterId) return null;
  const entry = (catalog.agents || []).find((a) => a.uuid === player.CharacterId);
  if (!entry) return null;
  const img = new Image();
  img.src = '../assets/' + entry.image;
  return img;
}

// ---------------------------------------------------------------------------
// Map resolution
// ---------------------------------------------------------------------------

function resolveMap() {
  const detected = normalizeMapInfo(state.match.Map);
  const catalog = window.VRF_CATALOG;
  const catalogMaps = (catalog && catalog.maps) || [];

  mapSelect.innerHTML = '';
  if (!detected) {
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '-- pick a map --';
    mapSelect.appendChild(placeholder);
  }
  catalogMaps.forEach((m) => {
    const opt = document.createElement('option');
    opt.value = m.uuid;
    opt.textContent = m.displayName;
    mapSelect.appendChild(opt);
  });

  state.map = detected;
  if (detected && catalogMaps.some((m) => m.uuid === detected.uuid)) {
    mapSelect.value = detected.uuid;
  }

  mapPicker.hidden = catalogMaps.length === 0;

  mapSelect.onchange = () => {
    if (!mapSelect.value) { state.map = null; applyLoadedOrientation(); loadMapImage(); return; }
    const chosen = catalogMaps.find((m) => m.uuid === mapSelect.value);
    state.map = chosen ? normalizeMapInfo(chosen) : null;
    applyLoadedOrientation();
    loadMapImage();
    logSpawnDebugInfo();
  };

  applyLoadedOrientation();
  loadMapImage();
}

/** Loads this map's remembered orientation correction (if any) into state + the UI controls. */
function applyLoadedOrientation() {
  const loaded = loadMapOrientation(state.map && state.map.uuid);
  mapOrientation.rotate = loaded.rotate;
  mapOrientation.flipH = loaded.flipH;
  mapOrientation.scale = loaded.scale;
  mapOrientation.offsetX = loaded.offsetX;
  mapOrientation.offsetY = loaded.offsetY;
  mapRotateSelect.value = String(loaded.rotate);
  mapFlipCheckbox.checked = loaded.flipH;
  mapScaleInput.value = String(loaded.scale);
  mapOffsetXInput.value = String(Math.round(loaded.offsetX * 100));
  mapOffsetYInput.value = String(Math.round(loaded.offsetY * 100));
  mapScaleValue.textContent = loaded.scale.toFixed(2);
  mapOffsetXValue.textContent = Math.round(loaded.offsetX * 100) + '%';
  mapOffsetYValue.textContent = Math.round(loaded.offsetY * 100) + '%';
  refreshCalibrationForCurrentMap();
}

// Sliders update mapOrientation and the on-screen readout live on every drag tick ('input', fires
// continuously) so the map redraws in real time as you adjust -- but only persist to localStorage
// and refresh the (relatively expensive) debug panel text on 'change' (fires once, on release),
// so dragging doesn't spam either of those.
function persistOrientationChange() {
  saveMapOrientation(state.map && state.map.uuid);
  if (state.tracks.length > 0) logSpawnDebugInfo();
}

mapRotateSelect.addEventListener('change', () => {
  mapOrientation.rotate = Number(mapRotateSelect.value) || 0;
  persistOrientationChange();
});
mapFlipCheckbox.addEventListener('change', () => {
  mapOrientation.flipH = mapFlipCheckbox.checked;
  persistOrientationChange();
});
mapScaleInput.addEventListener('input', () => {
  mapOrientation.scale = Number(mapScaleInput.value) || 1;
  mapScaleValue.textContent = mapOrientation.scale.toFixed(2);
});
mapScaleInput.addEventListener('change', persistOrientationChange);
mapOffsetXInput.addEventListener('input', () => {
  const pct = Number(mapOffsetXInput.value) || 0;
  mapOrientation.offsetX = pct / 100;
  mapOffsetXValue.textContent = pct + '%';
});
mapOffsetXInput.addEventListener('change', persistOrientationChange);
mapOffsetYInput.addEventListener('input', () => {
  const pct = Number(mapOffsetYInput.value) || 0;
  mapOrientation.offsetY = pct / 100;
  mapOffsetYValue.textContent = pct + '%';
});
mapOffsetYInput.addEventListener('change', persistOrientationChange);
btnResetOrientation.addEventListener('click', () => {
  const fresh = defaultOrientation();
  Object.assign(mapOrientation, fresh);
  mapRotateSelect.value = '0';
  mapFlipCheckbox.checked = false;
  mapScaleInput.value = '1';
  mapOffsetXInput.value = '0';
  mapOffsetYInput.value = '0';
  mapScaleValue.textContent = '1.00';
  mapOffsetXValue.textContent = '0%';
  mapOffsetYValue.textContent = '0%';
  persistOrientationChange();
});

function loadMapImage() {
  const catalog = window.VRF_CATALOG;

  if (!catalog) {
    showMapOverlay("No local art found. Run scripts/Fetch-Assets.ps1 once (see the project README), then reload this page. Positions will still be plotted below without a background image.");
    state.mapImage = null;
    return;
  }

  if (!state.map || !state.map.uuid) {
    showMapOverlay("Couldn't tell which map this replay is on — pick one above.");
    state.mapImage = null;
    return;
  }

  const entry = (catalog.maps || []).find((m) => m.uuid === state.map.uuid);
  if (!entry) {
    showMapOverlay('No local image for this map yet — re-run Fetch-Assets.ps1.');
    state.mapImage = null;
    return;
  }

  const img = new Image();
  img.onload = () => { state.mapImage = img; hideMapOverlay(); };
  img.onerror = () => { state.mapImage = null; showMapOverlay('That map image failed to load.'); };
  img.src = '../assets/' + entry.image;
}

function showMapOverlay(text) { mapOverlay.textContent = text; mapOverlay.hidden = false; }
function hideMapOverlay() { mapOverlay.hidden = true; }

/**
 * Diagnostic aid, shown on the page itself (a collapsible "Debug info" panel below the roster) --
 * not just the browser console, so it's copy-pasteable without opening developer tools. If
 * positions look wrong on the minimap (wrong side, clipped into walls, etc.), this is the first
 * thing to check: for every player's very first recorded sample, it lists the raw world
 * PosX/PosY, the normalized u/v this project's map transform computes from them, and whether that
 * u/v actually lands inside the map image (0..1 on both axes). u/v outside [0,1] at round start is
 * a strong signal of a real transform bug (wrong map detected, wrong multiplier, swapped axes);
 * u/v just barely inside [0,1] near an edge can be entirely correct data that simply looks clipped
 * on screen because player icons are drawn at a fixed pixel radius (AGENT_ICON_RADIUS_PX)
 * regardless of how close the real spawn point is to a wall.
 */
function logSpawnDebugInfo() {
  if (!state.map) {
    debugText.value = 'No map resolved yet -- pick one from the dropdown above, then reopen this panel.';
    debugPanel.hidden = false;
    return;
  }

  const rows = state.tracks.map((track) => {
    const sample = track.Samples && track.Samples[0];
    if (!sample) return null;
    const uv = worldToUv(sample.PosX, sample.PosY, state.map);
    return {
      player: (track.Player && (track.Player.AgentName || playerKey(track.Player))) || '(unknown)',
      PosX: Number(sample.PosX.toFixed(1)),
      PosY: Number(sample.PosY.toFixed(1)),
      u: Number(uv.u.toFixed(4)),
      v: Number(uv.v.toFixed(4)),
      insideImage: uv.u >= 0 && uv.u <= 1 && uv.v >= 0 && uv.v <= 1,
    };
  }).filter(Boolean);

  const lines = [];
  lines.push('Map: ' + state.map.displayName);
  lines.push('xMultiplier=' + state.map.xMultiplier + '  yMultiplier=' + state.map.yMultiplier +
    '  xScalarToAdd=' + state.map.xScalarToAdd + '  yScalarToAdd=' + state.map.yScalarToAdd);
  lines.push('Map orientation control: rotate=' + mapOrientation.rotate + '  flipH=' + mapOrientation.flipH +
    '  scale=' + mapOrientation.scale + '  offsetX=' + mapOrientation.offsetX + '  offsetY=' + mapOrientation.offsetY);
  lines.push(mapCalibration
    ? 'Calibrated fit ACTIVE (' + mapCalibration.points.length + ' point(s)) -- the orientation control above is being ignored.'
    : 'No calibrated fit saved for this map -- using the orientation control above.');
  lines.push('');
  lines.push('Spawn-frame positions (u/v should be within 0..1 to land on the map image):');
  lines.push(['player', 'PosX', 'PosY', 'u', 'v', 'insideImage'].join('\t'));
  for (const r of rows) {
    lines.push([r.player, r.PosX, r.PosY, r.u, r.v, r.insideImage].join('\t'));
  }

  debugText.value = lines.join('\n');
  debugPanel.hidden = false;
}

btnCopyDebug.addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText(debugText.value);
    copyDebugStatus.textContent = 'Copied!';
  } catch {
    debugText.select();
    try {
      document.execCommand('copy');
      copyDebugStatus.textContent = 'Copied!';
    } catch {
      copyDebugStatus.textContent = 'Could not auto-copy -- text is selected, press Ctrl+C.';
    }
  }
  setTimeout(() => { copyDebugStatus.textContent = ''; }, 3000);
});

// ---------------------------------------------------------------------------
// Roster / chapters
// ---------------------------------------------------------------------------

function buildRoster() {
  roster.innerHTML = '';
  (state.match.Players || []).forEach((p) => {
    const key = playerKey(p);
    const row = document.createElement('div');
    row.className = 'roster-row';
    const swatch = document.createElement('span');
    swatch.className = 'swatch';
    swatch.style.background = state.playerColor.get(key);
    const label = document.createElement('span');
    label.textContent = p.AgentName || '(unknown agent)';
    row.appendChild(swatch);
    row.appendChild(label);
    roster.appendChild(row);
  });
}

function buildChapters() {
  chaptersEl.innerHTML = '';
  const rounds = state.match.Rounds || [];
  const duration = state.durationMs || 1;

  rounds.forEach((r) => {
    const end = r.EndTimeMs == null ? duration : r.EndTimeMs;
    const widthPct = Math.max(0.5, ((end - r.StartTimeMs) / duration) * 100);
    const btn = document.createElement('button');
    btn.className = 'chapter';
    btn.style.flex = '0 0 ' + widthPct + '%';
    btn.textContent = 'R' + r.RoundNumber;
    btn.dataset.start = String(r.StartTimeMs);
    btn.title = 'Round ' + r.RoundNumber;
    btn.onclick = () => { state.currentTimeMs = r.StartTimeMs; updateTimeUi(); };
    chaptersEl.appendChild(btn);
  });
}

// ---------------------------------------------------------------------------
// Transport controls
// ---------------------------------------------------------------------------

scrubber.addEventListener('input', () => {
  state.currentTimeMs = Number(scrubber.value);
  updateTimeUi();
});

btnPlayPause.addEventListener('click', () => {
  state.playing = !state.playing;
  btnPlayPause.textContent = state.playing ? '⏸' : '▶';
});

btnRestartRound.addEventListener('click', () => {
  const rounds = state.match.Rounds || [];
  const current = findRoundAt(rounds, state.currentTimeMs);
  if (current && state.currentTimeMs - current.StartTimeMs > 3000) {
    state.currentTimeMs = current.StartTimeMs;
  } else {
    const idx = rounds.indexOf(current);
    state.currentTimeMs = idx > 0 ? rounds[idx - 1].StartTimeMs : 0;
  }
  updateTimeUi();
});

btnNextRound.addEventListener('click', () => {
  const rounds = state.match.Rounds || [];
  const current = findRoundAt(rounds, state.currentTimeMs);
  const idx = rounds.indexOf(current);
  state.currentTimeMs = (idx >= 0 && idx + 1 < rounds.length) ? rounds[idx + 1].StartTimeMs : state.durationMs;
  updateTimeUi();
});

speedSelect.addEventListener('change', () => { state.speed = Number(speedSelect.value); });
visionToggle.addEventListener('change', () => { state.showVision = visionToggle.checked; });
facingOffsetInput.addEventListener('input', () => {
  facingOffsetDeg = Number(facingOffsetInput.value) || 0;
  facingOffsetValue.textContent = facingOffsetDeg + '°';
});

function updateTimeUi() {
  scrubber.max = String(state.durationMs);
  scrubber.value = String(state.currentTimeMs);
  timeLabel.textContent = formatClock(state.currentTimeMs) + ' / ' + formatClock(state.durationMs);

  const round = findRoundAt(state.match.Rounds || [], state.currentTimeMs);
  roundLabel.textContent = round ? ('Round ' + round.RoundNumber) : '';

  chaptersEl.querySelectorAll('.chapter').forEach((btn) => {
    btn.classList.toggle('active', !!round && btn.dataset.start === String(round.StartTimeMs));
  });
}

// ---------------------------------------------------------------------------
// Playback loop
// ---------------------------------------------------------------------------

let rafStarted = false;
let lastFrameTime = null;

function frameLoop(now) {
  if (lastFrameTime == null) lastFrameTime = now;
  const dt = now - lastFrameTime;
  lastFrameTime = now;

  if (state.playing) {
    state.currentTimeMs += dt * state.speed;
    if (state.currentTimeMs >= state.durationMs) {
      state.currentTimeMs = state.durationMs;
      state.playing = false;
      btnPlayPause.textContent = '▶';
    }
    updateTimeUi();
  }

  render();
  requestAnimationFrame(frameLoop);
}

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

function resizeCanvas() {
  const w = stage.clientWidth, h = stage.clientHeight;
  if (w > 0 && h > 0 && (canvas.width !== w || canvas.height !== h)) {
    canvas.width = w;
    canvas.height = h;
  }
}
window.addEventListener('resize', () => { if (!viewerRoot.hidden) resizeCanvas(); });

function render() {
  if (viewerRoot.hidden) return;
  if (canvas.width === 0 || canvas.width !== stage.clientWidth) resizeCanvas();
  const w = canvas.width, h = canvas.height;
  if (w === 0 || h === 0) return;

  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = '#000';
  ctx.fillRect(0, 0, w, h);

  if (state.mapImage) ctx.drawImage(state.mapImage, 0, 0, w, h);
  if (!state.map) return;

  drawUtility(w, h);
  drawAbilityCasts(w, h);
  if (state.showVision && state.visionCones) drawVisionCones(w, h);
  drawPlayers(w, h);
}

function drawUtility(w, h) {
  const t = state.currentTimeMs;
  for (const u of state.utility) {
    if (u.X == null || u.Y == null) continue;
    if (t < u.SpawnTimeMs) continue;
    if (u.DespawnTimeMs != null && t >= u.DespawnTimeMs) continue;

    const origin = toPixel(u.X, u.Y, w, h);
    const px = origin.x, py = origin.y;
    const color = UTILITY_COLORS[u.Category] || 'rgba(255,255,255,0.6)';

    if (u.Category === 'Wall') {
      const yaw = (u.YawDegrees || 0) * Math.PI / 180;
      const p1 = toPixel(u.X - Math.cos(yaw) * WALL_HALF_LENGTH_UNITS, u.Y - Math.sin(yaw) * WALL_HALF_LENGTH_UNITS, w, h);
      const p2 = toPixel(u.X + Math.cos(yaw) * WALL_HALF_LENGTH_UNITS, u.Y + Math.sin(yaw) * WALL_HALF_LENGTH_UNITS, w, h);
      ctx.strokeStyle = color;
      ctx.lineWidth = 4;
      ctx.beginPath();
      ctx.moveTo(p1.x, p1.y);
      ctx.lineTo(p2.x, p2.y);
      ctx.stroke();
    } else if (u.Category === 'Smoke' || u.Category === 'IncendiaryOrMolly') {
      const edge = toPixel(u.X + UTILITY_AREA_RADIUS_UNITS, u.Y, w, h);
      const radiusPx = Math.max(Math.hypot(edge.x - origin.x, edge.y - origin.y), 4);
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.arc(px, py, radiusPx, 0, Math.PI * 2);
      ctx.fill();
    } else {
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.arc(px, py, 5, 0, Math.PI * 2);
      ctx.fill();
    }
  }
}

function drawAbilityCasts(w, h) {
  const t = state.currentTimeMs;
  for (const cast of state.abilityCasts) {
    if (cast.CastX == null || cast.CastY == null) continue;
    const age = t - cast.FirstObservedAtMs;
    if (age < 0 || age > ABILITY_MARKER_FADE_MS) continue;

    const alpha = 1 - age / ABILITY_MARKER_FADE_MS;
    const p = toPixel(cast.CastX, cast.CastY, w, h);
    ctx.fillStyle = `rgba(255,255,255,${(alpha * 0.9).toFixed(2)})`;
    ctx.beginPath();
    ctx.arc(p.x, p.y, 10 * alpha + 3, 0, Math.PI * 2);
    ctx.fill();
  }
}

function drawVisionCones(w, h) {
  for (const key of Object.keys(state.visionCones)) {
    const cones = state.visionCones[key];
    if (!cones || cones.length === 0) continue;

    const idx = findTimedIndex(cones, state.currentTimeMs);
    const cone = idx >= 0 ? cones[idx] : cones[0];
    if (!cone) continue;

    const color = state.playerColor.get(key) || '#ffffff';
    const origin = toPixel(cone.OriginX, cone.OriginY, w, h);

    ctx.fillStyle = colorWithAlpha(color, 0.1);
    ctx.beginPath();
    ctx.moveTo(origin.x, origin.y);

    const segments = 12;
    const startDeg = cone.ForwardYawDeg - cone.HalfAngleDeg;
    const stepDeg = (2 * cone.HalfAngleDeg) / segments;
    for (let i = 0; i <= segments; i++) {
      const angle = (startDeg + stepDeg * i) * Math.PI / 180;
      const p = toPixel(cone.OriginX + Math.cos(angle) * cone.RangeCm, cone.OriginY + Math.sin(angle) * cone.RangeCm, w, h);
      ctx.lineTo(p.x, p.y);
    }
    ctx.closePath();
    ctx.fill();
  }
}

function drawPlayers(w, h) {
  for (const track of state.tracks) {
    const player = track.Player;
    const key = playerKey(player);
    const sample = interpolateSample(track.Samples, state.currentTimeMs);
    if (!sample) continue;

    const origin = toPixel(sample.PosX, sample.PosY, w, h);
    const px = origin.x, py = origin.y;
    const color = state.playerColor.get(key) || '#ffffff';

    // Facing arrow: project a point ahead of the player in game-world space (using VALORANT's
    // own yaw) through the same map transform as everything else, rather than rotating on
    // screen directly — that way it's automatically correct regardless of how a given map's
    // transform flips or scales axes.
    const yawRad = (sample.Yaw + facingOffsetDeg) * Math.PI / 180;
    const ahead = toPixel(sample.PosX + Math.cos(yawRad) * FACING_LOOKAHEAD_UNITS, sample.PosY + Math.sin(yawRad) * FACING_LOOKAHEAD_UNITS, w, h);
    ctx.strokeStyle = color;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(px, py);
    ctx.lineTo(ahead.x, ahead.y);
    ctx.stroke();

    const img = state.playerAgentImage.get(key);
    ctx.save();
    ctx.beginPath();
    ctx.arc(px, py, AGENT_ICON_RADIUS_PX, 0, Math.PI * 2);
    ctx.closePath();
    ctx.clip();
    if (img && img.complete && img.naturalWidth > 0) {
      ctx.drawImage(img, px - AGENT_ICON_RADIUS_PX, py - AGENT_ICON_RADIUS_PX, AGENT_ICON_RADIUS_PX * 2, AGENT_ICON_RADIUS_PX * 2);
    } else {
      ctx.fillStyle = color;
      ctx.fill();
    }
    ctx.restore();

    ctx.strokeStyle = color;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(px, py, AGENT_ICON_RADIUS_PX, 0, Math.PI * 2);
    ctx.stroke();
  }
}
