using VrfInsights.Analysis.Rounds;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Abilities;

/// <param name="CharacterNetGuid">Resolves via <see cref="VrfInsights.Analysis.Identity.PlayerIdentity.CharacterNetGuid"/>.
/// Present only when vrfkit's structural overlay matched for this row — see remarks.</param>
public sealed record UltimateUsageEvent(long TimeMs, long? CharacterNetGuid, int? RoundNumber);

/// <summary>
/// Surfaces <c>events.characterUltimateUsed</c> as an ultimate-cast signal. vrfkit's own
/// measurement found these Event rows outnumber the underlying "UltimateActive" false→true
/// state transition by about 51.5% — i.e. this table is a reasonable <b>indicator</b> of
/// ultimate usage and a good cross-check, but on its own it will overcount actual casts. This
/// project surfaces it as-is (rather than chasing the harder, more precise "UltimateActive
/// transition in fields.parquet" signal, whose exact group/field name wasn't independently
/// confirmed here) — treat counts from this as an upper bound.
/// </summary>
public static class UltimateUsageBuilder
{
    public static IReadOnlyList<UltimateUsageEvent> Build(IReadOnlyList<EventRow> events, IReadOnlyList<RoundInfo> rounds)
    {
        var results = new List<UltimateUsageEvent>();
        foreach (EventRow ev in events)
        {
            if (ev.Group != "characterUltimateUsed")
            {
                continue;
            }

            int? roundNumber = null;
            foreach (RoundInfo round in rounds)
            {
                if (round.Contains(ev.Time1))
                {
                    roundNumber = round.RoundNumber;
                    break;
                }
            }

            results.Add(new UltimateUsageEvent(ev.Time1, ev.Word0, roundNumber));
        }

        results.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return results;
    }
}
