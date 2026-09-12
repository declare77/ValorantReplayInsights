using System.Globalization;
using VrfInsights.Analysis.Common;
using VrfInsights.Analysis.Identity;
using VrfInsights.Analysis.Weapons;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Combat;

/// <summary>
/// Extracts per-hit events from <c>fields.parquet</c>'s <c>MulticastNotifyDamage_Point</c> RPC —
/// vrfkit's own per-landed-hit signal (its docs mark this ✅, the same confidence tier as the
/// combat-report fix and unlike <see cref="Weapons.ShotFiredEvent"/>'s speculative shot-effect
/// approach, which this project tried first and which didn't produce visible output for at least
/// one real replay).
///
/// <para><b>Confirmed by reading vrfkit's own source</b>
/// (<c>crates/vrf-decode/src/table.rs</c>'s field table for <c>MulticastNotifyDamage_Point</c>,
/// <c>crates/vrfkit/src/sink/rpc.rs</c>'s field-naming code, <c>docs/DATA.md</c>): this RPC is
/// declared on <c>DamageableComponent</c> — the health-tracking component on the character taking
/// damage — with <c>FieldName</c> built as <c>"MulticastNotifyDamage_Point.&lt;ParamName&gt;"</c>
/// and NO vrfkit disambiguation suffix (unlike <c>AbilityCastsThisRound</c>'s members — confirmed
/// by reading the field-naming code directly), so this filters/matches by exact name rather than
/// prefix. <c>GroupPath</c> is the enclosing <c>DamageableComponent_ClassNetCache</c> group.</para>
///
/// <para><b>What's strongly evidenced but not a verbatim vrfkit statement:</b> that
/// <c>ActorNetGuid</c> on these rows identifies the VICTIM, not the attacker — inferred by analogy
/// with vrfkit's own healing-observation tooling, which explicitly labels the equivalent field on
/// the sibling heal RPC "recipient" (same component, same mechanism). <b>What's a genuine, resolved
/// ambiguity:</b> which of five raw reference fields (<c>DamageCauser</c>, <c>EventInstigator</c>,
/// <c>EventInstigatorPawn</c>, <c>DamagerPlayerState</c>, <c>KillCreditPlayerState</c>) most
/// reliably identifies the attacker — vrfkit's own docs flag <c>EventInstigator</c> specifically as
/// "an opaque packed reference candidate: its target type has not been established," so this tries
/// them in a preference order (see <see cref="TryResolveAttacker"/>) rather than trusting one
/// blindly, and keeps every candidate on the event (see <see cref="AttackerCandidateSet"/>) so a
/// wrong pick can be diagnosed and fixed without re-running the analyzer.</para>
///
/// <para><b>What's a documented assumption by analogy, not confirmed for this specific field:</b>
/// that <c>DamageImpactLocation</c>/<c>DamageDirection</c> (both vector-typed) decode to the same
/// bare <c>"(X,Y,Z)"</c> string format this project already confirmed for <c>CastLocation</c> (see
/// <c>AbilityCastBuilder</c>'s doc comment) — plausible since vrfkit decodes both as reconstructed
/// float vectors, but not independently checked via <c>dump-values</c> for these two fields.</para>
/// </summary>
public static class DamageHitBuilder
{
    private const string GroupPathMarker = "DamageableComponent";
    private const string FunctionPrefix = "MulticastNotifyDamage_Point.";

    public static IReadOnlyList<DamageHitEvent> Build(
        IReadOnlyList<FieldRow> fields,
        IReadOnlyList<ActorRow> actors,
        IReadOnlyList<PlayerIdentity> players,
        WeaponCatalog weaponCatalog)
    {
        Dictionary<long, string> classPathByActor = BuildClassPathIndex(actors);

        var groups = new Dictionary<(long Victim, long Channel, long Packet, long TimeMs), Dictionary<string, FieldRow>>();
        foreach (FieldRow row in fields)
        {
            if (row.FieldName is null ||
                !row.FieldName.StartsWith(FunctionPrefix, StringComparison.Ordinal) ||
                !row.GroupPath.Contains(GroupPathMarker, StringComparison.Ordinal))
            {
                continue;
            }

            string param = row.FieldName[FunctionPrefix.Length..];
            var key = (row.ActorNetGuid, row.ChannelIndex, row.PacketId, row.TimeMs);
            if (!groups.TryGetValue(key, out Dictionary<string, FieldRow>? members))
            {
                members = new Dictionary<string, FieldRow>();
                groups[key] = members;
            }

            members[param] = row;
        }

        var results = new List<DamageHitEvent>(groups.Count);
        foreach (KeyValuePair<(long Victim, long Channel, long Packet, long TimeMs), Dictionary<string, FieldRow>> kvp in groups)
        {
            Dictionary<string, FieldRow> m = kvp.Value;

            var candidates = new AttackerCandidateSet(
                DamageCauser: GetObjectRef(m, "DamageCauser"),
                EventInstigator: GetObjectRef(m, "EventInstigator"),
                EventInstigatorPawn: GetObjectRef(m, "EventInstigatorPawn"),
                DamagerPlayerState: GetObjectRef(m, "DamagerPlayerState"),
                KillCreditPlayerState: GetObjectRef(m, "KillCreditPlayerState"));

            long? attacker = TryResolveAttacker(candidates, players);

            long? equippableGuid = GetObjectRef(m, "EquippableUsed");
            string? weaponClassPath = equippableGuid.HasValue && classPathByActor.TryGetValue(equippableGuid.Value, out string? cp) ? cp : null;
            string? weaponName = weaponCatalog.Resolve(weaponClassPath)?.DisplayName;

            results.Add(new DamageHitEvent(
                VictimActorNetGuid: kvp.Key.Victim,
                TimeMs: kvp.Key.TimeMs,
                DamageDealt: GetDouble(m, "DamageDealt"),
                DamageTaken: GetDouble(m, "DamageTaken"),
                DeltaLife: GetDouble(m, "DeltaLife"),
                BDamageKilledTarget: GetBool(m, "bDamageKilledTarget"),
                AttackerActorNetGuid: attacker,
                AttackerCandidates: candidates,
                WeaponClassPath: weaponClassPath,
                WeaponDisplayName: weaponName,
                ImpactLocation: GetVector(m, "DamageImpactLocation"),
                ImpactDirection: GetVector(m, "DamageDirection"),
                DamagedBone: GetString(m, "DamagedBone"),
                IsWallPenetration: GetBool(m, "bIsWallPenetration")));
        }

        results.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return results;
    }

    /// <summary>Tries each attacker-reference candidate in a preference order, matching against
    /// known players' <c>ActorNetGuid</c> (PlayerState-style references) or
    /// <c>CharacterNetGuid</c> (pawn-style references) as appropriate for that field's evident
    /// meaning. Returns the first candidate that resolves to a known player -- not necessarily the
    /// "correct" one in every case, since this is exactly the ambiguity this class's doc comment
    /// describes.</summary>
    private static long? TryResolveAttacker(AttackerCandidateSet c, IReadOnlyList<PlayerIdentity> players)
    {
        var byActorGuid = new HashSet<long>();
        var byCharacterGuid = new Dictionary<long, long>(); // characterGuid -> actorGuid
        foreach (PlayerIdentity p in players)
        {
            byActorGuid.Add(p.ActorNetGuid);
            if (p.CharacterNetGuid.HasValue)
            {
                byCharacterGuid[p.CharacterNetGuid.Value] = p.ActorNetGuid;
            }
        }

        // DamagerPlayerState / KillCreditPlayerState are, by name, PlayerState references -- the
        // same kind of reference ManifestPlayer.ActorNetGuid already is ("the BombPlayerState
        // actor's own NetGUID") -- so these are tried first as direct ActorNetGuid matches.
        if (c.DamagerPlayerState is long dps && byActorGuid.Contains(dps)) return dps;
        if (c.KillCreditPlayerState is long kcps && byActorGuid.Contains(kcps)) return kcps;

        // EventInstigatorPawn / DamageCauser read as pawn (character) references by name --
        // tried against CharacterNetGuid, falling back to a direct ActorNetGuid match in case a
        // given replay's build routes them differently.
        if (c.EventInstigatorPawn is long eip)
        {
            if (byCharacterGuid.TryGetValue(eip, out long a1)) return a1;
            if (byActorGuid.Contains(eip)) return eip;
        }
        if (c.DamageCauser is long dc)
        {
            if (byCharacterGuid.TryGetValue(dc, out long a2)) return a2;
            if (byActorGuid.Contains(dc)) return dc;
        }

        // EventInstigator last -- vrfkit's own docs flag this one specifically as an opaque
        // reference whose target type isn't established, so it's the least trustworthy candidate.
        if (c.EventInstigator is long ei)
        {
            if (byCharacterGuid.TryGetValue(ei, out long a3)) return a3;
            if (byActorGuid.Contains(ei)) return ei;
        }

        return null;
    }

    private static Dictionary<long, string> BuildClassPathIndex(IReadOnlyList<ActorRow> actors)
    {
        var byActor = new Dictionary<long, string>();
        foreach (ActorRow row in actors)
        {
            if (row.ClassPath is not null && !byActor.ContainsKey(row.ActorNetGuid))
            {
                byActor[row.ActorNetGuid] = row.ClassPath;
            }
        }

        return byActor;
    }

    private static double? GetDouble(Dictionary<string, FieldRow> m, string name) =>
        m.TryGetValue(name, out FieldRow? row) ? row.Value switch { double d => d, long l => l, _ => (double?)null } : null;

    private static bool? GetBool(Dictionary<string, FieldRow> m, string name) =>
        m.TryGetValue(name, out FieldRow? row) ? row.Value as bool? : null;

    private static string? GetString(Dictionary<string, FieldRow> m, string name) =>
        m.TryGetValue(name, out FieldRow? row) ? row.Value as string : null;

    /// <summary>ObjectNetGuid-typed fields decode straight to <see cref="FieldRow.ValueI64"/> --
    /// the same convention already established and used elsewhere in this project for reading
    /// e.g. <c>BombEquippable_C.Owner</c> (see <c>TeamSideResolver</c>).</summary>
    private static long? GetObjectRef(Dictionary<string, FieldRow> m, string name) =>
        m.TryGetValue(name, out FieldRow? row) && row.ValueI64 is > 0 ? row.ValueI64 : null;

    /// <summary>Parses vrfkit's bare <c>(X,Y,Z)</c> vector-string format -- the same format
    /// confirmed for <c>CastLocation</c> via <c>dump-values</c> (see <c>AbilityCastBuilder</c>),
    /// assumed (not independently confirmed) to also apply to this RPC's vector-typed parameters.
    /// Returns null for anything that doesn't match, rather than throwing.</summary>
    private static Vec3? GetVector(Dictionary<string, FieldRow> m, string name)
    {
        if (!m.TryGetValue(name, out FieldRow? row) || row.Value is not string raw || string.IsNullOrEmpty(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')')
        {
            trimmed = trimmed[1..^1];
        }

        string[] parts = trimmed.Split(',');
        if (parts.Length != 3)
        {
            return null;
        }

        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            return new Vec3(x, y, z);
        }

        return null;
    }
}
