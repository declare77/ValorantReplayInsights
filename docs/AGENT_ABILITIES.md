# Agent abilities: real names, codenames, and what's actually confirmed

Two different things live in this file, and they come from two different sources with very
different confidence levels — read the caveats, don't just skim the tables.

1. **Real agent/ability names**, fetched live from Riot's own public content API
   (`valorant-api.com`) on 2026-09-11. This is verifiable public data, not reverse-engineered from
   any replay.
2. **Internal dev codenames** (agent-level from the user; ability-level from this project's own
   `dump-classes`/`dump-actors` runs against a real replay export) — see "About the codenames"
   below for exactly what is and isn't confirmed here, because it's less than you might expect.

## Agent real name ↔ dev codename

From [`AgentCodenames.cs`](../src/VrfInsights.Analysis/Common/AgentCodenames.cs) — user-supplied,
not derived from any replay. Only the 8 marked **✓** have actually been seen in a real export via
`dump-classes`.

| Real name | Codename | Seen in a real export? |
|---|---|---|
| Brimstone | Sarge | |
| Viper | Pandemic | |
| Omen | Wraith | ✓ |
| Killjoy | Killjoy *(unchanged)* | |
| Cypher | Gumshoe | |
| Sova | Hunter | |
| Sage | Thorne | |
| Phoenix | Phoenix *(unchanged)* | ✓ |
| Jett | Wushu | ✓ |
| Reyna | Vampire | ✓ |
| Raze | Clay | |
| Breach | Breach *(unchanged)* | |
| Skye | Guide | |
| Yoru | Stealth | |
| Astra | Rift | |
| KAY/O | Grenadier | ✓ |
| Chamber | Deadeye | ✓ |
| Neon | Sprinter | |
| Fade | BountyHunter | |
| Harbor | Mage | |
| Gekko | AggroBot | ✓ |
| Deadlock | Cable | |
| Iso | Sequoia | |
| Clove | Smonk | |
| Vyse | Nox | |
| Tejo | Cashew | |
| Waylay | Terra | ✓ |
| Veto | Pine | |
| Miks | Iris | |

## Real ability names, by agent

Fetched from `valorant-api.com/v1/agents/{id}` (per-agent, not the bulk endpoint — see "About the
codenames" for why). Slot names are Riot's own: **Ability1**/**Ability2** are the Q/E-keybound
abilities, **Grenade** is the C-keybound signature ability (not necessarily a literal grenade),
**Ultimate** is X, **Passive** (where an agent has one) has no keybind.

| Agent | Ability1 | Ability2 | Grenade (signature) | Ultimate | Passive |
|---|---|---|---|---|---|
| Brimstone | Incendiary | Sky Smoke | Stim Beacon | Orbital Strike | |
| Viper | Poison Cloud | Toxic Screen | Snake Bite | Viper's Pit | Toxic |
| Omen | Paranoia | Dark Cover | Shrouded Step | From the Shadows | |
| Killjoy | ALARMBOT | Turret | Nanoswarm | Lockdown | |
| Cypher | Cyber Cage | Spycam | Trapwire | Neural Theft | |
| Sova | Shock Bolt | Recon Bolt | Owl Drone | Hunter's Fury | Uncanny Marksman |
| Sage | Slow Orb | Healing Orb | Barrier Orb | Resurrection | |
| Phoenix | Hot Hands | Curveball | Blaze | Run It Back | Heating Up |
| Jett | Updraft | Tailwind | Cloudburst | Blade Storm | Drift |
| Reyna | Devour | Dismiss | Leer | Empress | |
| Raze | Blast Pack | Paint Shells | Boom Bot | Showstopper | |
| Breach | Flashpoint | Fault Line | Aftershock | Rolling Thunder | |
| Skye | Trailblazer | Guiding Light | Regrowth | Seekers | |
| Yoru | Blindside | Gatecrash | Fakeout | Dimensional Drift | |
| Astra | Nova Pulse | Nebula / Dissipate | Gravity Well | Cosmic Divide | Astral Form |
| KAY/O | FLASH/drive | ZERO/point | FRAG/ment | NULL/cmd | |
| Chamber | Headhunter | Rendezvous | Trademark | Tour De Force | |
| Neon | Relay Bolt | High Gear | Fast Lane | Overdrive | |
| Fade | Seize | Haunt | Prowler | Nightfall | |
| Harbor | High Tide | Cove | Storm Surge | Reckoning | |
| Gekko | Wingman | Dizzy | Mosh Pit | Thrash | |
| Deadlock | Sonic Sensor | GravNet | Barrier Mesh | Annihilation | |
| Iso | Undercut | Double Tap | Contingency | Kill Contract | |
| Clove | Meddle | Ruse | Pick-me-up | Not Dead Yet | |
| Vyse | Shear | Arc Rose | Razorvine | Steel Garden | |
| Tejo | Special Delivery | Guided Salvo | Stealth Drone | Armageddon | |
| Waylay | Lightspeed | Refract | Saturate | Convergent Paths | |
| Veto | Chokehold | Interceptor | Crosscut | Evolution | |
| Miks | Harmonize | Waveform | M-pulse | Bassquake | |

*(Fetched via a tool that summarizes page content rather than dumping raw JSON, so treat any name
above that looks visually off — stray punctuation, a merged field — as worth a manual check against
`valorant-api.com/v1/agents/{id}?language=en-US` directly rather than fully trusting on sight.)*

## About the ability-level codenames — what's actually confirmed, and what isn't

**`valorant-api.com` has no internal codenames at all** — the responses above only ever contain
the real, public ability names (`displayName`), slot, description, and icon URLs. There's no field
anywhere in that API that reveals what an ability is called internally. So the request to "get the
codenames from the Valorant API" doesn't quite work as stated — codenames only ever come from a
real replay's own `dump-classes`/`dump-actors` output, which is data this project produces itself,
not something an external API can hand over.

Given that, here's what's actually confirmed for the 8 agents seen in this project's sample
export, and what isn't:

- **Confirmed, with real behavioral evidence, not just a name guess:** Omen's Shrouded Step is
  `Ability_Wraith_4_Smoke` / `Projectile_Wraith_4_Smoke` / `Zone_Wraith_4_Smoke`, and Jett's
  Cloudburst is `Ability_Wushu_4_Smoke` / `Projectile_Wushu_4_Smoke` /
  `GameObject_Wushu_4_SmokeZone` — confirmed via `dump-actors`, matching each ability's real
  duration and behavior (see `UtilityTimelineBuilder`'s doc comment).
- **Not safe to guess, and I'm not going to:** for the *other* abilities of those same 8 agents,
  `dump-classes` gives the raw class names/folders (e.g. Gekko's `Ability_Q_Aggrobot_SeekerNade`,
  `Ability_E_Aggrobot_DiscTurret`; KAY/O's `Ability_Grenadier_C_Flash`,
  `Ability_Grenadier_Q_BasicSemtex`), but there's no reliable way to match each one to a specific
  real ability name from the class name or folder alone. I tried the obvious shortcut — assuming
  the ability folder number/letter (`Ability_Q`, `Ability_E`, `Ability_4`, `Ability_X`) lines up
  with a fixed real-ability slot the same way for every agent — and it doesn't: Gekko's `Ability_4`
  folder holds the grenade-slot ability (`ExplodeyPatch`, matching Mosh Pit), but KAY/O's
  `Ability_4` folder holds an *Ability1*-slot ability instead (`Flash`, i.e. FLASH/drive) while
  KAY/O's grenade-slot ability (FRAG/ment) sits under `Ability_Q`. Since that contradicts itself
  agent-to-agent, presenting a full name-to-codename table for every ability would mean guessing,
  which this project has deliberately avoided everywhere else (see the README's "Honesty about
  what's verified vs. assumed"). If you want the rest of these pinned down the same way the smoke
  ones were, the path is the same one that worked before: know what ability you just cast in a
  round (from your own memory of that match, or from a specific, deliberate test replay), then run
  `dump-actors --class <the suspected internal name>` and check whether an actor opens at that
  same moment. (`AbilityCastsThisRound`'s `Slot` field looks like it should shortcut this — it's a
  per-cast slot index — but this project's own `AbilityCastEvent.Slot` doc comment is explicit that
  the raw slot number's mapping to Ability1/Ability2/Grenade/Ultimate hasn't been independently
  confirmed either, so that's not a shortcut to lean on without checking it first.)
