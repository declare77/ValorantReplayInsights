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
const mapOrientation = { rotate: 0, flipH: false };

function orientationStorageKey(mapUuid) { return 'vrf-map-orientation:' + mapUuid; }

function loadMapOrientation(mapUuid) {
  try {
    const raw = mapUuid ? localStorage.getItem(orientationStorageKey(mapUuid)) : null;
    if (!raw) return { rotate: 0, flipH: false };
    const parsed = JSON.parse(raw);
    return { rotate: Number(parsed.rotate) || 0, flipH: !!parsed.flipH };
  } catch { return { rotate: 0, flipH: false }; }
}

function saveMapOrientation(mapUuid) {
  if (!mapUuid) return;
  try { localStorage.setItem(orientationStorageKey(mapUuid), JSON.stringify(mapOrientation)); }
  catch { /* private-mode / storage disabled -- orientation just won't be remembered next time */ }
}

/** Rotates/flips a normalized (u,v) point in [0,1] around the image center. Screen-space only --
 * independent of worldToUv's world-unit math, so it corrects mismatched minimap art without
 * needing to touch (or understand) Riot's own coordinate formula. */
function applyOrientation(u, v) {
  let x = u - 0.5, y = v - 0.5;
  if (mapOrientation.flipH) x = -x;
  switch (((mapOrientation.rotate % 360) + 360) % 360) {
    case 90: { const nx = -y, ny = x; x = nx; y = ny; break; }
    case 180: { x = -x; y = -y; break; }
    case 270: { const nx = y, ny = -x; x = nx; y = ny; break; }
    default: break;
  }
  return { u: x + 0.5, v: y + 0.5 };
}

/** World units -> screen pixels, applying both the map's own transform and the (usually
 * identity) orientation correction above. Every drawing function should go through this rather
 * than calling worldToUv directly, so the orientation control affects everything consistently. */
function toPixel(x, y, w, h) {
  const uv = worldToUv(x, y, state.map);
  const oriented = applyOrientation(uv.u, uv.v);
  return { x: oriented.u * w, y: oriented.v * h };
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
const mapRotateSelect = document.getElementById('mapRotate');
const mapFlipCheckbox = document.getElementById('mapFlip');

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
  logSpawnDebugInfo();

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
  };

  applyLoadedOrientation();
  loadMapImage();
}

/** Loads this map's remembered orientation correction (if any) into state + the UI controls. */
function applyLoadedOrientation() {
  const loaded = loadMapOrientation(state.map && state.map.uuid);
  mapOrientation.rotate = loaded.rotate;
  mapOrientation.flipH = loaded.flipH;
  mapRotateSelect.value = String(loaded.rotate);
  mapFlipCheckbox.checked = loaded.flipH;
}

mapRotateSelect.addEventListener('change', () => {
  mapOrientation.rotate = Number(mapRotateSelect.value) || 0;
  saveMapOrientation(state.map && state.map.uuid);
});
mapFlipCheckbox.addEventListener('change', () => {
  mapOrientation.flipH = mapFlipCheckbox.checked;
  saveMapOrientation(state.map && state.map.uuid);
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
 * Diagnostic aid, printed once per load to the browser console (F12 -> Console tab) — not shown
 * on screen. If positions look wrong on the minimap (wrong side, clipped into walls, etc.), this
 * is the first thing to check: for every player's very first recorded sample, it prints the raw
 * world PosX/PosY, the normalized u/v this project's map transform computes from them, and
 * whether that u/v actually lands inside the map image (0..1 on both axes). u/v outside [0,1] at
 * round start is a strong signal of a real transform bug (wrong map detected, wrong multiplier,
 * swapped axes); u/v just barely inside [0,1] near an edge can be entirely correct data that
 * simply looks clipped on screen because player icons are drawn at a fixed pixel radius
 * (AGENT_ICON_RADIUS_PX) regardless of how close the real spawn point is to a wall.
 */
function logSpawnDebugInfo() {
  if (!state.map) {
    console.log('[vrf-viewer] No map resolved yet -- spawn debug info will print once you pick a map above.');
    return;
  }

  const rows = state.tracks.map((track) => {
    const sample = track.Samples && track.Samples[0];
    if (!sample) return null;
    const uv = worldToUv(sample.PosX, sample.PosY, state.map);
    return {
      player: (track.Player && (track.Player.AgentName || playerKey(track.Player))) || '(unknown)',
      PosX: sample.PosX,
      PosY: sample.PosY,
      u: Number(uv.u.toFixed(4)),
      v: Number(uv.v.toFixed(4)),
      insideImage: uv.u >= 0 && uv.u <= 1 && uv.v >= 0 && uv.v <= 1,
    };
  }).filter(Boolean);

  console.log(
    '[vrf-viewer] Map:', state.map.displayName,
    ' xMultiplier/yMultiplier/xScalarToAdd/yScalarToAdd:',
    state.map.xMultiplier, state.map.yMultiplier, state.map.xScalarToAdd, state.map.yScalarToAdd
  );
  console.log('[vrf-viewer] Spawn-frame positions (u/v should be within 0..1 -- see comment above logSpawnDebugInfo in app.js):');
  console.table(rows);
}

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
facingOffsetInput.addEventListener('change', () => { facingOffsetDeg = Number(facingOffsetInput.value) || 0; });

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
