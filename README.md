# ValorantReplayInsights

A C# analysis and visualization layer for VALORANT replay (`.vrf`) data — player movement,
derived vision cones, ability casts, utility/smoke placement, agents, loadouts, and combat —
built **on top of** [vrfkit](https://github.com/yakisoba0728/vrfkit)'s already-decoded Parquet
output, rather than parsing `.vrf` files directly.

## Why it's split this way

Riot doesn't publish the `.vrf` format, and since around patch 12.10 the replicated-property
payload inside it is wrapped in a per-build scrambling transform whose only real purpose is to
keep third-party tools out. Reproducing that transform means reverse-engineering an
anti-tampering mechanism Riot deliberately maintains against tools exactly like this one — this
project deliberately doesn't do that, and never will.

vrfkit already does that work, openly, as an independent MIT-licensed project (it's itself a
derivative of [ValorantReplayParser](https://github.com/michel-giehl/ValorantReplayParser); see
vrfkit's own `NOTICE.md`). So the pipeline here is:

```
   .vrf file            vrfkit (Rust, external)          this project (C#)
 ──────────────►  export  ──────────────────────►  Parquet tables + manifest.json  ──────────────►  analysis / JSON
  (you run vrfkit yourself)                                                              (VrfInsights.*)
```

**This repository never opens a `.vrf` file and contains no decryption/descrambling code.**
`VrfInsights.Data` only reads the Parquet files and `manifest.json` that a `vrfkit export` run
already produced.

## Pipeline

There are three ways to run this, from least to most hand-holding:

### Option A — the GUI (easiest)

`VrfInsights.Gui` is a small Windows app that wraps everything below into point-and-click steps:
pick (or browse to) a `.vrf` file, pick an output folder, and click **Run**.

You don't need to have vrfkit built already. The first time you click **Run** (or the **Set up
vrfkit automatically** button), the app will, itself, `git clone` vrfkit's repository and run
`cargo build --release -p vrfkit --features export` — the exact two commands from vrfkit's own
README, just automated — and cache the resulting `vrfkit.exe` under
`%LocalAppData%\VrfInsights\vrfkit-src\`. Every run after that finds it instantly, with no
browsing and no rebuild. This still needs [Git](https://git-scm.com/download/win) and
[Rust](https://rustup.rs) installed once (Rust also needs the "Desktop development with C++"
workload from Visual Studio Build Tools, for its linker) — if either is missing, the app tells
you exactly which one and stops there rather than guessing. If you'd rather point it at a vrfkit
you already built yourself, the **Browse...** button next to the path box still works too.

```bash
dotnet run --project src/VrfInsights.Gui
```

**Want a real double-clickable `.exe` instead of that command (or a `.bat` wrapping it)?** Run
this once from PowerShell in the project folder:

```powershell
.\scripts\Build-Gui-Exe.ps1 -Shortcut
```

This still needs the .NET SDK installed (once, same as everything else here), but the `.exe` it
produces — `publish\gui\VrfInsights.Gui.exe` — is fully standalone (the .NET runtime is bundled
inside it), so from then on you just double-click it, or the desktop shortcut `-Shortcut` creates
for you. Re-run the script any time you pull code changes to rebuild it. If PowerShell refuses to
run it (`running scripts is disabled on this system`, or `is not digitally signed`), see the
execution-policy note under "one-time setup" in the 2D replay viewer section below.

If your machine's policy is locked down enough that even `-ExecutionPolicy Bypass` won't run a
`.ps1` at all (some managed/work laptops), skip the script entirely — this is the exact command it
runs, and typing/pasting it directly at a PowerShell prompt isn't a script file, so the execution
policy doesn't apply to it:

```powershell
dotnet publish src\VrfInsights.Gui\VrfInsights.Gui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish\gui
```

That writes `publish\gui\VrfInsights.Gui.exe`. To also pin a desktop shortcut to it without the
script, paste this too:

```powershell
$s = New-Object -ComObject WScript.Shell
$lnk = $s.CreateShortcut("$([Environment]::GetFolderPath('Desktop'))\VRF Insights.lnk")
$lnk.TargetPath = "$PWD\publish\gui\VrfInsights.Gui.exe"
$lnk.WorkingDirectory = "$PWD\publish\gui"
$lnk.Save()
```

### Option B — one command (`run`)

`vrf-insights run` wraps the same two steps (`vrfkit export` + `vrf-insights analyze`) into a
single CLI invocation, so you don't have to hop between vrfkit and this tool by hand:

```bash
dotnet run --project src/VrfInsights.Cli -- run path/to/match.vrf \
    --vrfkit path/to/vrfkit.exe \
    --out ./analysis \
    --with-vision
```

(Add `--movement-hz 0` if you specifically want full-fidelity movement data instead of the default
10 samples/sec/player thinning — see the `movement.json` note below.)

This decodes the replay into `./analysis/export/` (override with `--export-dir`) and then writes
the same analysis JSON described below into `./analysis/`.

### Option C — the two steps yourself

```bash
# 1. Decode the replay with vrfkit (once per replay) — see https://github.com/yakisoba0728/vrfkit
cargo +1.86.0 build --release -p vrfkit --features export --locked
./target/release/vrfkit export path/to/match.vrf --out ./export

# 2. Analyze vrfkit's output with this project
dotnet run --project src/VrfInsights.Cli -- analyze ./export --out ./analysis --with-vision
```

Whichever path you take, **this repository still never opens a `.vrf` file or contains any
decoding/descrambling code itself** — `run` and the GUI both just launch vrfkit.exe as an
ordinary external process (`System.Diagnostics.Process`) and then read the Parquet/JSON it
already wrote, exactly like Option C does by hand.

`analysis/` then contains:

| File | Contents |
|---|---|
| `match.json` | Replay build, duration, detected map (see below), resolved player/agent/loadout list, round boundaries |
| `events.json` | The server's own event timeline (kills, ultimates, spike plant/defuse/explode, round starts), attributed to a round number |
| `movement.json` | Per-player position/rotation/velocity time series (from `movement.parquet`), thinned to at most `--movement-hz` samples/sec/player for output (default 10 — see note below) |
| `vision_cones.json` | *(only with `--with-vision`)* one derived vision cone per movement sample per player |
| `utility.json` | Smoke/wall/molly/trap/etc. placement events with spawn/despawn time and position |
| `ability_casts.json` | Ability casts from `Comp_AbilityStatisticsReplicator.AbilityCastsThisRound` (caster, slot, round, location) |
| `ultimate_usages.json` | Ultimate-cast signal from the server's own event timeline |
| `combat_interactions.json` | Per-round `CombatReport` interactions (damage, hits, kill/assist, wallbang) |
| `economy.json` | `MoneyManagementComponent` credit snapshots over time |
| `hits.json` | Per-landed-hit events (time, impact point/direction, attacker/victim, weapon) from `MulticastNotifyDamage_Point` RPCs — powers the minimap's hit tracer and the ticker's weapon display; **new, and — like every file below it — not yet confirmed against a real export** — see "Hit tracers, weapon, and armor" below |
| `armor_purchases.json` | Armor item actors (Heavy/Light/"Plasma") opening a channel, resolved to their owning player — powers the ticker's armor display — see "Hit tracers, weapon, and armor" below |
| `shots.json` | Per-shot events (time, firing direction, ammo) from `ReplayPlayContinuousEffectAtLocation` RPCs — a secondary, independent attempt at the same tracer animation `hits.json` now provides, built first and kept in case a future fix gets it working too — see "Shot tracers (secondary/experimental)" below |

**About `movement.json`'s size.** `movement.parquet` replicates at close to the replay's own tick
rate (observed up to roughly 128 samples/sec per player) — for a full match, writing every sample
straight to JSON can produce a file hundreds of MB, too large for a browser to load in the 2D
replay viewer (`FileReader`/`JSON.parse` failing on a huge string surfaces as "Unexpected end of
JSON input", which looks like a truncated/corrupt file but isn't — the file is valid, just too
big). So `analyze`/`run` thin `movement.json` (and, with `--with-vision`, `vision_cones.json`) down
to at most `--movement-hz` samples/sec per player before writing — by minimum time spacing, always
keeping each player's first and last sample, so positions stay accurate, just less densely sampled.
Default is 10/sec, which is still smooth once the viewer interpolates between samples. Pass
`--movement-hz 0` for full, undownsampled fidelity if you specifically need every raw sample (the
GUI always uses the default of 10). If you already have an old, oversized `movement.json` from
before this existed, delete it and re-run `analyze`/`run`.

Four diagnostic commands help you verify or extend the assumptions below against your own export:

```bash
dotnet run --project src/VrfInsights.Cli -- dump-fields ./export --group Comp_AbilityStatisticsReplicator
dotnet run --project src/VrfInsights.Cli -- dump-values ./export --field CastLocation
dotnet run --project src/VrfInsights.Cli -- dump-classes ./export
dotnet run --project src/VrfInsights.Cli -- dump-actors ./export --class Smoke
```

`dump-fields` shows you the real `field_name`s (this already caught one wrong assumption —
`CastLocation` doesn't flatten into `.X`/`.Y`/`.Z` children after all, it's fixed now, see below).
`dump-values` goes a level deeper once you have a real name in hand: it prints the actual per-row
values (every `Value*` column, plus a hex preview of `RawBits`) for fields matching a substring,
so you can see *how* a field is encoded instead of guessing from its name. `dump-classes`/
`dump-actors` are the equivalent pair for `actors.parquet`: `dump-classes` lists the class names
that exist, `dump-actors` shows the actual open/close/spawn-position rows for a class, so you can
see whether an actor is reused across the match or fresh per use.

A fifth one, specifically for checking `shots.json` (the newest, least-confirmed output — see
above): `dotnet run --project src/VrfInsights.Cli -- dump-values ./export --field
ReplayPlayContinuousEffectAtLocation --limit 5` prints the raw decoded `value_str` JSON for a
handful of shots, so you can see the actual `{"tag":N,"value":...}` shape `ShotFiredBuilder`
assumes (see its doc comment) instead of taking it on faith.

## 2D replay viewer

`viewer/index.html` plays a whole match back on the real minimap: agents moving smoothly between
their recorded positions, utility/abilities appearing and disappearing where they were used, a
scrubber with one chapter per round, and play/pause/rewind/fast-forward/speed controls. It's a
plain local HTML/CSS/JS page — no server, no build step, nothing sent anywhere — that reads the
JSON files above straight off your disk in the browser.

**One-time setup — get the map/agent art.** This project's own sandbox can't reach
valorant-api.com (its network policy blocks it), so a small script does that part on your machine
instead, which has normal internet access:

```powershell
.\scripts\Fetch-Assets.ps1
```

**If PowerShell refuses with `running scripts is disabled on this system`:** this is Windows'
default script execution policy, not anything specific to this project — it blocks every `.ps1`
(this one, `Build-Gui-Exe.ps1` below, any other). Easiest fix, just for this one run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Fetch-Assets.ps1
```

Or, to stop it asking every time (once, per Windows user account):

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

(then answer `Y` when it asks to confirm). If you downloaded this project as a `.zip`, Windows
sometimes also flags the extracted files as "from the internet" and blocks them a second, different
way — if the above doesn't fix it, right-click the `.ps1` file → **Properties** → **Unblock** (or
run `Unblock-File .\scripts\Fetch-Assets.ps1`), then try again.

This downloads every competitive map's minimap image, every agent's icon, and every weapon's real
name/icon/shop cost from Riot's own public content API into `assets/maps/`, `assets/agents/`,
`assets/weapons/`, plus `assets/catalog.json` / `assets/catalog.js` (metadata the viewer reads —
see the script's own comments for why there are two copies). Safe to re-run any time;
already-downloaded files are skipped. If you'd rather not run a script against a third-party API
yourself, tell me and I'll take the images as an upload instead — either way, nothing about the
vrfkit/decoding side of this project is affected, this is purely artwork.

The weapon catalog (`assets/weapons/`, and `catalog.weapons` in `catalog.js`) is fetched ahead of
actually being used anywhere in the viewer yet — see the ticker section below for why a player's
currently-equipped weapon isn't shown yet even though the real names/icons/costs are now sitting
right there in the catalog.

**Using it:** open `viewer/index.html` in a browser, select every file from one `analyze`/`run`
output folder in the file picker (`match.json` and `movement.json` are required, the rest add
detail), and it starts playing. If the map couldn't be auto-detected from the replay, pick it from
the dropdown that appears.

What it draws, and how honestly-approximate each part is:

| On screen | Source | How exact |
|---|---|---|
| Player position, movement | `movement.json`, linearly interpolated between samples | Exact positions; smooth motion is this viewer's own interpolation, not extra recorded data |
| Facing direction | `movement.json`'s yaw | Exact yaw value; the on-screen rotation direction is a documented, adjustable assumption — see below |
| Agent icon / map image | `assets/` (from `Fetch-Assets.ps1`) | Riot's own official art |
| Minimap placement | Riot's published per-map `xMultiplier`/`yMultiplier`/`xScalarToAdd`/`yScalarToAdd` | Riot's own documented formula, not independently pixel-checked here |
| Smoke/molly circle size, wall line length | Fixed constants in `app.js` | Visual approximation — the replay only gives a spawn point (and, for a wall, a spawn yaw), never a size |
| Ability-cast flash | `ability_casts.json`'s `FirstObservedAtMs` | Approximate timing, see the CastTime caveat above |
| Vision cones (optional toggle) | `vision_cones.json`, if you ran `analyze --with-vision` | Same derived approximation described below |
| Player/roster color (attack = red, defense = green) | `match.json`'s `Sides` (`TeamSideResolver.cs`) | Derived from two independently-verified signals, not guessed — see "Team colors" below. Falls back to one arbitrary color per player if it couldn't be determined for a given match |
| Death marker (X where a player died) | `events.json`'s `characterDeath` rows | Exact death time and NetGUID (vrfkit-verified); the marker position is this viewer's own interpolation of `movement.json` at that timestamp, same as live positions |

If a facing arrow looks rotated or mirrored on a particular map, use the **Facing offset°**
control in the viewer rather than assuming the position data itself is wrong — that's the one
piece of this viewer's math (screen rotation direction for a given yaw) that hasn't been checked
against a real recording, and it's deliberately a live control instead of a silent guess.

If everything is on the wrong side of the map — e.g. the two teams show up on the left/right when
the map actually has spawns on the top/bottom — that's the minimap coordinate transform disagreeing
with how that map's downloaded art happens to be oriented, not the underlying position data being
wrong. Three ways this gets fixed, in the order the viewer itself tries them:

1. **Automatic calibration, no screenshot or in-game access needed.** The first time you load a
   map that isn't already covered by #2 below, the viewer tries all 4 rotations × flip/no-flip
   itself: every downloaded competitive-map image has its four corners fully transparent (only the
   actual playable shape is opaque), so a real recorded position can only ever be correct sitting
   on an opaque pixel — never one of those corners. For each of the 8 rotate/flip candidates, it
   also fits its own best-guess **scale + recentering pan** from the recorded footprint's own
   spread before scoring it — rotating and flipping alone can never fix a match whose whole raw
   footprint simply doesn't fit inside the image square yet (rotation/flip preserve every point's
   distance from the center exactly, so if a point is already too far out, no amount of turning it
   around the center brings it back in — only zooming/panning can), which real data from Ascent
   showed is often the actual problem, not the rotation. It picks whichever fitted candidate lands
   almost all points on opaque pixels, *only* when one candidate clearly, confidently wins (see
   `AUTO_ORIENTATION_MIN_SCORE`/`_MIN_LEAD` in `viewer/app.js` for the exact bar) — otherwise it
   leaves the manual controls alone rather than force a low-confidence guess. This runs
   automatically; there's nothing to click. The **Debug info** panel's "Automatic orientation
   calibration" line always says what happened — applied (with the fitted rotation, flip, scale,
   pan, and the winning/runner-up scores), inconclusive (and why), or not run (already covered by
   #2, or already has a saved value) — so it's never a silent black box.

   **This needs `scripts/Fetch-Assets.ps1` to have been run *after* this feature was added to know
   which parts of a map's image are real map vs. transparent padding.** Reading a map image's own
   pixels back out of a canvas — which is how it would otherwise tell — is something browsers
   flatly refuse to do for `viewer/index.html` opened the normal way (a plain local file, per the
   "Running the viewer" section above); there's no in-browser setting or trick that changes this,
   since it's the same restriction that keeps a malicious local page from reading other files on
   your disk. So instead, `Fetch-Assets.ps1` now precomputes each map's opaque/transparent shape
   itself (via .NET's `System.Drawing`, on your machine, once) and bakes it into `catalog.js`
   alongside everything else it already downloads — the viewer just reads that, no canvas access
   needed. If the Debug info panel says calibration is "inconclusive -- this browser won't allow
   reading the map image's pixels back", that's this: re-run `Fetch-Assets.ps1` (no `-Force`
   needed — it doesn't need to re-download any images, just regenerate `catalog.js` with the added
   data) and reload the page.

   **A map's rotation alone can also be pinned from real per-match calibration scores**
   (`CONFIRMED_ORIENTATIONS` in `viewer/app.js`, currently empty — see below) without hand-fixing
   scale/pan too. Real official minimap art turns out to have plenty of transparent void *inside*
   its outer shape too (walls, out-of-bounds interior gaps), not just the four corners this whole
   feature leans on — so even the genuinely correct rotation for a map can land well under
   `AUTO_ORIENTATION_MIN_SCORE` (this is exactly what happened with Ascent below, before its
   scale/pan were also hand-confirmed and it moved to #2's table instead). Rather than loosen that
   bar for every map — which would risk a false-confident pick on some future map with no real data
   behind it — a map's rotation can be pinned here once its own calibration scores clearly settle
   it, the same way `KNOWN_MAP_ORIENTATIONS` pins a fully-confirmed one below. The difference: a
   `CONFIRMED_ORIENTATIONS` entry still gets scale/pan fit fresh from every replay's own recorded
   footprint (via the same per-candidate fit as #1), it just skips re-deciding *which* rotation to
   use — useful for a map whose rotation is settled but hasn't had its scale/pan separately dialed
   in and fixed yet.
2. A handful of maps have a **complete orientation hand-confirmed and built in** — rotation, scale,
   AND pan all fixed constants (see the table below) — which take priority over #1 and are never
   recomputed or auto-fit; this is the most reliable option once a map's numbers are dialed in,
   since it doesn't depend on this browser's `localStorage`, or on any particular replay's own
   recorded footprint, at all.
3. For anything #1 couldn't confidently resolve, or if you just want to override it, use the **Map
   orientation** control (rotate 0/90/180/270°, plus a flip checkbox, plus the Scale/Pan sliders
   described further down) next to the facing offset to correct it by eye. It's per-map and
   remembered in your browser (`localStorage`) once set — by you *or* by #1 — so it only ever needs
   setting once per map, and a **Reset map fit** button clears that saved value, re-runs automatic
   calibration immediately (no reload needed), and lands the controls on whatever #1 or #2 comes up
   with (or 0°/no flip/1×/no pan if neither found anything).

   If you were on an earlier build and a map still looks wrong after clicking **Reset map fit**,
   that's expected the first time: a manually-chosen rotation saved from before this fix existed
   used to make Reset just re-save the same fallback value, which silently prevented automatic
   calibration from ever running again for that map. Reset now actually clears the saved value
   first, so a single click gets you the improved auto-fit.

**Maps with a complete hand-confirmed orientation built in** (`KNOWN_MAP_ORIENTATIONS` in
`viewer/app.js`) — rotation, scale, AND pan all fixed as constants, so they're identical on every
browser/machine with no dependence on `localStorage` or on a given replay's own footprint:

| Map | Rotation | Scale | Pan X / Y | Confirmed by |
|---|---|---|---|---|
| Sunset | 90°, flipped | 1.01 | 1.0% / 2.0% | Rotation matched against a player's own room-by-room read of a live match, then separately checked against 8 of 10 real player positions from a screenshot (see below); scale/pan hand-dialed in afterward |
| Ascent | 90°, flipped | 0.96 | 36.9% / 38.5% | Rotation confirmed from a real 24-round match's own calibration scores (64%, a clear ~16-point margin over every other candidate — see the Debug info panel's score breakdown); scale/pan hand-dialed in afterward |

**Maps with only a rotation confirmed from real calibration scores** (`CONFIRMED_ORIENTATIONS` in
`viewer/app.js`, currently empty) — see #1's note above for what this is and how it differs from the
table just above (scale/pan still gets fit fresh per replay here, only the rotation is pinned; a map
moves from this table to the one above once its scale/pan are also hand-confirmed and fixed).

**Maps where the downloaded picture itself needed rotating** (`MAP_IMAGE_ROTATIONS` in
`viewer/app.js`) — a different, independent correction for when the minimap art as downloaded is
just sideways/upside-down, rather than the coordinate formula disagreeing with an otherwise-correct
picture; this one physically turns the drawn image and leaves dot positions using the plain,
uncorrected formula:

*(none confirmed yet — every map that's needed a fix so far turned out to be a dot-position/scale/pan
issue instead, handled by the tables above. Any map without an entry in any of these tables goes
through plain automatic calibration (#1 above), which needs no screenshot or guess at all — check
the Debug info panel's "Automatic orientation calibration" line after loading it.)*

If you work out a good rotation for another map, add it to whichever of these tables in `app.js`
actually fixed it (one line) so nobody has to rediscover it — see "How to verify a map's rotation"
below. If you're not sure which one a given map needs, try the dot-position **Map
orientation** control in the UI first (it's instant, no code change) — if THAT alone lines
everything up, it belongs in `KNOWN_MAP_ORIENTATIONS`; if the dots land right but the picture
itself looks sideways (walls/rooms rotated relative to what the dots are doing), it belongs in
`MAP_IMAGE_ROTATIONS` instead.

If positions are pointed in the right general direction but still look a little off-center —
clipping into walls that aren't near the real spawn, or drifting slightly away from the correct
rooms — that's a separate problem from rotation: the downloaded map image's content might not fill
the exact same square Riot's coordinate formula was calibrated against (valorant-api.com's
`displayIcon` is a separate asset, not necessarily pixel-identical to whatever the game client
renders internally). The **Scale** and **Pan X%/Y%** controls next to Map orientation compensate
for that — Scale zooms in/out around the image center, the two Pan fields shift it — also
remembered per map. For a map covered by #1 above this is normally already fit automatically (it's
what the "scale, pan" part of automatic calibration is doing), so manual Scale/Pan is mainly for
fine-tuning after that, or for a `KNOWN_MAP_ORIENTATIONS` map (#2), where it's still a small,
by-eye correction — a few percent, not the main source of misalignment. Whatever the size of the
correction turns out to be, get rotation right first, since scale/pan are fit (or should be
adjusted) around whatever rotation is already selected. The Pan sliders move in tenths of a percent
(not whole percent), since a full 1% step moved players much further than intended for a
fine-tuning control.

**How to verify a map's rotation**, if you want more confidence than "the two teams are on the
right side": take a screenshot of the in-game minimap at a moment where you can positively identify
several players (ideally ones standing still or moving slowly — a sprinting player's position
shifts fast enough that being even half a second off between your screenshot and the replay
viewer's scrubber shows up as a real difference), then eyeball their positions in the viewer at that
same moment against your screenshot. If most of them land in the right rooms, the rotation is good
enough — don't chase exact pixel matches for every player, particularly ones who were moving fast
at that instant.

There used to be a "click points on the map, fit a custom transform" calibration feature here for
squeezing out more precision than plain rotation. It's been removed: fitting a transform from
hand-picked points turned out to be fragile in practice (one slightly-off or fast-moving point could
visibly distort the whole fit, in a way that was hard to diagnose after the fact), and it wasn't
buying meaningfully better accuracy than just getting the rotation right and living with the small
per-map centering error the Scale/Pan sliders address. If a map's built-in rotation (or one you set
by eye) isn't good enough for what you're doing, that's a sign the downloaded map image itself may
be the wrong one — worth re-running `Fetch-Assets.ps1 -Force` before trying to compensate further.

If you're not sure the transform itself is right (positions clipping into walls, or way off the
map entirely), open the **Debug info** panel below the roster after loading a match — it lists
every player's first recorded position and the exact normalized coordinate the map transform
computes from it, with a "Copy debug info" button so you can hand that straight to whoever's
troubleshooting it, no developer tools required. It also now says whether team sides (below) were
resolved for that particular replay, and lists every player's movement-sample count — including
0 for a player who never had a single position recorded at all (as opposed to one whose position
is merely off-map), which is the signature of only seeing one team on the map: see the panel's own
explanation of why that happens (in short — the norm for a .vrf recorded from a player's own
client, not a bug — the recording player's client only ever received position updates for enemies
it had actually seen).

The same panel also lists every entry in `state.utility` (from `utility.json`) — class path,
category, world/normalized position, spawn/despawn time, and whether it's active at the current
scrubber position — sorted by distance to the nearest player's spawn point, closest first. This
was added specifically to chase down "utility marker stuck at spawn" reports without another round
of CLI `dump-*` commands: a marker that's still misclassified as a real ability effect (the same
shape as the `AggroBot_PC` and `Ability_*` container bugs described further down, under "Also
fixed..."/"A second, separate cause...") reliably sorts to the top of this list, sitting at ~0
distance from a spawn point with `SpawnTimeMs` near 0 and `DespawnTimeMs` that never arrives.

### Team colors (attack = red, defense = green)

Players are colored by side rather than one arbitrary color each. There's no single field
anywhere in vrfkit's tables that just says "this player is attacking this round" (checked against
vrfkit's own `docs/DATA.md` — nothing marked verified there covers per-player side), so rather than
inventing a heuristic and hoping — the mistake the old map-calibration feature made — this combines
two things that vrfkit's own docs *do* independently verify:

1. **Team roster** (which players are on the same team) never changes during a match, only which
   side of the map — and therefore attack/defense role — a team plays, which flips at
   halftime/overtime (`events.switchTeams`, vrfkit-verified). The two teams' round-1 spawn points
   are always in the two separate spawn rooms at opposite ends of the map, so grouping players by
   which of two far-apart spawn clusters they're in reliably recovers the roster split without any
   per-map calibration data.
2. **Spike custody** (`BombEquippable_C.Owner`, the same signal vrfkit's own
   `tools/extract_spike_carrier.py` uses to find the planter) is direct evidence of who held the
   bomb — and only attackers can ever hold it. One resolved pickup during a round tells you that
   player's whole roster group was attacking that round; that's then applied to every round in the
   same half, since sides only change at `switchTeams`.

This is implemented in `TeamSideResolver.cs` (`VrfInsights.Analysis/Rounds/`) and covered by unit
tests in `TeamSideResolverTests.cs` using synthetic fixtures (this sandbox has no real `.vrf` or
exported Parquet sample to validate against — see "Honesty about what's verified vs. assumed"
below). If a replay doesn't have enough evidence for either signal (very little of the match
decoded, or the bomb was never picked up), `match.json`'s `Sides` array comes back empty and the
viewer quietly falls back to one arbitrary color per player instead of guessing sides — the
**Debug info** panel says which happened for a given replay.

### Death markers

When a player dies, their live icon disappears and a team-colored **X** appears at the exact spot
they died, for the rest of that round — scrub backward past their death and they reappear alive,
same as before. This reads `events.json`'s `characterDeath` rows, which vrfkit's docs confirm carry
the killed player's *character pawn* NetGUID — exactly the same ID `movement.json` is already keyed
by, so no extra identity resolution was needed. `events.json` is optional in the file picker (older
output folders that predate this feature won't have it); without it, players simply don't disappear
on death, same as before this feature existed.

### Round timing (freeze time)

vrfkit's `roundStarted` event — and so `RoundInfo.StartTimeMs`, from the C# side's
`RoundTimelineBuilder` — fires at the *start* of freeze time (the buy phase), not the moment
players are actually free to move. The viewer accounts for this: `playableStartMs()` in
`viewer/app.js` adds a freeze-time length on top of that raw timestamp — 45s for the first round of
each regulation half, 30s otherwise, per VALORANT's standard rules — and that's what "Round N"
navigation (the round chapters, restart/next-round buttons, and the "Round N · 0:00" readout next
to the scrubber) is actually anchored to, not the raw `roundStarted` moment. Scrub to a time still
inside freeze time and the readout shows a countdown (e.g. `freeze 0:12 left`) instead of a
round-elapsed time, so it's visually obvious you're still in the buy phase.

**`RoundInfo.RoundNumber` is 0-indexed**, not 1-indexed: it comes straight from the `roundStarted`
event's raw `Word0`, and a real replay showed "Round 0" as its first round before this was
accounted for — VALORANT itself never numbers a round starting at 0, so the raw value needs a `+1`
shift purely for display. Concretely, the match's first round is raw round `0` (not `1`), and the
first round of the second half is raw round `12` (not `13`) — those are the two rounds
`freezeTimeMsForRound()` gives the 45s treatment to. Every internal lookup/join in `viewer/app.js`
(and on the C# side) keys off the raw, unshifted number the whole way through; `roundNumberForDisplay()`
in `viewer/app.js` (`rawRoundNumber + 1`) is the *only* place that shift happens, applied right at
the handful of spots that put a round number on screen (chapter button labels/titles, the
`roundLabel` readout) — so "Round 1" is what you'll actually see for the match's first round.

This is **not confirmed for overtime rounds** (raw 24+, displayed as 25+) — only the first round of
each regulation half was specified, so an OT round is treated as a normal 30s round until someone
confirms otherwise from a real OT replay.

The round chapters bar reflects the same split: each round is two adjacent buttons now, not one —
a small, dim, unlabeled freeze-time segment (dashed border) sized to its actual 30s/45s length,
followed by the labeled `R<N>` round-play segment. Clicking the freeze segment jumps to the raw
buy-phase start; clicking the round segment jumps to `playableStartMs()`. Only whichever of the
two segments the playhead is actually in lights up as active, so the bar doubles as a "still in
the buy phase?" indicator at a glance.

The scrubber itself (`#scrubber` in `viewer/style.css`) is a custom-styled 20px track with a
28×38px thumb — much thicker than a native range input's ~4px default — plus `step="1"` (was
`"10"`) for finer keyboard-arrow nudging, both aimed at making it easier to land on an exact
moment over a long match.

### Real ability icons

Utility markers can show the actual VALORANT ability icon (from valorant-api.com) instead of —
or, for area effects, on top of — the plain colored shapes in `UTILITY_COLORS`. Run
`scripts/Fetch-Assets.ps1` (it now also downloads every agent's per-ability icons, into
`assets/abilities/<agent-uuid>/<slot>.png`) and reload the viewer to pick this up.

**Why this can't just use the internal class-path slot** (the `4`/`Q`/`E`/`X`/`C` in e.g.
`Ability_Wraith_4_Smoke`): it doesn't reliably correspond to valorant-api's own ability1/ability2/
grenade/ultimate slots. Confirmed directly: Omen's smoke is class-path slot `"4"`, but on
valorant-api it's his `Grenade`-slot ability (Dark Cover, his signature). So matching by slot
*number* would need per-agent, per-slot verification this project doesn't have for most of the 29
agents (this is the same "positional guessing is unreliable" finding as `docs/AGENT_ABILITIES.md`'s
own note on ability-level codenames).

**What it does instead: match on text.** The C# side now resolves, per utility event:
- `AgentRealName` (`AgentCodenames.ResolveRealName`) — the casting agent's real name, or `null`
  for something not tied to any agent (a map-wide pickup like `UltPointOrb`).
- `DescriptiveKeyword` (`UtilityEffectClassifier.ExtractDescriptiveKeyword`) — the human-readable
  fragment of the class name, with the actor-type prefix, the agent's codename, and any leftover
  numeric/single-character slot marker stripped out (e.g. `Zone_Wraith_4_Smoke` → `"Smoke"`,
  `Ability_Q_Aggrobot_SeekerNade` → `"SeekerNade"`).

The viewer (`resolveUtilityAbilityMatch` in `viewer/app.js`) then scores `DescriptiveKeyword`
against each of that agent's real abilities' `displayName` + `description` text (from the fetched
catalog), using a small stopword-filtered shared-word-root count (`scoreAbilityMatch`) — words
like "seeking"/"SeekerNade" or "explodes"/"ExplodeyPatch" share a root even though they're not the
same word. **A marker only gets an icon when exactly one ability scores strictly higher than every
other candidate for that agent** — a tie means genuine ambiguity from this text alone (confirmed:
Gekko's `Ability_Aggrobot_C_ExplodeyPatch` and `Ability_Aggrobot_X_RollyExplosion` both share an
"explod-" root with more than one of his 4 real ability descriptions), and the marker keeps its
plain colored shape rather than risk showing the wrong ability's icon.

**Verified against every agent this project has real ability text for** (fetched from
valorant-api.com this session): KAY/O's flash, Chamber's trap, Gekko's Wingman (and its correct
refusal to guess on the Explodey/Rolly tie above), and Phoenix's wall. Every other agent runs the
exact same generic algorithm — it is *not* hardcoded per agent — but is otherwise unverified until
checked against your own export; the viewer's Debug info panel now includes an `IconMatch` column
per utility marker (which ability it resolved to and its match score, or that it fell back to a
colored shape) specifically so you can check this yourself rather than take it on faith.

### Viper's wall: a special case, not an icon

Every other Wall-category ability (Sage's Barrier Orb, KAY/O's Zero/Point, etc.) uses the generic
handling above: a fixed-length purple line for orientation, plus the real ability icon at its
midpoint once one resolves, faded in/out on the actor's own real spawn/despawn window. **Viper's
Toxic Screen deliberately does none of that** — no icon, a distinct yellow-green "toxic gas" line
(not the map's own attack/red-vs-defense/teal-green legend colors, so it doesn't read as team-side
coloring), a longer line, and a different visibility rule: shown from the moment it's cast until
the **end of that round** (found via `match.json`'s `Rounds`), regardless of the RPC's own real
despawn/toggle time.

That last part is a deliberate stylization, requested for readability rather than accuracy: Toxic
Screen actually toggles on/off multiple times off a shared fuel meter within a round, and this
project doesn't attempt to track that live on/off state — the round-long line just marks "a wall
went up somewhere around here this round," which is more useful for reviewing a round's setups
than a line that flickers with the gas's exact live state (and matches how a player mentally tracks
it — "Viper walled here this round" — more than a strictly accurate on/off animation would).

**Why it didn't show up at all at first, and the fix.** Viper is the one agent in this whole
project that has **never actually been seen in a real export** (see "About the ability-level
codenames" in `docs/AGENT_ABILITIES.md`) — every other agent-specific piece of code here was at
least checked against one real class name for that agent; Viper's wasn't. The classifier only
matched `"Wall"`/`"Barrier"` for the whole `Wall` category, and `UtilityTimelineBuilder` silently
drops anything that classification doesn't recognize (`Category == Unclassified`) before it ever
reaches `utility.json` — so if Toxic Screen's real internal class name doesn't happen to contain
either word, nothing was ever emitted for it to draw in the first place, independent of anything
the viewer does. Fixed defensively, not confirmed: `UtilityEffectClassifier.Keywords` now also
matches `"Toxic"`/`"Poison"` (ordered after `"Cloud"`, so Poison Cloud still classifies as `Smoke`
rather than `Wall`) — a bet that Riot's internal name uses the ability's real English name the way
`"Molly"`/`"Flash"`/etc. already do for other agents, not something checked via `dump-classes`
against a real Viper cast. **If it still doesn't show up**, run
`dotnet run --project src/VrfInsights.Cli -- dump-classes ./export` against a real export that has
Viper in it and search the output for her dev codename (`Pandemic` — see `AgentCodenames.cs`); once
you have her wall's actual class name, add whatever word it actually uses to that `Keywords` array
(it's a plain, freely-editable list) instead of guessing further.

**The line's length.** Riot doesn't publish this, but community-tested numbers (see
[wiki.playvalorant.com's Toxic Screen page](https://wiki.playvalorant.com/en-us/Toxic_Screen),
"manually tested to be very accurate") put its maximum length at 60 meters. This project's
coordinate system is already confirmed to be centimeters, matching Unreal's own default
(`VisionConeCalculator.EyeHeightCm` is 155 — a real human eye height in cm — and its
`DefaultRangeCm` is 18,000, i.e. 180m, a plausible sight-line distance), so 60m becomes 6,000 units
— `VIPER_WALL_HALF_LENGTH_UNITS` in `viewer/app.js` is half that. Two things this still can't
account for without more data: the wall is drawn at this maximum length on every cast, when in-game
it's often shorter because it stops at the first piece of map geometry it hits (no collision data
available to truncate against); and the line's *direction* is the wall actor's own
`actors.parquet` `spawn_yaw` (`YawDegrees`) — plausibly Viper's aim direction at cast time, the same
way the real ability works, but — same as the class-name guess above — not independently confirmed
for this specific actor, since Viper has never been checked against a real export.

Matched by `AgentRealName === 'Viper'` on a `Wall`-category `utility.json` entry — same
agent-attribution confidence as everywhere else `AgentRealName` is used (see above), no separate
verification done for Viper's wall specifically.

### The ticker (right-hand panel)

The right side of the viewer shows a live per-player panel next to the minimap: agent icon,
current money, live K/D/A, that agent's three non-ultimate abilities with real icons, and an
overall match score header — attacking side (red) on the left column, defending side (green) on
the right, matching the same colors the minimap dots already use. Everything updates live as you
move the scrubber. No re-run of the analyzer is needed to get this — `economy.json` and
`combat_interactions.json` were already being produced by `analyze`/`run`, just not read by the
viewer before now; reload `viewer/index.html` and reselect the same output folder's files.

**What's exact, not estimated:**
- **Money** — `economy.json`'s `Money` field, a plain scalar (`MoneyManagementComponent.Money`
  per vrfkit's docs), read at whatever time the scrubber is currently at.
- **Kills/Assists** — `combat_interactions.json`, which vrfkit's own README documents as "the sole
  source of K/D/A ... reports as multiset-identical against the existing C# reference parser".
  **A real bug here (kills always reading 0) was found and fixed via code review** — see "Honesty
  about what's verified vs. assumed" below, "Fixed via code review, NOT yet confirmed" — but,
  unlike the other "exact" fields on this list, it hasn't yet been checked against a real export,
  so re-run `analyze`/`run` and confirm kills actually show up now.
- **Deaths** — `events.json`'s `characterDeath` rows, the same signal the minimap's death markers
  already use.

**What's a labeled best-effort estimate, not something read directly off the replay** (this was a
deliberate scope decision — see the conversation this shipped in — rather than an oversight):
- **Which named ability a cast belongs to.** `ability_casts.json` records a raw numeric `Slot` per
  cast, but neither vrfkit's docs nor this project's own diagnostics confirm what that number maps
  to for a given agent (see `AbilityCastEvent`'s doc comment). The viewer first tries to *learn*
  the mapping from real evidence in your own replay — correlating a cast's timestamp against a
  confidently text-matched utility placement (the same matching "Real ability icons" above already
  does for the minimap) — and only falls back to a conventional grenade/Q/E ordering for a slot
  number nothing could be correlated to. A dashed icon border means "estimated"; a solid border
  means "learned from this replay's own evidence". Hover any ability icon to see which, and why.
- **Charge/cooldown state.** Simulated from `viewer/ability-meta.js` — credit cost, max charges,
  and any regen rule (kill-based or time-based), researched per agent against
  valorant.fandom.com / wiki.playvalorant.com / liquipedia.net as of 2026-09 and cross-checked
  across sources. VALORANT rebalances abilities often and a few agents' kits don't fit a simple
  per-slot-credit-cost shape at all (Astra's Star pool, Reyna's Soul-Orb-gated Devour/Dismiss,
  Chamber's ammo-based Headhunter, Gekko's pick-up-to-reuse creatures) — see that file's own
  comments and `flag`/`special` fields for exactly what's simplified where.
- **Match score.** A round's winner is read from `spikeDefused`/`spikeExploded` events where
  present, or a full-team elimination otherwise, or "time expired with no plant" as a last-resort
  default — a round this can't resolve any of those ways shows as unresolved and isn't counted
  toward either team's total rather than being guessed. The two numbers track the same two rosters
  all match (so a team's total is continuous across the halftime side-swap), even though which
  color (red/green) each one currently sits under does swap at halftime along with the dots.

**Weapon and armor, best-effort:** the ticker shows a weapon icon/name and 🛡 armor tier per player,
both sourced from `hits.json`/`armor_purchases.json` — see "Hit tracers, weapon and armor" below
for exactly what these can and can't tell you. The weapon display specifically is "what they were
last confirmed holding, and how long ago" (from a landed hit), not a continuous "what they're
holding right now" — there's no signal for that between hits, so treat a weapon shown as several
seconds/rounds stale (dimmed and italicized in the UI) with appropriate skepticism. The icon itself
comes from `assets/catalog.js` (`scripts/Fetch-Assets.ps1`'s own weapons fetch, matched to the
resolved weapon by real name) — run that script first, or the ticker falls back to a 🔫 emoji plus
the plain weapon name.

### Hit tracers, weapon, and armor

Each recent hit draws a brief effect on the minimap: a tracer line from the attacker to the exact
point the hit landed (when this project could resolve who the attacker was), or just a small flash
at the impact point alone (when it couldn't — still shows *that* and *where* a hit landed). This
also drives the ticker's weapon and armor display above.

**History**: this project first tried building the same animation from `shots.json` (a "shot
fired" signal), which turned out not to produce visible output for at least one real replay. A
second research pass into vrfkit's own source found a much better-evidenced alternative —
`MulticastNotifyDamage_Point`, a per-landed-hit RPC vrfkit's own docs mark ✅ (its highest
confidence tier) — and this is now the primary source for the tracer animation; `shots.json` is
kept as a secondary, independent attempt (see its own section below) in case a future fix gets it
working too, but nothing here depends on it.

**What's confirmed by reading vrfkit's own source directly** (`crates/vrf-decode/src/table.rs`'s
field table for `MulticastNotifyDamage_Point`, `crates/vrfkit/src/sink/rpc.rs`'s field-naming code,
`docs/DATA.md`) — not yet independently checked against a real decoded export by this project, but
a noticeably higher-confidence starting point than the shot-effect approach was:
- The RPC lives on `DamageableComponent` (the health-tracking component on the character *taking*
  damage), with `FieldName` built as `"MulticastNotifyDamage_Point.<ParamName>"` and **no** vrfkit
  disambiguation suffix (confirmed by reading the field-naming code directly — unlike
  `AbilityCastsThisRound`'s members, which do carry one).
- It carries a real `DamageImpactLocation` (where the hit landed), `DamageDirection`, several
  attacker-reference fields (`DamageCauser`, `EventInstigator`, `EventInstigatorPawn`,
  `DamagerPlayerState`, `KillCreditPlayerState`), a `bDamageKilledTarget` flag, and an
  `EquippableUsed` reference that resolves — via `actors.parquet`'s `class_path`, no
  `net_guids.parquet` outer-chain walk needed for this one — to the actual weapon actor.
- **The weapon name table (`Weapons/weapons.json`) is vrfkit's own**, not guessed or
  reverse-engineered here: it mirrors vrfkit's `tools/equippable_table.py`, which vrfkit itself
  generates from a companion C# replay parser's hand-maintained resolver. Cross-checked during
  research against valorant-api.com's own `assetPath` field for several weapons (e.g. Vandal's
  `assetPath` folder is `.../Rifles/AK/...`, matching the table's `AssaultRifle_AK` codename).

**What's a strongly-evidenced inference, not a verbatim vrfkit statement:** that `ActorNetGuid` on
these rows is the *victim* — inferred by analogy with vrfkit's own healing-observation tooling,
which explicitly labels the equivalent field "recipient" for the sibling heal RPC (same component,
same mechanism), not something vrfkit's docs state in so many words for the damage RPC specifically.

**What's a genuinely unresolved ambiguity:** which of the five attacker-reference fields most
reliably identifies the attacker. `DamageHitBuilder` tries them in a preference order
(`DamagerPlayerState`/`KillCreditPlayerState` first, as direct PlayerState-style references; then
`EventInstigatorPawn`/`DamageCauser` against known character pawns; `EventInstigator` last, since
vrfkit's own docs flag it as "an opaque packed reference candidate: its target type has not been
established") and keeps every candidate on the event (`AttackerCandidates` in `hits.json`) so a
wrong pick can be diagnosed from the JSON itself without re-running the analyzer.

**What's a documented assumption by analogy:** that `DamageImpactLocation`/`DamageDirection`
decode to the same bare `"(X,Y,Z)"` string format already confirmed for `CastLocation` — plausible,
not independently checked for these two fields specifically.

**What this deliberately does NOT attempt:** live remaining-armor tracking as it absorbs damage
(would need decoding this same RPC's raw `LifeChangeEvents[]` array, which `DamageHitBuilder` skips
for now); cross-referencing a hit against `combat_interactions.json`'s own damage/kill numbers to
double-check they agree; and, for armor specifically, `armor_purchases.json`
(`ArmorPurchaseBuilder`) only reports **when a player bought/equipped a Heavy/Light/(unlabeled
25-point "Plasma") armor item** — read from `actors.parquet`'s own actor-open events for
`HeavyArmorItem_C`/`LightArmorItem_C`/`PlasmaArmorItem_C` (these open their own channel and show up
there "purely because they opened a channel," per vrfkit — no RPC decoding needed for this part),
with ownership resolved by walking `net_guids.parquet`'s `OuterNetGuid` chain up to a known player.
It is a purchase *event*, not a live remaining-armor value, and whether it should reset every round
or persist until a new purchase (VALORANT does let unbroken shields carry over between rounds) is
this project's own assumption (persist-until-replaced), not confirmed against real per-round
buy-phase timing.

If tracers/weapon/armor don't appear, or look wrong, that's expected until this gets checked
against a real export — `dotnet run --project src/VrfInsights.Cli -- dump-values ./export --field
MulticastNotifyDamage_Point --limit 5` is the equivalent diagnostic command for this data (see
`DamageHitBuilder`'s doc comment for exactly what to check).

### Shot tracers (`shots.json`, secondary/experimental)

A second, independent attempt at the same animation, built first and kept for anyone who wants to
compare it against the hit-based one above. Each recent shot in `shots.json` can draw a brief
animated tracer line on the minimap from the shooter's position, outward in the direction they
fired. **This is the least-confirmed thing in this project** — it didn't produce visible tracers
for at least one real replay, and nothing about it has been checked against a real decoded export.
It was built entirely by reading vrfkit's own Rust/Python source
(`crates/vrfkit/src/sink/rpc.rs`, `crates/vrf-decode/src/effect.rs` and `effect/json.rs`,
`docs/DATA.md`, and vrfkit's reference downstream converter `tools/to_valplay_bundle.py`) — the
same tier of confidence `AbilityCastBuilder` had *before* `dump-fields`/`dump-values` confirmed its
assumptions, not after.

- **What it's based on:** every weapon shot fires a `ReplayPlayContinuousEffectAtLocation` RPC
  carrying three parameter blobs (`FloatValues`, `ObjectValues`, `VectorValues`), each decoded by
  vrfkit into a JSON array of `{"tag": <gameplay-tag handle>, "value": ...}` pairs, landing in
  `fields.parquet` with `FieldName` exactly `"ReplayPlayContinuousEffectAtLocation.FloatValues"`
  etc. `GameplayTagTable` resolves each blob's numeric tag handles to real names via
  `manifest.json`'s `net_field_export_groups` — **replay-specific** (vrfkit's own words), rebuilt
  fresh per replay rather than hardcoded.
- **Likely candidates for why it came up empty**, per a follow-up research pass: this RPC's
  `GroupPath` is the enclosing `_ClassNetCache` group (`ShooterCharacter_ClassNetCache` in a real
  fixture vrfkit's own tests use) — not anything containing the RPC's own name — so a filter that
  checked `GroupPath` for `ReplayPlayContinuousEffectAtLocation` would match nothing (this
  project's actual filter checks `FieldName` instead, which should be right, but is exactly the
  kind of assumption this whole feature rests on without having been checked against a real
  export); or the per-element JSON `value` shape assumed for vector/object tags may not match
  what a real replay's blob actually contains.
- **Deliberately stylized, not physically simulated, even if the data itself gets confirmed later:**
  VALORANT's guns are hitscan, so the "travel" is a fixed-length cosmetic animation, not a real
  bullet path — same treatment this project already gives an ability wall's length.

See `ShotFiredBuilder`'s doc comment for the full unconfirmed-assumptions list if you want to debug
this one specifically; otherwise the hit-based tracer above is the one to trust more.

## Project layout

- **`VrfInsights.Data`** — reads vrfkit's Parquet tables (`fields`, `movement`, `actors`,
  `net_guids`, `events`) and `manifest.json` into typed row/manifest models. No business logic.
- **`VrfInsights.Analysis`** — everything derived: player identity (joining `manifest.players` to
  `game_specific_data`'s `playerLoadouts`), movement tracks, vision cones (computed — VALORANT's
  replay doesn't carry a "vision cone" field, see below), round timeline, per-round attack/defense
  sides (`Rounds/TeamSideResolver.cs` — see "Team colors" above), utility/persistent-effect
  lifecycle, ability casts, combat interactions, economy, per-hit events and weapon resolution
  (`Combat/DamageHitBuilder.cs` + `Weapons/WeaponCatalog.cs` — see "Hit tracers, weapon, and armor"
  above; the primary tracer/weapon signal), the older per-shot events (`Weapons/ShotFiredBuilder.cs`
  — secondary/experimental, see its own section above), armor purchases
  (`Loadouts/ArmorPurchaseBuilder.cs`), and best-effort map detection (`Identity/MapDetector.cs` +
  `Identity/MapCatalog.cs`, against Riot's own map list).
- **`VrfInsights.Pipeline`** — shells out to `vrfkit.exe` (`VrfkitExportRunner`) and runs the
  analysis (`AnalysisPipeline`); `FullPipeline` composes the two into the "one command" flow.
  `VrfkitBootstrapper` automates `git clone` + `cargo build` for vrfkit itself (see Option A
  above) so a person never has to. `ReplayDiscovery` lists `.vrf` files already sitting in
  `%LOCALAPPDATA%\VALORANT\Saved\Demos` for the GUI's picker. `ExternalProcessRunner` is the one
  shared place that actually launches a child process (vrfkit, git, or cargo) and streams its
  output — this project is the *only* place that launches any of them.
- **`VrfInsights.Cli`** — the `vrf-insights` console tool: `run` (the one-command path), `analyze`
  (analysis only, against an export you already made), and the `dump-*` diagnostic commands.
- **`VrfInsights.Gui`** — a small hand-built WinForms app (`net10.0-windows`) on top of
  `VrfInsights.Pipeline`, for people who'd rather click buttons than type CLI flags.
- **`VrfInsights.Web`** — the hosted "upload a `.vrf` in your browser" backend: a small ASP.NET
  Core API wrapping the exact same `VrfInsights.Pipeline` code the CLI/GUI use, so the browser
  never has to deal with vrfkit or a folder of JSON files directly. See `server/README.md` for
  building and deploying it (Render's free tier needs no card at all; Cloud Run is a documented
  alternative for later).
- **`VrfInsights.Tests`** — xUnit tests for the pure-logic pieces (array-flattening pivot, vision
  cone geometry, round/event attribution, team-side resolution, utility open/dormant/close state
  machine).
- **`viewer/`** — the 2D replay viewer (plain HTML/CSS/JS, no build step); see above. Its load
  panel offers either a direct `.vrf` upload (via `VrfInsights.Web`, see `server/README.md`) or
  picking a local analysis output folder by hand.
- **`scripts/Fetch-Assets.ps1`** — downloads the viewer's map/agent/weapon art from valorant-api.com.
- **`scripts/Build-Gui-Exe.ps1`** — publishes `VrfInsights.Gui` as a standalone, single-file
  `.exe` (see Option A above) so it can be launched without `dotnet run` or a `.bat` file.

## Agent codenames

Every agent's Blueprint asset path under `/Game/Characters/...` — and so every `class_path` you'll
see in `dump-classes`/`dump-actors` output, in `actors.parquet`, and anywhere else this project
reads that string straight from the replay — uses Riot's internal *development* codename, not the
agent's public release name. Reyna's folder is `Vampire`, Omen's is `Wraith`, Jett's is `Wushu`,
Chamber's is `Deadeye`, and so on. These can't be renamed (they're literally Riot's asset
structure baked into the replay format), so this project's own code and diagnostics will always
show codenames in raw `class_path` strings — but everywhere else (docs, UI copy, conversation)
should use real names.

The full mapping is recorded once, in code, at
[`AgentCodenames.cs`](src/VrfInsights.Analysis/Common/AgentCodenames.cs) — a plain lookup table
(`CodenameToRealName`) plus a small `ResolveRealName(classPath)` helper for turning a raw
`class_path` into a real name in logs/debugging. It's reference data, not derived from any
replay. Only 8 of the ~30 listed agents have actually been seen in the sample export this project
was developed against (confirmed via `dump-classes`): Gekko, Chamber, KAY/O, Phoenix, Waylay,
Reyna, Omen, and Jett.

For real ability names per agent (fetched from Riot's own public `valorant-api.com`, not the
replay), and an honest accounting of which internal ability *codenames* are actually confirmed
versus just raw, unmatched `dump-classes` output, see
[`docs/AGENT_ABILITIES.md`](docs/AGENT_ABILITIES.md).

## On "vision cones" specifically

VALORANT's replay does not replicate anything called a vision cone. What's real and exact (per
vrfkit's own cross-validation against the reference parser) is each player's position and yaw
over time, in `movement.parquet`. `VisionConeCalculator` derives a cone from that plus
VALORANT's publicly known default FOV (103°) — ordinary trigonometry on already-decoded data, not
anything extracted from the replay format itself. The FOV/range/eye-height are configurable
approximations, not measured values (a player's actual FOV is a local client setting, not part of
what a replay contains).

## Honesty about what's verified vs. assumed

I built this without a compiler available in the environment I wrote it in (no .NET SDK, and
the sandbox's network policy blocked installing one), so **please run `dotnet build` and
`dotnet test` yourself before relying on this** — I can't promise it's compile-clean, only that
I checked every non-obvious API call (especially Parquet.Net's) against that library's actual
source and tests rather than from memory.

What's schema-verified against vrfkit's own documentation and source (`docs/USAGE.md`,
`docs/DATA.md`, `manifest.rs`), not guessed:

- All five main table schemas (`fields`, `movement`, `actors`, `net_guids`, `events`) — column
  names and types.
- `manifest.json`'s top-level shape, `players[]`, and that `game_specific_data` is an array of
  raw embedded JSON strings (one of which contains `playerLoadouts`).
- The `Comp_AbilityStatisticsReplicator.AbilityCastsThisRound` member names (`Player`, `Slot`,
  `Round`, `RoundPhase`, `CastTime`) and the CastTime-vs-`roundStarted` timing offset.
- The `CombatReport` flattened path *shape* (`Rounds[r].Reports[p].Interactions[i].<Member>`) —
  but see the fix below regarding the member *names* under it, which were assumed, not verified.
- `MoneyManagementComponent`'s `Money` / `StartOfRoundMoney` / `TotalMoneyGranted`.
- The `actors.parquet` open/dormant/close lifecycle semantics (dormant ≠ despawn).
- `events.characterDeath`'s `(word0, word1)` = `(killer, killed)`, and both are *character pawn*
  NetGUIDs — the same ID `movement.parquet`/`PlayerIdentity.CharacterNetGuid` already use, per
  vrfkit's own `docs/KILL_LEDGER.md` ("The character-death words reference character pawns").
- `BombEquippable_C.Owner` writes name the carrying character pawn's NetGUID directly (or, for a
  proxy actor like Gekko's Wingman, via that actor's own `Instigator` field) — the same signal
  vrfkit's own `tools/extract_spike_carrier.py` uses to find the planter, per `docs/DATA.md`'s
  "Spike carrier"/"Planter" rows.

**Fixed, and confirmed against a real export rather than guessed:** `AbilityCastBuilder` used to
assume `CastLocation` (an `FVector`) flattens into `CastLocation.X`/`.Y`/`.Z` child fields, the way
some other nested members do elsewhere, and that every member's `field_name` was the plain
readable name (`Player`, `Slot`, `Round`, ...). Both turned out wrong: `dump-fields` against a real
replay showed every member of `AbilityCastsThisRound` carries vrfkit's own disambiguation suffix
(`Player_11_<hash>`, `CastLocation_21_<hash>`, etc. — the numeric/hash part isn't stable across
builds), and `dump-values` showed `CastLocation` is one field whose value is a bare `(X,Y,Z)`
string, not three separate fields. `AbilityCastBuilder` now matches members by name *prefix*
(tolerating the suffix) and parses that string directly — see its own doc comment, and
`AbilityCastBuilderTests.cs` for tests built from the real field names/values above.

**Fixed via code review, NOT yet confirmed against a real export (unlike the fixes above/below,
which were):** `CombatReportBuilder` (which produces `combat_interactions.json`, the ticker's
kill/assist/damage/hit source) looked up `Interactions[i]`'s members by an exact name —
`"DamageDealt"`, `"DidKill"`, `"AssistType"`, etc. — the same mistake `AbilityCastBuilder` made
and fixed above for `AbilityCastsThisRound`'s members. Since that fix was never carried over here,
and vrfkit's disambiguation suffix (confirmed real for `AbilityCastsThisRound`) is a
flattened-array-wide behavior rather than something specific to one array, every one of those
exact-name lookups was almost certainly silently missing its field on every real export — which
would surface as **every player showing 0 kills** (deaths still worked, since those come from the
separately-sourced `events.characterDeath`, not this report) — reported by a user as exactly that
symptom. Fixed the same way as `AbilityCastBuilder`: suffix-tolerant prefix matching
(`CombatReportBuilder.FindMember`). **What's still unconfirmed:** whether `DamageDealt`/
`HitsDealt`/etc. really are flat members of `Interactions[i]` once the suffix is stripped, or
whether some nest a level deeper (e.g. per-opponent `DealtInteractions[j]`/`ReceivedInteractions[j]`
sub-arrays — `FlattenedArrayPivot`'s own doc comment flags this as possible for this exact
struct); and — separate from whether `DidKill` itself now reads correctly — this report has never
promoted *which opponent* an interaction was against, because no such field has been confirmed to
exist yet (needed for anything that has to draw a line from shooter to target, not just count a
kill). If kills still read as 0 after this fix, or you want opponent-level detail, run
`dump-fields --group CombatReport` against a real export and check what `field_name` actually
looks like under one `Interactions[i]` entry — `combat_interactions.json`'s `RawMembers` on each
interaction already carries every member this project doesn't otherwise promote, verbatim, for
exactly this kind of check.

**Also fixed, and confirmed against a real export rather than guessed:** smoke/molly/grenade/etc.
markers in the viewer (`utility.json`, `UtilityTimelineBuilder`) used to mostly land at the casting
player's spawn point instead of the real effect location. `dump-classes` against a real export
showed why: a single smoke cast spawns **three** separately-classified actors, all of which
`UtilityEffectClassifier` matches as `"Smoke"` and (before this fix) each got its own
`PersistentEffectEvent` marker — e.g. for Omen's smoke (class paths use the codename `Wraith` — see
"Agent codenames" below), `Ability_Wraith_4_Smoke`, `Projectile_Wraith_4_Smoke`, and
`Zone_Wraith_4_Smoke`; for Jett's Cloudburst (codename `Wushu`), `Ability_Wushu_4_Smoke`,
`Projectile_Wushu_4_Smoke`, and `GameObject_Wushu_4_SmokeZone`.

`dump-actors --class <each of those names>` against a real export (Omen's and Jett's smoke, both
run side by side) confirmed exactly what those three actors are: `Ability_*` opens once per player
at ~match start (`t≈72ms`) at that player's spawn position and never reopens — it's a per-player
ability-slot container, not a placement, and its always-near-spawn position is exactly the
reported bug. `Projectile_*` opens per-cast and closes quickly after (the in-flight grenade's
travel time — often well under a second). `Zone_*`/`*Zone` opens slightly after the projectile and
stays open for the ability's real on-field duration (tens of seconds) at a different, correct
landing position.

`UtilityTimelineBuilder` now (1) excludes `Ability_*` container actors entirely
(`UtilityEffectClassifier.IsAbilityContainerActor`) and (2), per ability — grouped by agent + slot
via `UtilityEffectClassifier.ExtractAbilityGroupKey`, so this can't bleed across unrelated
abilities that happen to share a category — prefers a `Zone`-named actor's position over a sibling
`Projectile`-named one when both exist for that same ability
(`UtilityTimelineBuilder.PreferZoneOverNonZoneSiblings`). Abilities with no separate Zone actor
(most non-smoke utility, going by `dump-classes`) are unaffected. See
`UtilityEffectClassifierTests.cs` and `UtilityTimelineBuilderTests.cs` for tests built from the
real class paths/timings above — including one confirming a Zone actor for one ability doesn't
suppress an unrelated Projectile actor for another.

This was confirmed for smoke specifically (Omen, Jett) — it has not been separately verified for
molly/wall/trap/etc., though the `Ability_*`-exclusion is a general, structural fix (that actor
type is a per-player container for every ability tree seen in `dump-classes`, not just smoke) and
should help across the board even where a Zone-preference doesn't apply.

**A second, separate cause of the same "still at spawn" symptom, also confirmed against the real
export:** `UtilityEffectClassifier`'s keyword matching runs against the *entire* `class_path`
string, which includes the agent's own codename folder (see "Agent codenames" below) — and Gekko's
codename, `AggroBot`, itself contains the `"Bot"` keyword (mapped to `DroneOrDeployable`). That
false-matched almost everything under `/Game/Characters/AggroBot/...`, including `AggroBot_PC` —
Gekko's player controller, which (like the `Ability_*` containers above) opens once near match
start at that player's spawn position and stays open for the *entire match* — producing a
permanent, never-moving "utility" marker sitting at Gekko's spawn point for the whole game. The
same substring-matching also mis-fires on `Gun_Deadeye_X_Giantslayer_Prototype_FIreRatePrototype`
(Chamber's ultimate gun class, whose name contains "FIreRatePrototype" — a fire-*rate* stat,
nothing to do with incendiary utility), reading it as `"Fire"`.

Fixed two ways: `Classify()` now masks every known agent codename
(`UtilityEffectClassifier.MaskAgentCodenames`, built from `AgentCodenames.CodenameToRealName`) out
of the class path before keyword matching — checked against all ~29 known codenames, `AggroBot`/
`Bot` is (so far) the only such collision, but this masks all of them defensively rather than
special-casing just that one. Separately, `UtilityTimelineBuilder` now also excludes any
`Gun_*`-prefixed actor (`UtilityEffectClassifier.IsWeaponModelActor`) — a weapon/gun model is never
a utility placement, whatever it happens to match on. See `UtilityEffectClassifierTests.cs` (the
`AggroBot`/masking and `Gun_` tests) and `UtilityTimelineBuilderTests.cs`'s
`Build_NeverPlacesGekkosPlayerControllerAsAUtilityMarker` /
`Build_ExcludesWeaponModelActors_EvenIfTheirClassNameAccidentallyMatchesAKeyword` for the
regression coverage, built from the user's real `dump-classes` output.

What's a documented **assumption**, flagged in code comments, and worth checking with
`dump-fields`/`dump-values`/`dump-classes`/`dump-actors` against your own export before trusting:

- `MoneyManagementComponent`'s rows are joined to a player by `actor_net_guid` directly, assuming
  it replicates on the player's own PlayerState actor rather than a subobject.
- `UtilityEffectClassifier`'s keyword list is a starting point, not a per-agent catalogue — run
  `dump-classes` on your own export and extend the keyword table with what you actually see.
- The embedded agent id→name table (`Identity/agents.json`) has 27 confirmed entries fetched from
  Riot's own public content API (`valorant-api.com`) and is still missing a few (Sage and Yoru,
  confirmed absent as of this writing) — the bulk agent-list endpoint is large enough that this
  project's own fetch tooling truncates it before reaching every entry. It degrades to a labeled
  raw GUID rather than guessing, and if you see `(unrecognized agent id: ...)` in the viewer's
  roster or its Debug info panel, that GUID can be looked up directly at
  `https://valorant-api.com/v1/agents/<the-guid>` (a single-agent response is small enough not to
  get truncated) and added to `agents.json`.
- `events.characterUltimateUsed` overcounts actual ultimate casts by ~51.5% per vrfkit's own
  measurement (it's the easy signal, not the precise one — see `UltimateUsageBuilder`'s remarks).
- Map detection (`MapDetector`) is a substring search for a known map's internal asset-path
  folder name (e.g. `Duality` for Bind) inside `net_guids.parquet`/`actors.parquet` paths — a
  reasonable bet since that path has to appear *somewhere* in an object-path table, but not a
  confirmed "this field means the map." Comes back `null` (never a wrong guess) when nothing
  matches, and `match.json`'s `Map` field is null in that case — the viewer then asks you to pick
  the map yourself rather than assuming one.
- `TeamSideResolver`'s team-roster split (which players are grouped together, as opposed to which
  side is attacking — see "Team colors" above) comes from clustering round-1 spawn positions into
  two groups by distance. This is this project's own geometric method, not something vrfkit
  documents directly — a reasonable bet since the two teams' spawn rooms are always far apart on
  every map, but nothing stops an unusual export from having incomplete spawn data. It returns no
  sides at all for a match rather than a wrong split when spawn data is missing or too sparse to
  cluster.
- The map coordinate transform (`xMultiplier`/`yMultiplier`/`xScalarToAdd`/`yScalarToAdd` in
  `Identity/maps.json`) is Riot's own published formula from valorant-api.com, used exactly as
  published — not reverse-engineered — but not independently pixel-checked here against a real
  replay overlaid on a real minimap image.
- `PersistentEffectEvent.YawDegrees` (an actor's spawn yaw) is carried straight from
  `actors.parquet`'s `spawn_yaw`, same confidence as the rest of that table — what's an assumption
  is only how the 2D viewer *uses* it (a fixed-length line for a wall, since the replay has no
  size/extent field for it).
- **`hits.json`/`DamageHitEvent`/`DamageHitBuilder`, `armor_purchases.json`/`ArmorPurchase`/
  `ArmorPurchaseBuilder`, and `Weapons/WeaponCatalog`** (power the minimap's hit-tracer animation
  and the ticker's weapon/armor display) — like the `shots.json` entry below, built from reading
  vrfkit's own source directly rather than confirmed here via `dump-fields`/`dump-values` against a
  real export. Higher-confidence than `shots.json` was, though, since the underlying RPC
  (`MulticastNotifyDamage_Point`) is one vrfkit's own docs mark at their highest confidence tier
  (✅) — see "Hit tracers, weapon, and armor" above for the full breakdown of what's confirmed vs.
  inferred vs. assumed within this piece, and how to check it against your own export.
- **`shots.json`/`ShotFiredEvent`/`ShotFiredBuilder`/`GameplayTagTable`** — the original attempt at
  the same tracer animation `hits.json` now provides; kept as a secondary/experimental path since
  it didn't produce visible output for at least one real replay (see "Shot tracers
  (secondary/experimental)" above). Same "built from source, not confirmed" caveat as `hits.json`
  above, but a lower confidence tier — the RPC it reads isn't one vrfkit's own docs specifically
  rate, unlike `MulticastNotifyDamage_Point`.

`VrfInsights.Pipeline` and `VrfInsights.Gui` are new and carry the same caveat as the rest of
this project — please actually click through the GUI once (or run `vrf-insights run`) before
trusting it blindly. The WinForms UI is hand-coded (no designer/`.resx` file) specifically so
there's nothing beyond ordinary, well-documented `System.Windows.Forms` API calls in it, but I
still have no way to compile or visually check it myself.

Nothing above affects the parts most central to what was asked — movement, utility/smoke
placement and lifetime, agents, and round/event structure all rest on directly-documented,
cross-validated columns.

## Requirements

- .NET 10 SDK (targets `net10.0`, and `net10.0-windows` for the GUI; drop the `<TargetFramework>`
  in each `.csproj` to `net8.0`/`net8.0-windows` if you're on an older SDK instead)
- `VrfInsights.Gui` only builds/runs on Windows (`net10.0-windows` + WinForms) — the CLI and the
  `run` command work anywhere the .NET SDK does, same as vrfkit itself
- A built `vrfkit.exe`/`vrfkit` binary (Rust 1.86+) — either point `run`/the GUI at it, or run
  `vrfkit export <file.vrf> --out <dir>` yourself first and use `analyze` — see
  [vrfkit](https://github.com/yakisoba0728/vrfkit) for build instructions

## Disclaimer

Not affiliated with, endorsed by, or approved by Riot Games. VALORANT and all related trademarks
are the property of Riot Games, Inc. This project only reads data vrfkit has already decoded from
your own local replay files — analyze replays you have a right to access, and be aware that
Riot's terms of service govern what you may do with client data regardless of what's technically
possible.
