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

This downloads every competitive map's minimap image and every agent's icon from Riot's own
public content API into `assets/maps/`, `assets/agents/`, plus `assets/catalog.json` /
`assets/catalog.js` (metadata the viewer reads — see the script's own comments for why there are
two copies). Safe to re-run any time; already-downloaded files are skipped. If you'd rather not
run a script against a third-party API yourself, tell me and I'll take the images as an upload
instead — either way, nothing about the vrfkit/decoding side of this project is affected, this is
purely artwork.

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
wrong. A handful of maps have a **known-good rotation built in** (see the table below) — those
apply automatically the first time you load that map, no setup needed. For anything else, use the
**Map orientation** control (rotate 0/90/180/270°, plus a flip checkbox) next to the facing offset
to correct it by eye — try each rotation until the two teams land on the correct sides. It's per-map
and remembered in your browser (`localStorage`) once set, so you only need to set it once per map,
ever, and a **Reset map fit** button puts it back to that map's built-in default (or 0°/no flip if
it doesn't have one) if you want to start over.

**Maps with a confirmed rotation built in** (`KNOWN_MAP_ORIENTATIONS` in `viewer/app.js`) — this
one moves where each dot lands, without touching the downloaded picture itself:

| Map | Rotation | Confirmed by |
|---|---|---|
| Sunset | 90°, flipped | Matched against a player's own room-by-room read of a live match, then separately checked against 8 of 10 real player positions taken from an in-game screenshot (see below) |

**Maps where the downloaded picture itself needed rotating** (`MAP_IMAGE_ROTATIONS` in
`viewer/app.js`) — a different, independent correction for when the minimap art as downloaded is
just sideways/upside-down, rather than the coordinate formula disagreeing with an otherwise-correct
picture; this one physically turns the drawn image and leaves dot positions using the plain,
uncorrected formula:

| Map | Rotation | Confirmed by |
|---|---|---|
| Ascent | 90° clockwise | Reported by a user — turning just the dot-position mapping (the mechanism above) didn't fix it, turning the picture itself did. Not yet independently cross-checked against a screenshot with known positions the way Sunset was, so if agents still land in the wrong rooms after this, the fix may need to go the other direction (270°) or use both mechanisms together |

If you work out a good rotation for another map, add it to whichever of the two tables in `app.js`
actually fixed it (one line) so nobody has to rediscover it — see "How to verify a map's rotation"
below. If you're not sure which of the two a given map needs, try the dot-position **Map
orientation** control in the UI first (it's instant, no code change) — if THAT alone lines
everything up, it belongs in `KNOWN_MAP_ORIENTATIONS`; if the dots land right but the picture
itself looks sideways (walls/rooms rotated relative to what the dots are doing), it belongs in
`MAP_IMAGE_ROTATIONS` instead.

If positions are pointed in the right general direction but still look a little off-center —
clipping into walls that aren't near the real spawn, or drifting slightly away from the correct
rooms — that's a smaller, separate problem: the downloaded map image's content might not fill the
exact same square Riot's coordinate formula was calibrated against (valorant-api.com's
`displayIcon` is a separate asset, not necessarily pixel-identical to whatever the game client
renders internally). The **Scale** and **Pan X%/Y%** controls next to Map orientation compensate
for that — Scale zooms in/out around the image center, the two Pan fields shift it — also
remembered per map. In practice this tends to be a small correction (a few percent), not the main
source of misalignment — get rotation right first. The Pan sliders move in tenths of a percent
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
resolved for that particular replay.

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

## Project layout

- **`VrfInsights.Data`** — reads vrfkit's Parquet tables (`fields`, `movement`, `actors`,
  `net_guids`, `events`) and `manifest.json` into typed row/manifest models. No business logic.
- **`VrfInsights.Analysis`** — everything derived: player identity (joining `manifest.players` to
  `game_specific_data`'s `playerLoadouts`), movement tracks, vision cones (computed — VALORANT's
  replay doesn't carry a "vision cone" field, see below), round timeline, per-round attack/defense
  sides (`Rounds/TeamSideResolver.cs` — see "Team colors" above), utility/persistent-effect
  lifecycle, ability casts, combat interactions, economy, and best-effort map detection
  (`Identity/MapDetector.cs` + `Identity/MapCatalog.cs`, against Riot's own map list).
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
- **`VrfInsights.Tests`** — xUnit tests for the pure-logic pieces (array-flattening pivot, vision
  cone geometry, round/event attribution, team-side resolution, utility open/dormant/close state
  machine).
- **`viewer/`** — the 2D replay viewer (plain HTML/CSS/JS, no build step); see above.
- **`scripts/Fetch-Assets.ps1`** — downloads the viewer's map/agent art from valorant-api.com.
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
- The `CombatReport` flattened path shape (`Rounds[r].Reports[p].Interactions[i].<Member>`) and
  the member names used here (`DamageDealt`, `DamageReceived`, `HitsDealt`, `HitsReceived`,
  `DidKill`, `AssistType`, `bIsWallPen`).
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
