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

```bash
# 1. Decode the replay with vrfkit (once per replay) — see https://github.com/yakisoba0728/vrfkit
cargo +1.86.0 build --release -p vrfkit --features export --locked
./target/release/vrfkit export path/to/match.vrf --out ./export

# 2. Analyze vrfkit's output with this project
dotnet run --project src/VrfInsights.Cli -- analyze ./export --out ./analysis --with-vision
```

`analysis/` then contains:

| File | Contents |
|---|---|
| `match.json` | Replay build, duration, resolved player/agent/loadout list, round boundaries |
| `events.json` | The server's own event timeline (kills, ultimates, spike plant/defuse/explode, round starts), attributed to a round number |
| `movement.json` | Per-player position/rotation/velocity time series (from `movement.parquet`) |
| `vision_cones.json` | *(only with `--with-vision`)* one derived vision cone per movement sample per player |
| `utility.json` | Smoke/wall/molly/trap/etc. placement events with spawn/despawn time and position |
| `ability_casts.json` | Ability casts from `Comp_AbilityStatisticsReplicator.AbilityCastsThisRound` (caster, slot, round, location) |
| `ultimate_usages.json` | Ultimate-cast signal from the server's own event timeline |
| `combat_interactions.json` | Per-round `CombatReport` interactions (damage, hits, kill/assist, wallbang) |
| `economy.json` | `MoneyManagementComponent` credit snapshots over time |

Two diagnostic commands help you verify or extend the assumptions below against your own export:

```bash
dotnet run --project src/VrfInsights.Cli -- dump-fields ./export --group Comp_AbilityStatisticsReplicator
dotnet run --project src/VrfInsights.Cli -- dump-classes ./export
```

## Project layout

- **`VrfInsights.Data`** — reads vrfkit's Parquet tables (`fields`, `movement`, `actors`,
  `net_guids`, `events`) and `manifest.json` into typed row/manifest models. No business logic.
- **`VrfInsights.Analysis`** — everything derived: player identity (joining `manifest.players` to
  `game_specific_data`'s `playerLoadouts`), movement tracks, vision cones (computed — VALORANT's
  replay doesn't carry a "vision cone" field, see below), round timeline, utility/persistent-effect
  lifecycle, ability casts, combat interactions, economy.
- **`VrfInsights.Cli`** — the `vrf-insights` console tool tying it together.
- **`VrfInsights.Tests`** — xUnit tests for the pure-logic pieces (array-flattening pivot, vision
  cone geometry, round/event attribution, utility open/dormant/close state machine).

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

What's a documented **assumption**, flagged in code comments, and worth checking with
`dump-fields`/`dump-classes` against your own export before trusting:

- That `CastLocation` (an `FVector`) flattens as `CastLocation.X`/`.Y`/`.Z` child fields — this
  specific sub-field naming wasn't independently confirmed against a real export.
- `MoneyManagementComponent`'s rows are joined to a player by `actor_net_guid` directly, assuming
  it replicates on the player's own PlayerState actor rather than a subobject.
- `UtilityEffectClassifier`'s keyword list is a starting point, not a per-agent catalogue — run
  `dump-classes` on your own export and extend the keyword table with what you actually see.
- The embedded agent id→name table (`Identity/agents.json`) has 23 confirmed entries fetched from
  Riot's own public content API (`valorant-api.com`) and is very likely missing a few agents —
  it degrades to a labeled raw GUID rather than guessing, and is trivially refreshable from the
  same endpoint.
- `events.characterUltimateUsed` overcounts actual ultimate casts by ~51.5% per vrfkit's own
  measurement (it's the easy signal, not the precise one — see `UltimateUsageBuilder`'s remarks).

Nothing above affects the parts most central to what was asked — movement, utility/smoke
placement and lifetime, agents, and round/event structure all rest on directly-documented,
cross-validated columns.

## Requirements

- .NET 8 SDK
- A vrfkit export directory (`vrfkit export <file.vrf> --out <dir>`) — see
  [vrfkit](https://github.com/yakisoba0728/vrfkit) for build instructions (Rust 1.86+)

## Disclaimer

Not affiliated with, endorsed by, or approved by Riot Games. VALORANT and all related trademarks
are the property of Riot Games, Inc. This project only reads data vrfkit has already decoded from
your own local replay files — analyze replays you have a right to access, and be aware that
Riot's terms of service govern what you may do with client data regardless of what's technically
possible.
