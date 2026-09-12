// Per-agent non-ultimate ability economics: credit cost, max charges, and any regen rule beyond
// a plain per-round reset. This is game-DESIGN reference data (not anything derived from a
// replay) -- researched against valorant.fandom.com / wiki.playvalorant.com / liquipedia.net as
// of 2026-09, cross-checked across at least two sources per agent. VALORANT balances abilities
// often, so treat this as "best available at time of writing", not permanent truth -- exactly
// like AgentCodenames.cs being "user-supplied, not derived from any replay".
//
// Where sources genuinely disagreed after cross-checking, the LOWER-drama/most-recent-looking
// number was kept and the conflict is noted in `flag`. Treat any `flag`ed value as worth a quick
// in-game double-check before trusting it for something important.
//
// Shape per agent: { grenade, ability1, ability2, ultimateCost }, each ability an object:
//   { name, cost, charges, free, regen }
//     cost: credits for ONE charge (0/omitted if `free` is true)
//     charges: max charges purchasable/available per round
//     free: true if it never costs credits (a true signature ability)
//     regen: optional special regen rule beyond "resets to full next round":
//       { kills: N }     -- gains back a charge after N kills this round (by this player)
//       { seconds: N }   -- automatically regains a charge N seconds after it was last used,
//                           even mid-round (on top of the normal round reset)
//   A slot with no `regen` field just resets to full charges at the start of every round.
//
// A few agents don't fit this per-slot-credit-cost shape at all (Astra's shared Star pool,
// Reyna's Soul-Orb-gated Devour/Dismiss, Chamber's ammo-based Headhunter, Gekko's
// pick-up-to-reuse creatures). Those are called out with `special` instead of forcing them into
// a box that would misrepresent how they actually work -- see each one's comment below.
const AGENT_ABILITY_META = {
  Brimstone: {
    ability1: { name: 'Incendiary', cost: 250, charges: 1 },
    ability2: { name: 'Sky Smoke', free: true, charges: 1, flag: 'exact charge-purchase structure unconfirmed (sources disagree on "free" vs "100cr for 3 placements")' },
    grenade: { name: 'Stim Beacon', cost: 200, charges: 1 },
    ultimateCost: 8,
  },
  Viper: {
    ability1: { name: 'Poison Cloud', cost: 200, charges: 1, flag: 'shares a fuel meter with Toxic Screen -- reactivating/moving an active cloud does not cost a new charge' },
    ability2: { name: 'Toxic Screen', free: true, charges: 1, flag: 'shares the same fuel meter as Poison Cloud' },
    grenade: { name: 'Snake Bite', cost: 300, charges: 1 },
    ultimateCost: 9,
  },
  Omen: {
    ability1: { name: 'Paranoia', cost: 400, charges: 1 },
    ability2: { name: 'Dark Cover', free: true, charges: 2, flag: 'mid-round regen cooldown reported as both 35s and 40s across sources -- not simulated here, resets each round only' },
    grenade: { name: 'Shrouded Step', cost: 100, charges: 2 },
    ultimateCost: 7,
  },
  Killjoy: {
    ability1: { name: 'Alarmbot', cost: 200, charges: 1, flag: 'redeploy-after-recall cooldown (~20s) not simulated here' },
    ability2: { name: 'Turret', free: true, charges: 1, flag: 'redeploy cooldown after destroy/recall (~20-60s) not simulated here' },
    grenade: { name: 'Nanoswarm', cost: 200, charges: 2 },
    ultimateCost: 9,
  },
  Cypher: {
    ability1: { name: 'Cyber Cage', cost: 100, charges: 2 },
    ability2: { name: 'Spycam', free: true, charges: 1, flag: 'redeploy cooldown after destroy/recall (~15-45s) not simulated here' },
    grenade: { name: 'Trapwire', cost: 200, charges: 2 },
    ultimateCost: 7,
  },
  Sova: {
    ability1: { name: 'Shock Bolt', cost: 150, charges: 2 },
    ability2: { name: 'Recon Bolt', free: true, charges: 1, regen: { seconds: 40 } },
    grenade: { name: 'Owl Drone', cost: 400, charges: 1 },
    ultimateCost: 8,
  },
  Sage: {
    ability1: { name: 'Slow Orb', cost: 200, charges: 2 },
    ability2: { name: 'Barrier Orb', cost: 400, charges: 1 },
    grenade: { name: 'Healing Orb', free: true, charges: 1, regen: { seconds: 45 } },
    ultimateCost: 8,
  },
  Phoenix: {
    ability1: { name: 'Curveball', cost: 250, charges: 2 },
    ability2: { name: 'Blaze', cost: 200, charges: 1 },
    grenade: { name: 'Hot Hands', free: true, charges: 1, regen: { kills: 2 } },
    ultimateCost: 6,
  },
  Jett: {
    ability1: { name: 'Updraft', free: true, charges: 1, flag: 'charge count contested across sources (1 vs 2) -- using 1' },
    ability2: { name: 'Tailwind', free: true, charges: 1, regen: { kills: 2 } },
    grenade: { name: 'Cloudburst', cost: 200, charges: 2 },
    ultimateCost: 7,
  },
  Reyna: {
    special: 'Devour (ability2) and Dismiss (grenade) share a purchasable pool (2 charges @ 200cr, usable on either) but ALSO each require a fresh Soul Orb -- which only spawns from a kill and vanishes after ~3s -- to actually cast. A purchased charge with no orb available cannot be used. Not modeled as a simple per-round charge count.',
    ability1: { name: 'Leer', cost: 200, charges: 2 },
    ability2: { name: 'Devour', cost: 200, charges: 2, flag: 'shared pool with Dismiss, see `special`; also gated on a Soul Orb from a kill' },
    grenade: { name: 'Dismiss', cost: 200, charges: 2, flag: 'shared pool with Devour, see `special`; also gated on a Soul Orb from a kill' },
    ultimateCost: 6,
  },
  Raze: {
    ability1: { name: 'Blast Pack', cost: 200, charges: 2 },
    ability2: { name: 'Boom Bot', cost: 300, charges: 1 },
    grenade: { name: 'Paint Shells', free: true, charges: 1, regen: { kills: 2 } },
    ultimateCost: 8,
  },
  Breach: {
    ability1: { name: 'Aftershock', cost: 200, charges: 1 },
    ability2: { name: 'Flashpoint', cost: 250, charges: 2 },
    grenade: { name: 'Fault Line', free: true, charges: 1, regen: { seconds: 40 } },
    ultimateCost: 9,
  },
  Skye: {
    ability1: { name: 'Trailblazer', cost: 300, charges: 1, flag: 'has an in-flight "recast" (detonate early) -- not an extra charge' },
    ability2: { name: 'Guiding Light', cost: 250, charges: 2, regen: { seconds: 40 }, flag: 'has an in-flight recast-to-detonate mechanic too' },
    grenade: { name: 'Regrowth', cost: 150, charges: 1 },
    ultimateCost: 8,
  },
  Yoru: {
    ability1: { name: 'Blindside', cost: 250, charges: 2 },
    ability2: { name: 'Gatecrash', cost: 150, charges: 2, regen: { seconds: 35 }, flag: 'used to be kill-based regen, sources say now time-based -- exact current figure not fully confirmed' },
    grenade: { name: 'Fakeout', cost: 100, charges: 1 },
    ultimateCost: 7,
  },
  Astra: {
    special: 'Nova Pulse (ability1), Gravity Well (grenade) and Nebula/Dissipate (ability2) are not bought individually -- Astra gets a shared pool of 5 free "Stars" per round and converts each into whichever ability she wants, with each ability then having its own reuse cooldown (~25-45s). Not modeled as a simple per-slot credit cost.',
    ability1: { name: 'Nova Pulse', free: true, charges: 5, flag: 'shared Star pool -- see `special`' },
    ability2: { name: 'Nebula / Dissipate', free: true, charges: 5, flag: 'shared Star pool -- see `special`' },
    grenade: { name: 'Gravity Well', free: true, charges: 5, flag: 'shared Star pool -- see `special`' },
    ultimateCost: 7,
  },
  'KAY/O': {
    ability1: { name: 'FLASH/drive', cost: 250, charges: 2 },
    ability2: { name: 'ZERO/point', free: true, charges: 1, regen: { seconds: 40 } },
    grenade: { name: 'FRAG/ment', cost: 200, charges: 1 },
    ultimateCost: 8,
  },
  Chamber: {
    special: 'Headhunter (ability1) is a free sidearm whose special ammo costs credits per-bullet rather than working as a placeable charge -- not modeled here (shown as always "available", no cooldown tracking).',
    ability1: { name: 'Headhunter', free: true, charges: 1, flag: 'ammo-based, not charge-based -- see `special`; cooldown state not tracked' },
    ability2: { name: 'Rendezvous', free: true, charges: 1, flag: 'redeploy cooldown ~30-45s not simulated here' },
    grenade: { name: 'Trademark', cost: 200, charges: 1, flag: '~30s cooldown to redeploy after trigger, not simulated here' },
    ultimateCost: 8,
  },
  Neon: {
    special: 'High Gear (ability2) runs on a 100-point energy meter (drains while sprinting/sliding, regenerates when idle, refills instantly on a kill) rather than discrete charges.',
    ability1: { name: 'Relay Bolt', cost: 200, charges: 2 },
    ability2: { name: 'High Gear', free: true, charges: 1, flag: 'energy-meter based -- see `special`, not modeled as simple charges' },
    grenade: { name: 'Fast Lane', cost: 300, charges: 1 },
    ultimateCost: 8,
  },
  Fade: {
    ability1: { name: 'Seize', cost: 200, charges: 1 },
    ability2: { name: 'Haunt', free: true, charges: 1, regen: { seconds: 45 }, flag: 'mid-round regen length reported as both 40s and 50s across sources' },
    grenade: { name: 'Prowler', cost: 250, charges: 2 },
    ultimateCost: 8,
  },
  Harbor: {
    ability1: { name: 'Cove', free: true, charges: 1, regen: { seconds: 35 }, flag: 'mid-round regen length reported as both 30s and 40s across sources' },
    ability2: { name: 'High Tide', cost: 300, charges: 1 },
    grenade: { name: 'Storm Surge', cost: 200, charges: 1, flag: 'reworked ability (replaced Cascade) -- some aggregator sites still show stale pre-rework numbers' },
    ultimateCost: 7,
  },
  Gekko: {
    special: 'Wingman, Mosh Pit and Dizzy each leave a "globule" on the ground for ~20s after use; walking up to reclaim it (then waiting a further ~15s reclaim cooldown) grants a free reuse without repurchasing. Not modeled here -- shown as a plain per-round charge.',
    ability1: { name: 'Wingman', cost: 300, charges: 1, flag: 'globule-reclaim mechanic -- see `special`' },
    ability2: { name: 'Dizzy', free: true, charges: 1, flag: 'globule-reclaim mechanic -- see `special`' },
    grenade: { name: 'Mosh Pit', cost: 250, charges: 1, flag: 'globule-reclaim mechanic -- see `special`' },
    ultimateCost: 8,
  },
  Deadlock: {
    ability1: { name: 'Sonic Sensor', cost: 200, charges: 2, flag: 'a placed sensor can be recalled and redeployed without spending a new charge -- not simulated here' },
    ability2: { name: 'GravNet', free: true, charges: 1, regen: { seconds: 50 } },
    grenade: { name: 'Barrier Mesh', cost: 300, charges: 1 },
    ultimateCost: 7,
  },
  Iso: {
    ability1: { name: 'Undercut', cost: 300, charges: 1 },
    ability2: { name: 'Double Tap', free: true, charges: 1, flag: 'has an orb-based shield-extend mechanic while active, not a simple recharge -- see agent research notes' },
    grenade: { name: 'Contingency', cost: 200, charges: 1 },
    ultimateCost: 7,
  },
  Clove: {
    ability1: { name: 'Meddle', cost: 250, charges: 1 },
    ability2: { name: 'Ruse', free: true, charges: 1, flag: '2nd charge purchasable for 150cr (only 1 charge max while dead); usable as a ghost after death, unlike Meddle/Pick-me-up' },
    grenade: { name: 'Pick-me-up', cost: 200, charges: 1, flag: 'only activatable within ~4.5s of Clove damaging/killing an enemy -- not a plain cooldown' },
    ultimateCost: 8,
  },
  Vyse: {
    ability1: { name: 'Shear', cost: 200, charges: 1 },
    ability2: { name: 'Arc Rose', free: true, charges: 1, regen: { seconds: 20 }, flag: 'in-round cooldown is 45s if the device was destroyed, ~20s if recalled -- shorter figure used here' },
    grenade: { name: 'Razorvine', cost: 150, charges: 2 },
    ultimateCost: 8,
  },
  Tejo: {
    ability1: { name: 'Special Delivery', cost: 300, charges: 1 },
    ability2: { name: 'Guided Salvo', free: true, charges: 1, regen: { seconds: 40 } },
    grenade: { name: 'Stealth Drone', cost: 300, charges: 1 },
    ultimateCost: 8,
  },
  Waylay: {
    ability1: { name: 'Lightspeed', cost: 300, charges: 1 },
    ability2: { name: 'Refract', free: true, charges: 1, regen: { kills: 2 } },
    grenade: { name: 'Saturate', cost: 300, charges: 1 },
    ultimateCost: 8,
  },
  Veto: {
    ability1: { name: 'Chokehold', cost: 200, charges: 1 },
    ability2: { name: 'Interceptor', free: true, charges: 1, regen: { seconds: 20 }, flag: '45s if destroyed vs ~20s if recalled -- shorter figure used here; ultimate cost (7) is lower than most agents and worth double-checking' },
    grenade: { name: 'Crosscut', cost: 200, charges: 2 },
    ultimateCost: 7,
  },
  Miks: {
    ability1: { name: 'Harmonize', cost: 200, charges: 1, flag: 'one source claims faster regen after a kill -- medium confidence, not simulated here' },
    ability2: { name: 'Waveform', cost: 100, charges: 2, flag: 'a conflicting source instead describes this as "1 free + 1 purchasable (~150cr)" -- unresolved, using the 100cr/2-charge reading' },
    grenade: { name: 'M-pulse', cost: 250, charges: 2 },
    ultimateCost: 8,
  },
};

// Newest agent this project has real ability text for is confirmed via AGENT_ABILITIES.md /
// catalog.js (valorant-api.com); this table's KEYS must match AgentName exactly as that produces
// it (real display names, e.g. "KAY/O" with the slash).
