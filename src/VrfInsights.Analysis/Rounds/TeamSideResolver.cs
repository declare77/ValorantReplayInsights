using VrfInsights.Analysis.Identity;
using VrfInsights.Data;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Rounds;

/// <summary>Attacking/defending roster for one round, keyed by <see cref="PlayerIdentity.ActorNetGuid"/>
/// (always present, unlike <see cref="PlayerIdentity.Subject"/>) -- see <see cref="TeamSideResolver"/>
/// for how this is derived and when it's left out entirely.</summary>
public sealed record RoundSides(int RoundNumber, IReadOnlyList<long> AttackingActorNetGuids, IReadOnlyList<long> DefendingActorNetGuids);

/// <summary>
/// Figures out, per round, which players were attacking and which were defending -- so a viewer
/// can color players by side instead of one arbitrary color per player.
///
/// There is no single field anywhere in vrfkit's tables that says "this player is attacking this
/// round" (checked against vrfkit's own docs/DATA.md -- nothing marked verified there covers
/// per-player side). So this combines two things that ARE independently verified/derivable,
/// deliberately avoiding a repeat of this project's earlier mistake of inventing an unverified
/// per-map heuristic (see README's map-orientation section):
///
///  1. Team ROSTER (which players are on the same team) never changes during a match -- only
///     which side of the map (and therefore attack/defense role) a team plays changes, at
///     halftime/overtime (<c>events.switchTeams</c>, vrfkit-verified per docs/DATA.md). The two
///     teams' round-1 spawn points are always in the two separate spawn rooms at opposite ends of
///     the map, so a simple nearest-of-two-seeds split on round-1 spawn position reliably recovers
///     the grouping without needing any per-map calibration data.
///
///  2. Spike CUSTODY (<c>BombEquippable_C.Owner</c>, the same signal vrfkit's own
///     <c>tools/extract_spike_carrier.py</c> uses to find the planter -- see docs/DATA.md's
///     "Spike carrier" and "Planter" rows) is direct, verified evidence of which player held the
///     bomb during a round -- and only attackers can ever hold it. Any Owner-write during a round
///     that resolves to one of the players tells us that player's whole roster group was
///     attacking that round.
///
/// If neither signal is available (e.g. very little of the replay decoded, no round-1 spawn data,
/// or no bomb pickups anywhere in the export), this returns an empty list rather than guessing --
/// callers should treat an empty list as "team sides unknown" and fall back to per-player coloring.
/// </summary>
public static class TeamSideResolver
{
    private const string BombEquippableClassMarker = "BombEquippable_C";

    public static IReadOnlyList<RoundSides> Build(
        VrfExportSet export,
        IReadOnlyList<PlayerIdentity> players,
        IReadOnlyList<RoundInfo> rounds,
        IReadOnlyList<MatchEvent> events)
    {
        List<PlayerIdentity> roster = players.Where(p => p.CharacterNetGuid is not null).ToList();
        if (roster.Count < 4 || rounds.Count == 0)
        {
            return Array.Empty<RoundSides>();
        }

        Dictionary<long, (double X, double Y)>? spawns = FindRoundOneSpawns(export.Actors, roster);
        if (spawns is null || spawns.Count != roster.Count)
        {
            return Array.Empty<RoundSides>();
        }

        (HashSet<long> teamA, HashSet<long> teamB) = SplitIntoTwoTeams(spawns);

        var isTeamA = new Dictionary<long, bool>();
        foreach (long guid in teamA) isTeamA[guid] = true;
        foreach (long guid in teamB) isTeamA[guid] = false;

        var knownCharacterGuids = new HashSet<long>(roster.Select(p => p.CharacterNetGuid!.Value));
        Dictionary<long, long> characterToActorNetGuid = roster.ToDictionary(p => p.CharacterNetGuid!.Value, p => p.ActorNetGuid);

        Dictionary<int, long> carrierEvidenceByRound = FindCarrierEvidencePerRound(export, rounds, knownCharacterGuids);

        // Half boundaries: attack/defense flips at every `switchTeams` event (halftime, and each
        // overtime side-swap) -- vrfkit-verified per docs/DATA.md. A round's "half index" is how
        // many switchTeams events preceded its start.
        List<long> switchTimes = events.Where(e => e.Group == "switchTeams").Select(e => e.TimeMs).OrderBy(t => t).ToList();

        // For each half, which team (A/B) held carrier evidence -- majority vote across that
        // half's rounds, so one mis-resolved pickup can't flip the whole half.
        var votesForA = new Dictionary<int, int>();
        var votesForB = new Dictionary<int, int>();
        foreach (RoundInfo round in rounds)
        {
            int half = HalfIndex(round.StartTimeMs, switchTimes);
            if (carrierEvidenceByRound.TryGetValue(round.RoundNumber, out long carrierCharGuid) &&
                isTeamA.TryGetValue(carrierCharGuid, out bool carrierIsTeamA))
            {
                if (carrierIsTeamA) votesForA[half] = votesForA.GetValueOrDefault(half) + 1;
                else votesForB[half] = votesForB.GetValueOrDefault(half) + 1;
            }
        }

        var attackerIsTeamAByHalf = new Dictionary<int, bool>();
        foreach (int half in votesForA.Keys.Concat(votesForB.Keys).Distinct())
        {
            int votesA = votesForA.GetValueOrDefault(half);
            int votesB = votesForB.GetValueOrDefault(half);
            if (votesA != votesB) attackerIsTeamAByHalf[half] = votesA > votesB;
        }

        if (attackerIsTeamAByHalf.Count == 0)
        {
            // No round anywhere in the replay produced usable carrier evidence -- nothing to
            // anchor a side assignment to, so don't guess.
            return Array.Empty<RoundSides>();
        }

        // Fill in halves with no direct evidence of their own: sides always alternate, so borrow
        // from a neighboring resolved half and flip.
        int maxHalf = rounds.Select(r => HalfIndex(r.StartTimeMs, switchTimes)).DefaultIfEmpty(0).Max();
        for (int half = 1; half <= maxHalf; half++)
        {
            if (!attackerIsTeamAByHalf.ContainsKey(half) && attackerIsTeamAByHalf.TryGetValue(half - 1, out bool prevA))
            {
                attackerIsTeamAByHalf[half] = !prevA;
            }
        }
        for (int half = maxHalf - 1; half >= 0; half--)
        {
            if (!attackerIsTeamAByHalf.ContainsKey(half) && attackerIsTeamAByHalf.TryGetValue(half + 1, out bool nextA))
            {
                attackerIsTeamAByHalf[half] = !nextA;
            }
        }

        long[] teamAActorGuids = teamA.Select(g => characterToActorNetGuid[g]).ToArray();
        long[] teamBActorGuids = teamB.Select(g => characterToActorNetGuid[g]).ToArray();

        var result = new List<RoundSides>(rounds.Count);
        foreach (RoundInfo round in rounds)
        {
            int half = HalfIndex(round.StartTimeMs, switchTimes);
            if (!attackerIsTeamAByHalf.TryGetValue(half, out bool attackerIsA))
            {
                continue; // this half never got resolved even after propagation -- skip it honestly
            }

            result.Add(attackerIsA
                ? new RoundSides(round.RoundNumber, teamAActorGuids, teamBActorGuids)
                : new RoundSides(round.RoundNumber, teamBActorGuids, teamAActorGuids));
        }

        return result;
    }

    private static int HalfIndex(long timeMs, List<long> switchTimes)
    {
        int half = 0;
        foreach (long t in switchTimes)
        {
            if (timeMs >= t) half++;
            else break;
        }

        return half;
    }

    /// <summary>Each player's spawn position at the very start of the replay (their character
    /// pawn's first `open` in <c>actors.parquet</c>) -- the two teams' round-1 spawn rooms are
    /// always far apart, which is what makes the split below reliable without any per-map data.</summary>
    private static Dictionary<long, (double X, double Y)>? FindRoundOneSpawns(IReadOnlyList<ActorRow> actors, List<PlayerIdentity> roster)
    {
        var earliestOpen = new Dictionary<long, ActorRow>();
        foreach (ActorRow row in actors)
        {
            if (!row.IsOpen || row.SpawnX is null || row.SpawnY is null) continue;
            if (!earliestOpen.TryGetValue(row.ActorNetGuid, out ActorRow? existing) || row.TimeMs < existing.TimeMs)
            {
                earliestOpen[row.ActorNetGuid] = row;
            }
        }

        var result = new Dictionary<long, (double, double)>();
        foreach (PlayerIdentity player in roster)
        {
            long guid = player.CharacterNetGuid!.Value;
            if (earliestOpen.TryGetValue(guid, out ActorRow? spawn))
            {
                result[guid] = (spawn.SpawnX!.Value, spawn.SpawnY!.Value);
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>Splits into two equal-ish groups by 2D distance: seed on the two mutually
    /// farthest-apart points (the two spawn rooms), assign everyone else to the nearer seed, then
    /// rebalance so both groups land at the same size (guards against a rare near-tie).</summary>
    private static (HashSet<long> A, HashSet<long> B) SplitIntoTwoTeams(Dictionary<long, (double X, double Y)> spawns)
    {
        List<long> guids = spawns.Keys.ToList();
        long seedA = guids[0], seedB = guids[0];
        double best = -1;
        for (int i = 0; i < guids.Count; i++)
        {
            for (int j = i + 1; j < guids.Count; j++)
            {
                double d = Dist2(spawns[guids[i]], spawns[guids[j]]);
                if (d > best) { best = d; seedA = guids[i]; seedB = guids[j]; }
            }
        }

        var teamA = new HashSet<long>();
        var teamB = new HashSet<long>();
        var marginToB = new Dictionary<long, double>(); // distToB - distToA; larger = "more clearly A"
        foreach (long g in guids)
        {
            double distA = Dist2(spawns[g], spawns[seedA]);
            double distB = Dist2(spawns[g], spawns[seedB]);
            marginToB[g] = distB - distA;
            if (distA <= distB) teamA.Add(g); else teamB.Add(g);
        }

        int targetA = guids.Count / 2;
        while (teamA.Count > targetA)
        {
            long move = teamA.OrderBy(g => marginToB[g]).First(); // least confidently "A"
            teamA.Remove(move);
            teamB.Add(move);
        }

        while (teamA.Count < targetA)
        {
            long move = teamB.OrderByDescending(g => marginToB[g]).First(); // least confidently "B"
            teamB.Remove(move);
            teamA.Add(move);
        }

        return (teamA, teamB);
    }

    private static double Dist2((double X, double Y) a, (double X, double Y) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>For each round, one resolved bomb-carrier character NetGUID seen during that
    /// round's time window (the first one found is enough -- we only need to know which side is
    /// attacking, not the full custody history). Mirrors vrfkit's own
    /// <c>tools/extract_spike_carrier.py</c>: an Owner-field write on a <c>BombEquippable_C</c>
    /// actor names the carrying pawn directly, or (Gekko's Wingman and similar proxy actors) via
    /// that actor's own <c>Instigator</c> field back to the spawning pawn.</summary>
    private static Dictionary<int, long> FindCarrierEvidencePerRound(VrfExportSet export, IReadOnlyList<RoundInfo> rounds, HashSet<long> knownCharacterGuids)
    {
        var classByActor = new Dictionary<long, string>();
        foreach (ActorRow row in export.Actors)
        {
            if (row.ClassPath is not null && !classByActor.ContainsKey(row.ActorNetGuid))
            {
                classByActor[row.ActorNetGuid] = row.ClassPath;
            }
        }

        var instigatorByActor = new Dictionary<long, long>();
        foreach (FieldRow row in export.Fields)
        {
            if (row.FieldName == "Instigator" && row.ValueI64 is > 0)
            {
                instigatorByActor[row.ActorNetGuid] = row.ValueI64.Value;
            }
        }

        var result = new Dictionary<int, long>();
        foreach (FieldRow row in export.Fields)
        {
            if (row.FieldName != "Owner" || row.ValueI64 is not > 0) continue;
            if (!classByActor.TryGetValue(row.ActorNetGuid, out string? actorClass) ||
                !actorClass.Contains(BombEquippableClassMarker, StringComparison.Ordinal))
            {
                continue;
            }

            long ownerGuid = row.ValueI64!.Value;
            long? carrierCharGuid = knownCharacterGuids.Contains(ownerGuid)
                ? ownerGuid
                : (instigatorByActor.TryGetValue(ownerGuid, out long instigator) && knownCharacterGuids.Contains(instigator) ? instigator : null);
            if (carrierCharGuid is not long resolved) continue;

            foreach (RoundInfo round in rounds)
            {
                if (round.Contains(row.TimeMs) && !result.ContainsKey(round.RoundNumber))
                {
                    result[round.RoundNumber] = resolved;
                    break;
                }
            }
        }

        return result;
    }
}
