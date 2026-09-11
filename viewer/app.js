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

// Known-good FULL orientations (rotate, flip, AND scale/pan) hand-dialed-in once by a person using
// the manual sliders until positions matched real play, so nobody has to rediscover them per map --
// and so they're a fixed constant rather than something dependent on this browser's own
// localStorage or a given replay's own recorded footprint (which is what the automatic-calibration
// paths below produce instead). This is OUR OWN empirical finding, not part of Riot's published
// data -- it doesn't come from (and isn't overwritten by) Fetch-Assets.ps1. If a map isn't listed
// here, the manual sliders and/or automatic calibration below still work exactly as before; add an
// entry once a map's correct rotate/flip/scale/pan is confirmed by hand (see README's
// map-orientation section for how).
const KNOWN_MAP_ORIENTATIONS = {
  // Sunset -- rotate/flip confirmed twice (matches a player's own room-by-room read of a live
  // match, and separately matches 8 of 10 real player positions from a screenshot to within a few
  // % of the map -- the remaining two were players sprinting at that exact instant, not a problem
  // with the rotation itself; see README), scale/pan later fine-tuned by hand on top of that.
  '92584fbe-486a-b1b2-9faa-39b0f486b498': { rotate: 90, flipH: true, scale: 1.01, offsetX: 0.01, offsetY: 0.02 },
  // Ascent -- rotate/flip originally confirmed from a real match's own calibration scores (64%,
  // a clear ~16-point margin over every alternative -- see CONFIRMED_ORIENTATIONS's doc comment
  // for why that's confirmation despite not being a clean majority), scale/pan then hand-dialed on
  // top of that until positions matched real play.
  '7eaecc1b-4337-bbf6-6ab9-04b8f06b3319': { rotate: 90, flipH: true, scale: 0.96, offsetX: 0.369, offsetY: 0.385 },
};

function defaultOrientation(mapUuid) {
  const known = mapUuid && KNOWN_MAP_ORIENTATIONS[mapUuid];
  if (known) return { rotate: known.rotate, flipH: known.flipH, scale: known.scale, offsetX: known.offsetX, offsetY: known.offsetY };
  // Falls back to whatever this session's own automatic calibration (below) worked out, if
  // anything -- so "Reset map fit" and a fresh page load both land on the computed value instead
  // of blank 0/no-flip once one's been found.
  const auto = mapUuid && autoDetectedOrientations[mapUuid];
  if (auto && !auto.error) {
    return { rotate: auto.rotate, flipH: auto.flipH, scale: auto.scale, offsetX: auto.offsetX, offsetY: auto.offsetY };
  }
  return { rotate: 0, flipH: false, scale: 1, offsetX: 0, offsetY: 0 };
}

// A *different* correction from KNOWN_MAP_ORIENTATIONS above. That table nudges where each
// player/utility DOT lands, while leaving the downloaded minimap picture itself drawn exactly as
// downloaded -- the right fix when the position-data formula and the image disagree about
// orientation, but the image's own content is otherwise fine. This table instead physically spins
// the drawn picture itself (see drawMapImage below), for the case where the picture as downloaded
// is just sideways/upside-down -- dots keep using the unmodified position formula and only the art
// underneath them turns. The two are independent and can be combined if a map genuinely needs
// both, but start with only one and see if that alone lines everything up.
const MAP_IMAGE_ROTATIONS = {
  // Ascent was tried here at 90 (and, before that, in KNOWN_MAP_ORIENTATIONS' dot-position table
  // at 90) based on a user's visual impression -- both guesses reportedly made alignment WORSE,
  // not better, which means neither guess should be trusted, including a further blind guess at
  // 180/270. Left empty (no correction) until it's pinned down from the Debug info panel's actual
  // u/v numbers against a real known reference point, the same way Sunset's entry was derived --
  // see the "How to verify a map's rotation" section in the README before adding an entry here.
};

/** Draws the current map image, physically rotated per MAP_IMAGE_ROTATIONS if this map has an
 * entry (clockwise degrees) -- see the comment on that table for how this differs from the
 * dot-position correction above. Assumes a roughly square source image/canvas, true of every
 * competitive map's valorant-api.com `displayIcon` seen so far, so rotating 90°/270° around the
 * center still fills the canvas edge-to-edge without needing to swap width/height. */
function drawMapImage(w, h) {
  if (!state.mapImage) return;
  const rotateDeg = (state.map && MAP_IMAGE_ROTATIONS[state.map.uuid]) || 0;
  if (((rotateDeg % 360) + 360) % 360 === 0) {
    ctx.drawImage(state.mapImage, 0, 0, w, h);
    return;
  }
  ctx.save();
  ctx.translate(w / 2, h / 2);
  ctx.rotate(rotateDeg * Math.PI / 180); // canvas rotate() is clockwise for a positive angle
  ctx.drawImage(state.mapImage, -w / 2, -h / 2, w, h);
  ctx.restore();
}

// Automatic per-map orientation calibration ------------------------------------------------
// The manual Map orientation control above works, but needs a person to eyeball a real screenshot
// or a known callout to tell which of 8 possibilities (4 rotations x flip/no-flip) is right --
// not always available (e.g. a recording player who no longer has access to that match in-game).
// This derives the same (rotate, flipH) pair with no external reference at all, from two things
// this project already has for every replay:
//
//  1. Every downloaded competitive-map image (valorant-api.com's `displayIcon`) is a square PNG
//     with the actual playable map inset and its four corners left fully transparent. A real
//     recorded position can only ever be somewhere on the actual map -- so it can only ever be
//     correct to draw it over an OPAQUE pixel of that image, never one of the transparent corners.
//  2. Riot's own coordinate formula (worldToUv) already places the WHOLE match's real footprint
//     somewhere inside the [0,1] square, regardless of image orientation -- what differs per
//     candidate rotate/flip is only where inside that square each point ends up.
//
// So: for each of the 8 possible (rotate, flipH) pairs, transform every recorded movement sample
// (already loaded -- no extra data or user input needed) and measure what fraction land on an
// opaque pixel of the downloaded image rather than a transparent corner. A real map's shape is far
// from rotationally symmetric, so in practice one candidate clearly wins; this only auto-applies a
// result when it clears real confidence thresholds (below), and otherwise leaves the manual
// controls exactly as they were -- never forcing a low-confidence guess, which is exactly what
// went wrong with the two prior hand-picked attempts at Ascent.
const AUTO_ORIENTATION_CANDIDATES = [
  { rotate: 0, flipH: false }, { rotate: 90, flipH: false }, { rotate: 180, flipH: false }, { rotate: 270, flipH: false },
  { rotate: 0, flipH: true }, { rotate: 90, flipH: true }, { rotate: 180, flipH: true }, { rotate: 270, flipH: true },
];
const AUTO_ORIENTATION_MIN_SCORE = 0.75; // winning candidate must land at least this often
const AUTO_ORIENTATION_MIN_LEAD = 0.12; // ...and beat the runner-up by at least this much
const AUTO_ORIENTATION_MIN_VOID_FRACTION = 0.02; // image must have at least this much real transparency to calibrate against at all
// Rotation/flip alone can only ever be right when the recorded footprint already happens to be
// scaled/centered the same way the downloaded image is. Real data (Ascent) showed that's often not
// true at all -- one team's spawn can sit more than 3x farther from center than the other's, and
// hundreds of utility markers across a match can fall well outside [0,1] on both axes, no matter
// which of the 8 rotate/flip candidates is tried. Rotation and flip are both isometries about the
// center point, so they preserve every point's distance from center exactly -- meaning no amount of
// rotating/flipping can ever pull a point back inside [0,1] if it's already too far out. Only a
// scale (zoom) + recentering offset can fix that. So each candidate below is fit with its own
// best-guess scale/offset (from the recorded footprint's own robust extent) before being scored --
// the scoring then has to fall back on actual pixel shape to pick a winner, not just "did I forget
// to zoom out enough".
const AUTO_ORIENTATION_TRIM = 0.01; // trim the extreme 1% of points on each side per axis before measuring the footprint's extent, so a handful of rare stray/glitched samples can't blow up the fit
const AUTO_ORIENTATION_TARGET_HALF_EXTENT = 0.46; // fit the footprint to +/-46% from center (an ~8% margin so it doesn't touch the image edge exactly)

// Rotations CONFIRMED from a real match's own calibration scores (not a visual guess), for a map
// where the rotation is settled but scale/pan should still be fit fresh per replay rather than
// hand-fixed -- when a map is listed here, autoDetectOrientation skips picking a winner by score
// among the 8 candidates and instead fits scale/offset directly for this pinned (rotate, flipH)
// pair, using the exact same per-candidate fit + opacity scoring as every other candidate (so it's
// still a real, data-driven fit, just for a rotation that's already settled rather than re-decided
// every time). Compare this to KNOWN_MAP_ORIENTATIONS above, which pins scale/pan too, for a map
// where those have ALSO been hand-dialed-in and found to work well as fixed constants.
//
// Why this table needs to exist at all, rather than just trusting AUTO_ORIENTATION_MIN_SCORE/_LEAD
// every time: real official Valorant minimap art has plenty of transparent VOID *inside* its outer
// silhouette too -- walls, out-of-bounds interior gaps -- not just the four corners this feature's
// whole approach is built on. A correct rotation can legitimately still only land some fraction of
// real recorded positions on an opaque pixel, well under what a clean synthetic test would suggest.
// Ascent was the confirmed case that motivated this table (a real 24-round, 10-player match scored
// rotate=90+flip at 64% -- clearly, consistently ahead of every other candidate, next-best 51%,
// down to 33% for the worst -- but 64% alone doesn't clear AUTO_ORIENTATION_MIN_SCORE of 0.75), and
// has since moved to KNOWN_MAP_ORIENTATIONS once its scale/pan were also hand-confirmed. Empty for
// now -- add an entry here for a map whose rotation alone is confirmed this way, before its
// scale/pan has been separately dialed in and fixed.
const CONFIRMED_ORIENTATIONS = {};

// mapUuid -> { rotate, flipH, score, scores } on success, or { error, scores? } when inconclusive.
// Session-only (not persisted itself -- a successful result gets persisted like any manual choice
// via saveMapOrientation, see maybeAutoDetectOrientation below); this cache just avoids recomputing
// on every re-render and lets the Debug info panel report exactly what happened.
let autoDetectedOrientations = {};

function rotateFlipAroundCenter(u, v, rotate, flipH) {
  let x = u - 0.5, y = v - 0.5;
  if (flipH) x = -x;
  switch (((rotate % 360) + 360) % 360) {
    case 90: { const nx = -y, ny = x; x = nx; y = ny; break; }
    case 180: { x = -x; y = -y; break; }
    case 270: { const nx = y, ny = -x; x = nx; y = ny; break; }
    default: break;
  }
  return { u: x + 0.5, v: y + 0.5 };
}

/** Value at percentile `p` (0..1) of an already-sorted numeric array. */
function percentileOf(sortedArr, p) {
  if (sortedArr.length === 0) return 0;
  const idx = Math.min(sortedArr.length - 1, Math.max(0, Math.round(p * (sortedArr.length - 1))));
  return sortedArr[idx];
}

/** Given a candidate (rotate, flipH) and every recorded point (already run through worldToUv, so
 * still in the *unrotated* [0,1]-ish space), works out the uniform scale + recentering offset that
 * would fit that candidate's rotated/flipped footprint snugly inside the image square -- the
 * scale+offset a person would land on by eye if they nudged the sliders themselves. Robust to a
 * handful of stray points via AUTO_ORIENTATION_TRIM; uses ONE scale for both axes (not independent
 * x/y stretch) so the map's own proportions aren't distorted, sized off whichever axis is wider. */
function fitScaleOffset(points, candidate) {
  const xs = [], ys = [];
  for (const p of points) {
    const rf = rotateFlipAroundCenter(p.u, p.v, candidate.rotate, candidate.flipH);
    xs.push(rf.u - 0.5);
    ys.push(rf.v - 0.5);
  }
  xs.sort((a, b) => a - b);
  ys.sort((a, b) => a - b);
  const xLo = percentileOf(xs, AUTO_ORIENTATION_TRIM), xHi = percentileOf(xs, 1 - AUTO_ORIENTATION_TRIM);
  const yLo = percentileOf(ys, AUTO_ORIENTATION_TRIM), yHi = percentileOf(ys, 1 - AUTO_ORIENTATION_TRIM);
  const midX = (xLo + xHi) / 2, midY = (yLo + yHi) / 2;
  const halfW = Math.max(1e-6, (xHi - xLo) / 2);
  const halfH = Math.max(1e-6, (yHi - yLo) / 2);
  const scale = AUTO_ORIENTATION_TARGET_HALF_EXTENT / Math.max(halfW, halfH);
  return { scale, offsetX: -midX * scale, offsetY: -midY * scale };
}

/** Applies a candidate's rotate/flip AND its fitted scale/offset to one worldToUv'd point, in the
 * exact same order applyOrientation() uses live (rotate/flip about center, then scale, then pan) --
 * so what's scored here is exactly what would end up on screen if this candidate were picked. */
function applyCandidateFit(u, v, candidate, fit) {
  const rf = rotateFlipAroundCenter(u, v, candidate.rotate, candidate.flipH);
  const x = (rf.u - 0.5) * fit.scale + fit.offsetX;
  const y = (rf.v - 0.5) * fit.scale + fit.offsetY;
  return { u: x + 0.5, v: y + 0.5 };
}

/** Builds an alpha sampler straight from a map's PRECOMPUTED mask (catalog.js's alphaMask/
 * alphaMaskSize, baked in by Fetch-Assets.ps1 via .NET's System.Drawing on the person's own
 * machine) rather than reading the live <img> back through a canvas. This is the preferred path,
 * and for most people the only one that actually works: viewer/index.html is opened as a plain
 * local file (see the README), and browsers flatly refuse to read a canvas's pixels back out at
 * all on a page loaded that way ("tainted canvas" -- there's no way for a local file to present
 * CORS headers, which is what that check normally looks for) -- no crossOrigin setting or trick
 * fixes this, it has to be worked around by not needing canvas readback in the browser in the
 * first place. Returns null (not an error) when this map has no mask yet, so the caller can fall
 * back to the canvas approach, which still works for anyone who happens to serve the viewer over
 * an actual local web server instead of opening it directly. */
function buildAlphaSamplerFromMask(map) {
  const mask = map && map.alphaMask;
  const size = map && map.alphaMaskSize;
  if (!mask || !size || mask.length !== size * size) return null;
  return {
    alphaAt(u, v) {
      if (u < 0 || u > 1 || v < 0 || v > 1) return 0; // off the square entirely -- never on-map
      const px = Math.min(size - 1, Math.max(0, Math.floor(u * size)));
      const py = Math.min(size - 1, Math.max(0, Math.floor(v * size)));
      return mask.charCodeAt(py * size + px) === 49 /* '1' */ ? 255 : 0; // '0' is 48, '1' is 49
    },
  };
}

/** Reads `image`'s own alpha channel into a small lookup grid, drawn onto a fresh *transparent*
 * offscreen canvas (never the black-background stage canvas) so a genuinely transparent source
 * pixel reads back as alpha 0 rather than blended with black. 256px is plenty for a coarse
 * inside/outside-the-map read and keeps thousands of per-candidate lookups cheap. Fallback ONLY --
 * see buildAlphaSamplerFromMask above for why this throws for most people (a page opened as a
 * plain local file, which is the documented, expected way to use this viewer) -- every caller
 * wraps this in try/catch and treats a throw as "this map has no precomputed mask AND this browser
 * won't allow the fallback either" rather than a hard failure. */
function buildAlphaSampler(image) {
  if (!image || !image.naturalWidth || !image.naturalHeight) return null;
  const SIZE = 256;
  const off = document.createElement('canvas');
  off.width = SIZE;
  off.height = SIZE;
  const offCtx = off.getContext('2d', { willReadFrequently: true });
  offCtx.clearRect(0, 0, SIZE, SIZE);
  offCtx.drawImage(image, 0, 0, SIZE, SIZE);
  const { data } = offCtx.getImageData(0, 0, SIZE, SIZE); // throws if the canvas is tainted
  return {
    alphaAt(u, v) {
      if (u < 0 || u > 1 || v < 0 || v > 1) return 0; // off the square entirely -- never on-map
      const px = Math.min(SIZE - 1, Math.max(0, Math.floor(u * SIZE)));
      const py = Math.min(SIZE - 1, Math.max(0, Math.floor(v * SIZE)));
      return data[(py * SIZE + px) * 4 + 3];
    },
  };
}

/** Coarse fraction of the WHOLE image (a fixed 32x32 grid, independent of any candidate rotation)
 * that's transparent -- used to tell "this image has no real void margin to calibrate against"
 * apart from "none of the 8 candidates happen to fit", which need different explanations. */
function wholeImageTransparentFraction(sampler) {
  const N = 32;
  let transparent = 0;
  for (let i = 0; i < N; i++) {
    for (let j = 0; j < N; j++) {
      if (sampler.alphaAt((i + 0.5) / N, (j + 0.5) / N) <= 40) transparent++;
    }
  }
  return transparent / (N * N);
}

/** Every recorded world position worth checking -- every player's every movement sample across
 * the whole match. movement.json is already thinned to at most 10 samples/sec/player (see the
 * README); this additionally strides down to a few thousand points, which is plenty to confidently
 * separate 8 candidates without doing tens of thousands of image lookups per candidate. */
function collectCalibrationPoints(tracks) {
  const all = [];
  for (const track of tracks) {
    for (const s of track.Samples || []) {
      if (s.PosX != null && s.PosY != null) all.push(s);
    }
  }
  const CAP = 4000;
  if (all.length <= CAP) return all;
  const stride = Math.ceil(all.length / CAP);
  const strided = [];
  for (let i = 0; i < all.length; i += stride) strided.push(all[i]);
  return strided;
}

/** Tries every (rotate, flipH) pair and returns the one whose transformed points most consistently
 * land on opaque image pixels -- or `{ error }` if the image has no usable transparent margin, or
 * no candidate clears the confidence bar (AUTO_ORIENTATION_MIN_SCORE/_LEAD), in which case the
 * caller leaves the manual controls exactly as they were rather than force a guess. */
function autoDetectOrientation(map, image, tracks) {
  let sampler = buildAlphaSamplerFromMask(map);
  if (!sampler) {
    try {
      sampler = buildAlphaSampler(image);
    } catch {
      return {
        error: "this map has no precomputed alpha mask yet, and this browser won't allow reading " +
          "the map image's pixels back directly either (a security restriction on a page opened as " +
          "a plain local file) -- re-run scripts/Fetch-Assets.ps1 (no need for -Force) to add the " +
          "mask this needs, or use the manual controls",
      };
    }
  }
  if (!sampler) return { error: 'map image not ready yet' };

  const voidFraction = wholeImageTransparentFraction(sampler);
  if (voidFraction < AUTO_ORIENTATION_MIN_VOID_FRACTION) {
    return { error: 'this map image has only ' + (voidFraction * 100).toFixed(1) + '% transparent area -- not enough of a void margin to calibrate rotation against for this map' };
  }

  const samples = collectCalibrationPoints(tracks);
  if (samples.length < 20) {
    return { error: 'not enough recorded positions in this replay to calibrate from (' + samples.length + ')' };
  }
  // worldToUv doesn't depend on the candidate being tried, so run it once per sample rather than
  // once per (sample, candidate) pair.
  const points = samples.map((s) => worldToUv(s.PosX, s.PosY, map));

  const scores = AUTO_ORIENTATION_CANDIDATES.map((candidate) => {
    const fit = fitScaleOffset(points, candidate);
    let onMap = 0;
    for (const p of points) {
      const oriented = applyCandidateFit(p.u, p.v, candidate, fit);
      if (sampler.alphaAt(oriented.u, oriented.v) > 40) onMap++;
    }
    return { candidate, fit, score: onMap / points.length };
  });
  scores.sort((a, b) => b.score - a.score);

  // A map already confirmed via a real match's own scores (see CONFIRMED_ORIENTATIONS's doc
  // comment) skips the confidence gate below and always uses its pinned rotation -- but still gets
  // scale/offset fit fresh from THIS match's own recorded footprint, same as any other candidate,
  // rather than reusing whatever scale/offset happened to be fit the first time it was confirmed.
  const confirmed = map && map.uuid && CONFIRMED_ORIENTATIONS[map.uuid];
  if (confirmed) {
    const match = scores.find((s) => s.candidate.rotate === confirmed.rotate && s.candidate.flipH === confirmed.flipH);
    return {
      rotate: match.candidate.rotate,
      flipH: match.candidate.flipH,
      scale: match.fit.scale,
      offsetX: match.fit.offsetX,
      offsetY: match.fit.offsetY,
      score: match.score,
      scores,
      confirmed: true,
    };
  }

  const best = scores[0], runnerUp = scores[1];
  if (best.score < AUTO_ORIENTATION_MIN_SCORE || best.score - runnerUp.score < AUTO_ORIENTATION_MIN_LEAD) {
    return {
      error: 'no single orientation clearly wins (best ' + (best.score * 100).toFixed(0) + '%, runner-up ' +
        (runnerUp.score * 100).toFixed(0) + '%) -- not confident enough to auto-apply',
      scores,
    };
  }

  return {
    rotate: best.candidate.rotate,
    flipH: best.candidate.flipH,
    scale: best.fit.scale,
    offsetX: best.fit.offsetX,
    offsetY: best.fit.offsetY,
    score: best.score,
    scores,
  };
}

/** Runs auto-detection at most once per map per browser session, and only when there's nothing
 * more authoritative to respect already: a hand-confirmed KNOWN_MAP_ORIENTATIONS entry, or a value
 * already saved for this map (a person's own manual choice, or an earlier auto-detected one from a
 * previous load) -- this never overwrites either. On success, applies the result exactly like a
 * manual choice (updates the live controls, persists it via saveMapOrientation) so it behaves
 * identically to one from here on, including surviving "Reset map fit" and future reloads. */
function maybeAutoDetectOrientation() {
  const uuid = state.map && state.map.uuid;
  if (!uuid || !state.mapImage) return;
  if (KNOWN_MAP_ORIENTATIONS[uuid]) return;
  if (autoDetectedOrientations[uuid]) return;
  try {
    if (localStorage.getItem(orientationStorageKey(uuid))) return;
  } catch { /* private mode / storage disabled -- fall through and just recompute each load */ }

  const result = autoDetectOrientation(state.map, state.mapImage, state.tracks);
  autoDetectedOrientations[uuid] = result;
  if (!result.error) {
    // Guards against the extremely unlikely race of a person manually adjusting any control while
    // this async image load was still in flight -- don't clobber a choice they just made.
    const untouched = mapOrientation.rotate === 0 && !mapOrientation.flipH &&
      mapOrientation.scale === 1 && !mapOrientation.offsetX && !mapOrientation.offsetY;
    if (untouched) {
      mapOrientation.rotate = result.rotate;
      mapOrientation.flipH = result.flipH;
      mapOrientation.scale = result.scale;
      mapOrientation.offsetX = result.offsetX;
      mapOrientation.offsetY = result.offsetY;
      syncOrientationControlsFromState();
      saveMapOrientation(uuid);
    }
  }
  // Refresh the (already-generated, possibly now-stale) debug text so whoever opens that panel
  // sees this result without needing to nudge a slider first -- doesn't force the panel open,
  // `hidden` here only controls whether its collapsed <summary> is available to click at all,
  // already true by this point in the load sequence.
  if (state.tracks.length > 0) logSpawnDebugInfo();
}

function describeAutoDetection(uuid) {
  if (KNOWN_MAP_ORIENTATIONS[uuid]) return 'not run -- this map already has a full hand-confirmed orientation (rotation, scale, and pan) built in';
  let saved = null;
  try { saved = localStorage.getItem(orientationStorageKey(uuid)); } catch { /* ignore */ }
  const result = autoDetectedOrientations[uuid];
  const fmtScores = (scores) => scores.map((s) =>
    (s.candidate.rotate + (s.candidate.flipH ? '+flip' : '') + '=' + (s.score * 100).toFixed(0) + '%')).join(', ');
  if (!result) {
    return saved ? 'not run -- this map already has a saved orientation in this browser' : 'not run yet';
  }
  if (result.error) {
    return 'ran, inconclusive -- ' + result.error + (result.scores ? '  [' + fmtScores(result.scores) + ']' : '');
  }
  return 'applied' + (result.confirmed ? ' (rotation pre-confirmed from real match data -- see CONFIRMED_ORIENTATIONS -- scale/offset still freshly fit)' : '') +
    ' -- rotate=' + result.rotate + ' flipH=' + result.flipH +
    ' scale=' + result.scale.toFixed(2) +
    ' offsetX=' + (Math.round(result.offsetX * 1000) / 10).toFixed(1) + '%' +
    ' offsetY=' + (Math.round(result.offsetY * 1000) / 10).toFixed(1) + '%' +
    ' (' + (result.score * 100).toFixed(0) + '% of recorded positions landed on the map image)  [' +
    fmtScores(result.scores) + ']';
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
    // Only ever present on a catalog.js entry (Fetch-Assets.ps1 bakes these in) -- match.json's own
    // embedded Map info never has them, which is fine: loadMapImage() below fills these in from the
    // catalog once it looks up this map's image anyway. See buildAlphaSamplerFromMask's doc comment
    // for what these are for.
    alphaMask: m.alphaMask || m.AlphaMask || null,
    alphaMaskSize: m.alphaMaskSize || m.AlphaMaskSize || 0,
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

/** Pushes the current mapOrientation values into every slider/select/readout in the Map orientation
 * panel, without touching mapOrientation itself or persisting anything -- the one place all of
 * applyLoadedOrientation / maybeAutoDetectOrientation / "Reset map fit" go to keep the controls in
 * sync with state, so a value computed programmatically (loaded, auto-detected, or reset) always
 * shows up in the UI exactly like a person's own manual drag would have. */
function syncOrientationControlsFromState() {
  const o = mapOrientation;
  mapRotateSelect.value = String(o.rotate);
  mapFlipCheckbox.checked = o.flipH;
  mapScaleInput.value = String(o.scale);
  // Rounded to 0.1% (matching the sliders' step="0.1") rather than a whole percent -- panning
  // used to jump by whole percentage points per tick, which was much too coarse.
  const offsetXPct = Math.round(o.offsetX * 1000) / 10;
  const offsetYPct = Math.round(o.offsetY * 1000) / 10;
  mapOffsetXInput.value = String(offsetXPct);
  mapOffsetYInput.value = String(offsetYPct);
  mapScaleValue.textContent = o.scale.toFixed(2);
  mapOffsetXValue.textContent = offsetXPct.toFixed(1) + '%';
  mapOffsetYValue.textContent = offsetYPct.toFixed(1) + '%';
}

/** Loads this map's remembered orientation correction (if any) into state + the UI controls. */
function applyLoadedOrientation() {
  const loaded = loadMapOrientation(state.map && state.map.uuid);
  mapOrientation.rotate = loaded.rotate;
  mapOrientation.flipH = loaded.flipH;
  mapOrientation.scale = loaded.scale;
  mapOrientation.offsetX = loaded.offsetX;
  mapOrientation.offsetY = loaded.offsetY;
  syncOrientationControlsFromState();
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
  const uuid = state.map && state.map.uuid;
  // Clear the saved value outright (rather than overwriting it with a fresh save, which is what
  // this used to do) -- an overwrite here would immediately re-persist whatever defaultOrientation
  // falls back to, which permanently blocks maybeAutoDetectOrientation's "already saved for this
  // map" guard from ever letting the automatic calibration run again. Also drop this map's cached
  // auto-detect result so it's free to recompute rather than reusing a stale one.
  if (uuid) {
    try { localStorage.removeItem(orientationStorageKey(uuid)); } catch { /* ignore */ }
    delete autoDetectedOrientations[uuid];
  }
  const fresh = defaultOrientation(uuid);
  Object.assign(mapOrientation, fresh);
  syncOrientationControlsFromState();
  // Re-run calibration immediately (rather than waiting for a future reload) so Reset actually
  // takes effect right away when the map image is already loaded.
  if (uuid && state.mapImage) {
    maybeAutoDetectOrientation();
  } else if (state.tracks.length > 0) {
    logSpawnDebugInfo();
  }
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

  // match.json's own embedded Map info (the `detected` path in resolveMap()) never carries the
  // alpha mask -- only a catalog.js entry does -- so backfill it here now that this map's own
  // catalog entry is in hand, regardless of which path set state.map.
  state.map.alphaMask = entry.alphaMask || entry.AlphaMask || null;
  state.map.alphaMaskSize = entry.alphaMaskSize || entry.AlphaMaskSize || 0;

  const img = new Image();
  img.onload = () => { state.mapImage = img; hideMapOverlay(); maybeAutoDetectOrientation(); };
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

  // Every player.json/movement.json entry is listed here, INCLUDING one with zero movement
  // samples at all -- that's a real, distinct failure mode ("this player never got a single
  // position") from one whose first sample just lands outside the image, and the two look
  // identical if a track with no samples is silently skipped instead (which this used to do,
  // and which is exactly how a whole missing team could go undiagnosed -- it would just never
  // show up in this list, instead of showing up with an obvious "no movement samples" flag).
  const rows = state.tracks.map((track) => {
    const player = (track.Player && (track.Player.AgentName || playerKey(track.Player))) || '(unknown)';
    const sampleCount = (track.Samples && track.Samples.length) || 0;
    const sample = track.Samples && track.Samples[0];
    if (!sample) {
      return { player, sampleCount, PosX: '-', PosY: '-', u: '-', v: '-', insideImage: '-' };
    }
    const uv = worldToUv(sample.PosX, sample.PosY, state.map);
    return {
      player,
      sampleCount,
      PosX: Number(sample.PosX.toFixed(1)),
      PosY: Number(sample.PosY.toFixed(1)),
      u: Number(uv.u.toFixed(4)),
      v: Number(uv.v.toFixed(4)),
      insideImage: uv.u >= 0 && uv.u <= 1 && uv.v >= 0 && uv.v <= 1,
    };
  });

  const lines = [];
  lines.push('Map: ' + state.map.displayName);
  lines.push('xMultiplier=' + state.map.xMultiplier + '  yMultiplier=' + state.map.yMultiplier +
    '  xScalarToAdd=' + state.map.xScalarToAdd + '  yScalarToAdd=' + state.map.yScalarToAdd);
  lines.push('Map orientation control: rotate=' + mapOrientation.rotate + '  flipH=' + mapOrientation.flipH +
    '  scale=' + mapOrientation.scale + '  offsetX=' + mapOrientation.offsetX + '  offsetY=' + mapOrientation.offsetY +
    (KNOWN_MAP_ORIENTATIONS[state.map.uuid] ? '  (built-in default for this map)' : ''));
  lines.push('Map image rotation (separate from the above -- turns the picture itself, not the dots): ' +
    ((state.map && MAP_IMAGE_ROTATIONS[state.map.uuid]) || 0) + '°');
  lines.push('Automatic orientation calibration: ' + describeAutoDetection(state.map.uuid));
  const hasSides = (state.match.Sides || []).length > 0;
  lines.push('Team sides: ' + (hasSides
    ? 'resolved (' + state.match.Sides.length + ' round(s) -- spike-carrier + spawn-cluster method, see README)'
    : 'not determined for this replay -- falling back to individual per-player colors'));
  lines.push('');
  lines.push('Players (from movement.json) -- ' + rows.length + ' total, ' +
    rows.filter((r) => r.sampleCount > 0).length + ' with at least one movement sample. A player ' +
    'with 0 samples never had a single position recorded anywhere in movement.parquet (this ' +
    'project never invents one) -- if that\'s a whole enemy team, the likely cause is that the ' +
    '.vrf was recorded from one player\'s own client, which (like the live game itself) only ever ' +
    'receives position updates for enemies it has actually seen -- an enemy never spotted the ' +
    'whole match would genuinely have zero recorded positions, not a bug in this project. If it\'s ' +
    'a teammate (someone who should always be visible to the recording player) missing instead, ' +
    'that points at an identity-resolution mismatch between player.json and movement.parquet\'s ' +
    'character GUIDs instead.');
  lines.push('Spawn-frame positions (u/v should be within 0..1 to land on the map image):');
  lines.push(['player', 'sampleCount', 'PosX', 'PosY', 'u', 'v', 'insideImage'].join('\t'));
  for (const r of rows) {
    lines.push([r.player, r.sampleCount, r.PosX, r.PosY, r.u, r.v, r.insideImage].join('\t'));
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

  drawMapImage(w, h);
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
