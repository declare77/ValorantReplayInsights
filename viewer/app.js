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

// Fallback only -- used per-player when a round's attack/defense side isn't known (see
// TEAM_COLOR_ATTACK/TEAM_COLOR_DEFEND below, which take priority whenever match.json's `Sides`
// covers the current round).
const PLAYER_COLORS = [
  '#4d9fff', '#ff5c5c', '#4ade80', '#facc15', '#c084fc',
  '#22d3ee', '#fb923c', '#f472b6', '#a3e635', '#94a3b8',
];

// Attacker/defender colors -- used whenever the analysis pipeline could work out which side a
// player was on for the current round (see the C# project's TeamSideResolver). Falls back to
// PLAYER_COLORS above for a match/round where that couldn't be determined.
const TEAM_COLOR_ATTACK = '#ff4d4d';
const TEAM_COLOR_DEFEND = '#4ade80';

// Freeze time (buy phase) length, in ms -- user-supplied domain knowledge, not derived from any
// replay data (vrfkit doesn't carry a buy-phase-length field; RoundInfo.StartTimeMs is the
// `roundStarted` event, which per the C# project's own AbilityCastBuilder remarks fires at the
// START of freeze time, not when players are actually free to move -- see freezeTimeMsForRound()
// and playableStartMs() below, which correct for this). Only the first round of each regulation
// half is known to differ from the standard 30s -- overtime rounds (25+) are NOT covered here
// since their freeze time wasn't specified; they'll be (possibly incorrectly) treated as a normal
// 30s round until confirmed otherwise.
//
// RoundInfo.RoundNumber (from the roundStarted event's raw Word0) is 0-indexed -- confirmed by a
// real replay showing "Round 0" as its first round in this viewer, which is not how VALORANT
// itself ever numbers a round (it always shows "ROUND 1" first). So "the first round of the
// match" is raw round 0, and "the first round of the second half" is raw round 12 -- NOT 1 and 13,
// which is what those mean only once shifted for display (see roundNumberForDisplay() below,
// which is the ONLY place that shift should ever happen -- every internal join/lookup here keys
// off the raw, unshifted RoundNumber, same as the C# side).
const FREEZE_TIME_MS_FIRST_OF_HALF = 45000;
const FREEZE_TIME_MS_DEFAULT = 30000;

function freezeTimeMsForRound(rawRoundNumber) {
  return (rawRoundNumber === 0 || rawRoundNumber === 12) ? FREEZE_TIME_MS_FIRST_OF_HALF : FREEZE_TIME_MS_DEFAULT;
}

/** The moment freeze time actually ends and players can move/shoot/plant -- as opposed to
 * RoundInfo.StartTimeMs, which is when freeze time *begins*. Use this, not r.StartTimeMs
 * directly, anywhere "round start" is meant in the gameplay sense. */
function playableStartMs(round) {
  return round.StartTimeMs + freezeTimeMsForRound(round.RoundNumber);
}

/** RoundInfo.RoundNumber is 0-indexed internally (see the comment above) -- this is the one
 * place that turns it into the 1-indexed number VALORANT itself shows players ("Round 1", not
 * "Round 0"). Every UI string that shows a round number to the person should go through this,
 * rather than using round.RoundNumber directly. */
function roundNumberForDisplay(rawRoundNumber) {
  return rawRoundNumber + 1;
}

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
const UTILITY_ICON_SIZE_PX = 28;

// Words too generic/common across almost every ability's UI text (a HUD action verb like "FIRE",
// or a filler word) to count as a real content match -- see resolveUtilityAbilityMatch's doc
// comment. Extend freely, same editable-list philosophy as UtilityCategory's own keyword table
// on the C# side.
const ABILITY_MATCH_STOPWORDS = new Set([
  'equip', 'equipped', 'equipment', 'fire', 'alt', 'activate', 'deactivate', 'instantly', 'hold',
  'release', 'reactivate', 'interact', 'ability', 'abilities', 'effect', 'effects', 'field',
  'fields', 'this', 'that', 'their', 'them', 'they', 'your', 'you', 'anyone', 'anything',
  'someone', 'enemy', 'enemies', 'player', 'players', 'ally', 'allies', 'damage', 'damages',
  'dealing', 'deals', 'deal', 'instead', 'area', 'areas', 'radius', 'short', 'long', 'line',
  'lines', 'forward', 'through', 'world', 'direction', 'amount', 'times', 'time', 'duration',
  'charge', 'charges', 'cooldown', 'aim', 'sights', 'ground', 'create', 'creates', 'creating',
  'while', 'after', 'before', 'first', 'each', 'every', 'near', 'around', 'within', 'without',
  'into', 'onto', 'gain', 'gains', 'brief', 'briefly', 'quickly', 'slowly', 'multiple',
  'remaining', 'lasts', 'last', 'sets',
]);

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

// Known-good starting orientations, worked out once by comparing a replay's positions against a
// real in-game screenshot with known player locations, so nobody has to rediscover them per map.
// This is OUR OWN empirical finding, not part of Riot's published data -- it doesn't come from
// (and isn't overwritten by) Fetch-Assets.ps1. If a map isn't listed here, the manual sliders
// below still work exactly as before; add an entry once a map's correct rotate/flip is confirmed
// (see README's map-orientation section for how to verify one).
const KNOWN_MAP_ORIENTATIONS = {
  // Sunset -- confirmed twice: matches a player's own room-by-room read of a live match, and
  // separately matches 8 of 10 real player positions from a screenshot to within a few % of the
  // map (see README). The remaining two were players sprinting at that exact instant, not a
  // problem with the rotation itself.
  '92584fbe-486a-b1b2-9faa-39b0f486b498': { rotate: 90, flipH: true },
};

function defaultOrientation(mapUuid) {
  const known = mapUuid && KNOWN_MAP_ORIENTATIONS[mapUuid];
  return { rotate: known ? known.rotate : 0, flipH: known ? known.flipH : false, scale: 1, offsetX: 0, offsetY: 0 };
}

function loadMapOrientation(mapUuid) {
  const fallback = defaultOrientation(mapUuid);
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

/** World units -> screen pixels: Riot's own formula (worldToUv) plus the manual orientation
 * correction above (rotate/flip/scale/pan, seeded from KNOWN_MAP_ORIENTATIONS when available).
 * Every drawing function should go through this rather than calling worldToUv directly. */
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
  events: [],

  map: null,       // normalized {uuid, displayName, xMultiplier, yMultiplier, xScalarToAdd, yScalarToAdd}
  mapImage: null,

  durationMs: 0,
  currentTimeMs: 0,
  playing: false,
  speed: 1,
  showVision: false,

  playerColor: new Map(),
  playerAgentImage: new Map(),
  playerByKey: new Map(),
  // "<agent uuid>/<ability image path>" -> Image, built lazily the first time a marker resolves
  // to that ability. See resolveUtilityIconImage().
  utilityIconCache: new Map(),
  // playerKey -> Map(roundNumber -> { TimeMs, X, Y }), built from characterDeath events. See
  // buildDeathMarkers().
  deaths: new Map(),
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
const legendAttack = document.getElementById('legendAttack');
const legendDefend = document.getElementById('legendDefend');

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
    // Optional (older output folders may predate this file) -- only used to find each player's
    // death time/location per round so drawPlayers() can show a death marker instead of a live icon.
    state.events = await readOptional('events.json', []);
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
  buildDeathMarkers();
  const hasSides = (state.match.Sides || []).length > 0;
  legendAttack.hidden = !hasSides;
  legendDefend.hidden = !hasSides;
  resolveMap();
  buildRoster();
  lastRosterRound = findRoundAt(state.match.Rounds || [], state.currentTimeMs)?.RoundNumber ?? null;
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
  state.playerByKey.clear();
  const players = state.match.Players || [];
  players.forEach((p, idx) => {
    const key = playerKey(p);
    state.playerColor.set(key, PLAYER_COLORS[idx % PLAYER_COLORS.length]);
    state.playerAgentImage.set(key, agentImageFor(p));
    state.playerByKey.set(key, p);
  });
}

/** Attack/defense color for a player in a given round, from match.json's `Sides` (see the C#
 * project's TeamSideResolver) -- null if that round's side isn't known, so callers fall back to
 * the individual per-player color. */
function teamColorForRound(actorNetGuid, roundNumber) {
  if (roundNumber == null) return null;
  const sides = (state.match.Sides || []).find((s) => s.RoundNumber === roundNumber);
  if (!sides) return null;
  if (sides.AttackingActorNetGuids && sides.AttackingActorNetGuids.includes(actorNetGuid)) return TEAM_COLOR_ATTACK;
  if (sides.DefendingActorNetGuids && sides.DefendingActorNetGuids.includes(actorNetGuid)) return TEAM_COLOR_DEFEND;
  return null;
}

function colorForPlayer(player, roundNumber) {
  return teamColorForRound(player.ActorNetGuid, roundNumber) || state.playerColor.get(playerKey(player)) || '#ffffff';
}

/** Builds state.deaths from events.json's `characterDeath` rows: Word1 is the killed character
 * pawn's NetGUID (vrfkit docs: "the character-death words reference character pawns"), which is
 * exactly PlayerIdentity.CharacterNetGuid -- the same join key movement/tracks already use, so no
 * extra PlayerState-vs-pawn resolution is needed. Each round gets at most one death per player
 * (they respawn next round), stored with their exact position at the moment they died. */
function buildDeathMarkers() {
  state.deaths = new Map();

  const charGuidToKey = new Map();
  const charGuidToSamples = new Map();
  for (const track of state.tracks) {
    const guid = track.Player && track.Player.CharacterNetGuid;
    if (guid != null) {
      charGuidToKey.set(guid, playerKey(track.Player));
      charGuidToSamples.set(guid, track.Samples);
    }
  }

  for (const ev of state.events) {
    if (ev.Group !== 'characterDeath' || ev.Word1 == null || ev.RoundNumber == null) continue;
    const key = charGuidToKey.get(ev.Word1);
    if (!key) continue;
    const sample = interpolateSample(charGuidToSamples.get(ev.Word1), ev.TimeMs);
    if (!sample) continue;

    if (!state.deaths.has(key)) state.deaths.set(key, new Map());
    const roundDeaths = state.deaths.get(key);
    const existing = roundDeaths.get(ev.RoundNumber);
    if (!existing || ev.TimeMs < existing.TimeMs) {
      roundDeaths.set(ev.RoundNumber, { TimeMs: ev.TimeMs, X: sample.PosX, Y: sample.PosY });
    }
  }
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
// Real ability icons for utility markers (matches a marker to its agent's actual VALORANT
// ability, via valorant-api.com data -- see scripts/Fetch-Assets.ps1 -- instead of always
// drawing the plain colored shapes in UTILITY_COLORS).
// ---------------------------------------------------------------------------

function resolveUtilityAgentCatalogEntry(agentRealName) {
  const catalog = window.VRF_CATALOG;
  if (!catalog || !agentRealName) return null;
  return (catalog.agents || []).find((a) => a.displayName === agentRealName) || null;
}

function significantWords(text) {
  if (!text) return [];
  return (text.toLowerCase().match(/[a-z]+/g) || [])
    .filter((w) => w.length >= 4 && !ABILITY_MATCH_STOPWORDS.has(w));
}

// Real ability descriptions never phrase a class name's internal fragment ("SeekerNade",
// "ExplodeyPatch") the same way English prose does ("seeking", "explodes") -- so rather than
// requiring an exact word match, two words "share a root" if they're identical, share the same
// first 4 characters (catches seek/seeking, explod/explodes/explosion), or one is a substring of
// the other (catches flamewallmanager/flame, smokezone/smoke).
function wordsShareRoot(a, b) {
  if (a === b) return true;
  if (a.length < 4 || b.length < 4) return false;
  if (a.slice(0, 4) === b.slice(0, 4)) return true;
  return a.includes(b) || b.includes(a);
}

function scoreAbilityMatch(descriptiveKeyword, ability) {
  const queryWords = significantWords(descriptiveKeyword);
  const abilityWords = significantWords((ability.displayName || '') + ' ' + (ability.description || ''));
  if (queryWords.length === 0 || abilityWords.length === 0) return 0;

  let score = 0;
  const used = new Set();
  for (const qw of queryWords) {
    for (const aw of abilityWords) {
      if (used.has(aw)) continue;
      if (wordsShareRoot(qw, aw)) {
        used.add(aw);
        score++;
        break;
      }
    }
  }
  return score;
}

/**
 * Best-effort match of a utility marker to one of its agent's real abilities, so it can show the
 * actual VALORANT ability icon instead of a plain colored shape.
 *
 * This project's own internal class-path slot tokens (the "4"/"Q"/"E"/"X"/"C" in e.g.
 * `Ability_Wraith_4_Smoke`) do NOT reliably correspond to valorant-api's own ability1/ability2/
 * grenade/ultimate slots -- confirmed: Omen's smoke is class-path slot "4" but valorant-api slot
 * "grenade" (it's his signature ability, Dark Cover). So this matches on TEXT instead:
 * `u.DescriptiveKeyword` (the human-readable fragment of the class name, from the C# side's
 * `UtilityEffectClassifier.ExtractDescriptiveKeyword` -- e.g. "Smoke", "SeekerNade") against each
 * of `u.AgentRealName`'s real abilities' displayName + description (from valorant-api.com, via
 * VRF_CATALOG/scripts/Fetch-Assets.ps1), scored by stopword-filtered shared-word-root count.
 *
 * A marker only gets an icon when exactly one ability scores strictly higher than every other
 * candidate for that agent. A tie -- e.g. Gekko's `Ability_Aggrobot_X_RollyExplosion` and
 * `Ability_Aggrobot_C_ExplodeyPatch` both share a root with "explod-" words in more than one of
 * his abilities' descriptions -- means genuine ambiguity from this text alone, so the marker
 * keeps its plain colored shape rather than risk showing the wrong ability's icon. Verified
 * against every agent this project has real ability text for (KAY/O, Chamber, Gekko, Phoenix);
 * every other agent runs the same generic algorithm, unverified until you check it against your
 * own export.
 */
function resolveUtilityAbilityMatch(u) {
  const agent = resolveUtilityAgentCatalogEntry(u.AgentRealName);
  if (!agent || !u.DescriptiveKeyword || !agent.abilities || agent.abilities.length === 0) {
    return null;
  }

  let best = null;
  let bestScore = 0;
  let secondScore = 0;
  for (const ability of agent.abilities) {
    const score = scoreAbilityMatch(u.DescriptiveKeyword, ability);
    if (score > bestScore) {
      secondScore = bestScore;
      bestScore = score;
      best = ability;
    } else if (score > secondScore) {
      secondScore = score;
    }
  }

  return (best && bestScore >= 1 && bestScore > secondScore) ? { agent, ability: best, score: bestScore } : null;
}

/** The (possibly still-loading) Image for a marker's matched ability icon, or null if none
 * confidently matched (see resolveUtilityAbilityMatch) -- cached per agent+ability so repeated
 * calls across frames don't recreate/re-request the same image. */
function resolveUtilityIconImage(u) {
  const match = resolveUtilityAbilityMatch(u);
  if (!match || !match.ability.image) return null;

  const key = match.agent.uuid + '/' + match.ability.image;
  if (!state.utilityIconCache.has(key)) {
    const img = new Image();
    img.src = '../assets/' + match.ability.image;
    state.utilityIconCache.set(key, img);
  }
  return state.utilityIconCache.get(key);
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
  // Rounded to 0.1% (matching the sliders' step="0.1") rather than a whole percent -- panning
  // used to jump by whole percentage points per tick, which was much too coarse.
  mapOffsetXInput.value = String(Math.round(loaded.offsetX * 1000) / 10);
  mapOffsetYInput.value = String(Math.round(loaded.offsetY * 1000) / 10);
  mapScaleValue.textContent = loaded.scale.toFixed(2);
  mapOffsetXValue.textContent = (Math.round(loaded.offsetX * 1000) / 10).toFixed(1) + '%';
  mapOffsetYValue.textContent = (Math.round(loaded.offsetY * 1000) / 10).toFixed(1) + '%';
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
  mapOffsetXValue.textContent = pct.toFixed(1) + '%';
});
mapOffsetXInput.addEventListener('change', persistOrientationChange);
mapOffsetYInput.addEventListener('input', () => {
  const pct = Number(mapOffsetYInput.value) || 0;
  mapOrientation.offsetY = pct / 100;
  mapOffsetYValue.textContent = pct.toFixed(1) + '%';
});
mapOffsetYInput.addEventListener('change', persistOrientationChange);
btnResetOrientation.addEventListener('click', () => {
  const fresh = defaultOrientation(state.map && state.map.uuid);
  Object.assign(mapOrientation, fresh);
  mapRotateSelect.value = String(fresh.rotate);
  mapFlipCheckbox.checked = fresh.flipH;
  mapScaleInput.value = '1';
  mapOffsetXInput.value = '0';
  mapOffsetYInput.value = '0';
  mapScaleValue.textContent = '1.00';
  mapOffsetXValue.textContent = '0.0%';
  mapOffsetYValue.textContent = '0.0%';
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
    '  scale=' + mapOrientation.scale + '  offsetX=' + mapOrientation.offsetX + '  offsetY=' + mapOrientation.offsetY +
    (KNOWN_MAP_ORIENTATIONS[state.map.uuid] ? '  (built-in default for this map)' : ''));
  const hasSides = (state.match.Sides || []).length > 0;
  lines.push('Team sides: ' + (hasSides
    ? 'resolved (' + state.match.Sides.length + ' round(s) -- spike-carrier + spawn-cluster method, see README)'
    : 'not determined for this replay -- falling back to individual per-player colors'));
  lines.push('');
  lines.push('Spawn-frame positions (u/v should be within 0..1 to land on the map image):');
  lines.push(['player', 'PosX', 'PosY', 'u', 'v', 'insideImage'].join('\t'));
  for (const r of rows) {
    lines.push([r.player, r.PosX, r.PosY, r.u, r.v, r.insideImage].join('\t'));
  }

  // Utility markers (state.utility, from utility.json) -- added to help pin down "still shows up
  // at spawn" reports without another round of CLI dump-* commands. Sorted by distance to the
  // nearest player's spawn point (closest first): a marker still stuck at spawn should sit at or
  // very near distance 0. A marker whose SpawnTimeMs is near 0 and whose DespawnTimeMs never
  // arrives (open for the whole match) is the same shape as the AggroBot_PC / Ability_* container
  // bugs already fixed -- some other persistent, per-player container actor is the next suspect if
  // this list still shows one sitting at a spawn point after those fixes.
  const spawnPoints = rows.map((r) => ({ x: r.PosX, y: r.PosY }));
  function distToNearestSpawn(x, y) {
    if (spawnPoints.length === 0 || x == null || y == null) return null;
    let best = Infinity;
    for (const p of spawnPoints) {
      const d = Math.hypot(x - p.x, y - p.y);
      if (d < best) best = d;
    }
    return best;
  }
  const utilityRows = (state.utility || []).map((u) => {
    const uv = (u.X != null && u.Y != null) ? worldToUv(u.X, u.Y, state.map) : null;
    const dist = distToNearestSpawn(u.X, u.Y);
    const iconMatch = resolveUtilityAbilityMatch(u);
    return {
      ClassPath: u.ClassPath || '(none)',
      Category: u.Category,
      AgentRealName: u.AgentRealName || '(none)',
      DescriptiveKeyword: u.DescriptiveKeyword || '(none)',
      IconMatch: iconMatch ? (iconMatch.ability.displayName + ' (score ' + iconMatch.score + ')') : '(no confident match -- colored shape)',
      X: u.X != null ? Number(u.X.toFixed(1)) : null,
      Y: u.Y != null ? Number(u.Y.toFixed(1)) : null,
      u: uv ? Number(uv.u.toFixed(4)) : null,
      v: uv ? Number(uv.v.toFixed(4)) : null,
      insideImage: uv ? (uv.u >= 0 && uv.u <= 1 && uv.v >= 0 && uv.v <= 1) : null,
      distToNearestSpawn: dist != null ? Number(dist.toFixed(1)) : null,
      SpawnTimeMs: u.SpawnTimeMs,
      DespawnTimeMs: u.DespawnTimeMs == null ? '(never closes)' : u.DespawnTimeMs,
      activeAtPlayhead: u.SpawnTimeMs != null && state.currentTimeMs >= u.SpawnTimeMs &&
        (u.DespawnTimeMs == null || state.currentTimeMs < u.DespawnTimeMs),
    };
  }).sort((a, b) => (a.distToNearestSpawn == null ? Infinity : a.distToNearestSpawn) -
    (b.distToNearestSpawn == null ? Infinity : b.distToNearestSpawn));

  lines.push('');
  lines.push('Utility markers (state.utility), closest to a player spawn point first -- ' +
    utilityRows.length + ' total, current playhead ' + state.currentTimeMs + 'ms. IconMatch shows ' +
    'which real ability icon (if any) this marker resolved to -- see README\'s "Real ability icons" section.');
  lines.push(['ClassPath', 'Category', 'AgentRealName', 'DescriptiveKeyword', 'IconMatch', 'X', 'Y', 'u', 'v',
    'insideImage', 'distToNearestSpawn', 'SpawnTimeMs', 'DespawnTimeMs', 'activeAtPlayhead'].join('\t'));
  for (const r of utilityRows) {
    lines.push([r.ClassPath, r.Category, r.AgentRealName, r.DescriptiveKeyword, r.IconMatch, r.X, r.Y, r.u, r.v,
      r.insideImage, r.distToNearestSpawn, r.SpawnTimeMs, r.DespawnTimeMs, r.activeAtPlayhead].join('\t'));
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
  const round = findRoundAt(state.match.Rounds || [], state.currentTimeMs);
  const roundNumber = round ? round.RoundNumber : null;
  (state.match.Players || []).forEach((p) => {
    const row = document.createElement('div');
    row.className = 'roster-row';
    const swatch = document.createElement('span');
    swatch.className = 'swatch';
    swatch.style.background = colorForPlayer(p, roundNumber);
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
    const playableStart = playableStartMs(r);

    // Freeze-time (buy phase) segment -- deliberately smaller/dimmer (see .chapter-freeze in
    // style.css) than the round-play segment next to it, and unlabeled, so it reads as "the
    // quiet bit before the round" rather than competing with the round number for attention.
    const freezeWidthPct = Math.max(0.3, ((playableStart - r.StartTimeMs) / duration) * 100);
    const freezeBtn = document.createElement('button');
    freezeBtn.className = 'chapter chapter-freeze';
    freezeBtn.style.flex = '0 0 ' + freezeWidthPct + '%';
    freezeBtn.title = 'Round ' + roundNumberForDisplay(r.RoundNumber) + ' -- freeze time (buy phase)';
    freezeBtn.dataset.round = String(r.RoundNumber);
    freezeBtn.onclick = () => { state.currentTimeMs = r.StartTimeMs; updateTimeUi(); };
    chaptersEl.appendChild(freezeBtn);

    // Playable round segment.
    const roundWidthPct = Math.max(0.5, ((end - playableStart) / duration) * 100);
    const btn = document.createElement('button');
    btn.className = 'chapter';
    btn.style.flex = '0 0 ' + roundWidthPct + '%';
    btn.textContent = 'R' + roundNumberForDisplay(r.RoundNumber);
    btn.title = 'Round ' + roundNumberForDisplay(r.RoundNumber) + ' -- jumps to end of freeze time, not the buy phase start';
    btn.dataset.round = String(r.RoundNumber);
    // Jump to when the round is actually playable, not the raw roundStarted (freeze-time-start)
    // timestamp -- see playableStartMs().
    btn.onclick = () => { state.currentTimeMs = playableStart; updateTimeUi(); };
    chaptersEl.appendChild(btn);
  });
}

// ---------------------------------------------------------------------------
// Transport controls
// ---------------------------------------------------------------------------

let lastRosterRound; // see updateTimeUi() below

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
  // Jump to when the round is actually playable (after freeze time), not the raw
  // roundStarted timestamp -- see playableStartMs().
  if (current && state.currentTimeMs - playableStartMs(current) > 3000) {
    state.currentTimeMs = playableStartMs(current);
  } else {
    const idx = rounds.indexOf(current);
    state.currentTimeMs = idx > 0 ? playableStartMs(rounds[idx - 1]) : 0;
  }
  updateTimeUi();
});

btnNextRound.addEventListener('click', () => {
  const rounds = state.match.Rounds || [];
  const current = findRoundAt(rounds, state.currentTimeMs);
  const idx = rounds.indexOf(current);
  state.currentTimeMs = (idx >= 0 && idx + 1 < rounds.length) ? playableStartMs(rounds[idx + 1]) : state.durationMs;
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
  if (round) {
    // Relative to playableStartMs (end of freeze time), not round.StartTimeMs (start of freeze
    // time) -- so this reads 0:00 at the moment the round actually becomes playable, matching
    // what "Round N, 0:00" means in-game, rather than at the buy-phase barrier drop.
    const relativeMs = state.currentTimeMs - playableStartMs(round);
    const roundClock = relativeMs < 0
      ? ('freeze ' + formatClock(-relativeMs) + ' left')
      : formatClock(relativeMs);
    roundLabel.textContent = 'Round ' + roundNumberForDisplay(round.RoundNumber) + ' · ' + roundClock;
  } else {
    roundLabel.textContent = '';
  }

  // Highlight whichever of the two segments (freeze-time vs playable) for the current round the
  // playhead is actually in -- not both at once -- so the chapters bar answers "am I still in
  // the buy phase right now?" at a glance.
  chaptersEl.querySelectorAll('.chapter').forEach((btn) => {
    if (!round || btn.dataset.round !== String(round.RoundNumber)) {
      btn.classList.toggle('active', false);
      return;
    }
    const isFreezeSegment = btn.classList.contains('chapter-freeze');
    const stillInFreezeTime = state.currentTimeMs < playableStartMs(round);
    btn.classList.toggle('active', isFreezeSegment === stillInFreezeTime);
  });

  // Roster swatches show attack/defense color, which can flip between rounds (halftime) --
  // rebuild them only when the round actually changed, not on every scrub/frame tick.
  const roundNumber = round ? round.RoundNumber : null;
  if (roundNumber !== lastRosterRound) {
    lastRosterRound = roundNumber;
    buildRoster();
  }
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
    const icon = resolveUtilityIconImage(u);

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
      // The line itself still shows the wall's length/orientation -- the icon just marks its
      // midpoint, same as it marks the center of an area effect below.
      drawUtilityIcon(icon, px, py);
    } else if (u.Category === 'Smoke' || u.Category === 'IncendiaryOrMolly') {
      // Area effects keep their translucent radius circle -- that conveys real spatial info
      // (how much ground it covers) an icon alone can't -- and the icon marks its center on top.
      const edge = toPixel(u.X + UTILITY_AREA_RADIUS_UNITS, u.Y, w, h);
      const radiusPx = Math.max(Math.hypot(edge.x - origin.x, edge.y - origin.y), 4);
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.arc(px, py, radiusPx, 0, Math.PI * 2);
      ctx.fill();
      drawUtilityIcon(icon, px, py);
    } else if (icon) {
      // Point markers (traps, turrets, drones, thrown projectiles): the real ability icon
      // replaces the plain dot entirely once one is confidently matched.
      drawUtilityIcon(icon, px, py);
    } else {
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.arc(px, py, 5, 0, Math.PI * 2);
      ctx.fill();
    }
  }
}

/**
 * Draws a resolved ability icon (see resolveUtilityIconImage) centered at (px, py), with a small
 * dark backing circle so a light-colored icon stays legible over any part of the map art. A
 * no-op if the image hasn't finished loading yet (or failed to) -- next frame picks it up once it
 * has, same as the player-portrait icons elsewhere in this file.
 */
function drawUtilityIcon(icon, px, py) {
  if (!icon || !icon.complete || icon.naturalWidth === 0) return;
  const half = UTILITY_ICON_SIZE_PX / 2;
  try {
    ctx.fillStyle = 'rgba(0,0,0,0.55)';
    ctx.beginPath();
    ctx.arc(px, py, half + 2, 0, Math.PI * 2);
    ctx.fill();
    ctx.drawImage(icon, px - half, py - half, UTILITY_ICON_SIZE_PX, UTILITY_ICON_SIZE_PX);
  } catch (err) {
    // A broken/errored image can throw in some browsers -- fall through silently, same as the
    // (still-drawn) colored shape would if this function weren't called at all.
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
  const round = findRoundAt(state.match.Rounds || [], state.currentTimeMs);
  const roundNumber = round ? round.RoundNumber : null;

  for (const key of Object.keys(state.visionCones)) {
    const cones = state.visionCones[key];
    if (!cones || cones.length === 0) continue;

    const idx = findTimedIndex(cones, state.currentTimeMs);
    const cone = idx >= 0 ? cones[idx] : cones[0];
    if (!cone) continue;

    const player = state.playerByKey.get(key);
    const color = player ? colorForPlayer(player, roundNumber) : (state.playerColor.get(key) || '#ffffff');
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
  const round = findRoundAt(state.match.Rounds || [], state.currentTimeMs);
  const roundNumber = round ? round.RoundNumber : null;

  for (const track of state.tracks) {
    const player = track.Player;
    const key = playerKey(player);
    const color = colorForPlayer(player, roundNumber);

    // Once this player has died this round, stop drawing their live icon and mark where they
    // died instead -- for the rest of the round (scrubbing back before the death time still shows
    // them alive, since state.currentTimeMs < death.TimeMs again).
    const death = roundNumber != null ? state.deaths.get(key)?.get(roundNumber) : null;
    if (death && state.currentTimeMs >= death.TimeMs) {
      const deathPos = toPixel(death.X, death.Y, w, h);
      drawDeathMarker(deathPos.x, deathPos.y, color);
      continue;
    }

    const sample = interpolateSample(track.Samples, state.currentTimeMs);
    if (!sample) continue;

    const origin = toPixel(sample.PosX, sample.PosY, w, h);
    const px = origin.x, py = origin.y;

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

/** Death marker: an X in the dying player's team color, at the position they died. */
function drawDeathMarker(px, py, color) {
  const r = AGENT_ICON_RADIUS_PX * 0.8;
  ctx.strokeStyle = color;
  ctx.lineWidth = 3;
  ctx.beginPath();
  ctx.moveTo(px - r, py - r);
  ctx.lineTo(px + r, py + r);
  ctx.moveTo(px + r, py - r);
  ctx.lineTo(px - r, py + r);
  ctx.stroke();
}
