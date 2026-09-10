using VrfInsights.Data;
using VrfInsights.Data.Manifest;

namespace VrfInsights.Analysis.Identity;

/// <param name="Subject">Account UUID. Null for a bot or an unresolved slot.</param>
/// <param name="ActorNetGuid">The player's BombPlayerState actor NetGUID.</param>
/// <param name="CharacterNetGuid">Join key into <c>movement.parquet</c> and any character-owned
/// rows in <c>fields.parquet</c>/<c>actors.parquet</c>. Null if the character was never spawned
/// (e.g. a very early disconnect) — see vrfkit's docs/DATA.md note on "last non-zero write
/// wins" for why a naive read can otherwise see 0 here.</param>
/// <param name="AgentName">Resolved via <see cref="AgentCatalog"/>; falls back to a labeled raw
/// id string if this build's agent isn't in the embedded table yet.</param>
/// <param name="CharacterId">The raw agent asset id, kept alongside <see cref="AgentName"/> in
/// case you need it for your own lookup.</param>
public sealed record PlayerIdentity(
    string? Subject,
    long ActorNetGuid,
    long? CharacterNetGuid,
    string AgentName,
    string? CharacterId,
    string? SkinId,
    IReadOnlyList<string> SprayIds);

/// <summary>Joins <c>manifest.players</c> (wire actor identity) to <c>game_specific_data</c>'s
/// <c>playerLoadouts</c> (account-keyed agent/cosmetic selection) on the account UUID
/// (<c>subject</c>) — the join vrfkit's own docs describe under "Player identity".</summary>
public static class PlayerRegistry
{
    public static IReadOnlyList<PlayerIdentity> Build(VrfExportSet export, AgentCatalog agentCatalog)
    {
        IReadOnlyList<PlayerLoadoutEntry> loadouts =
            GameSpecificDataAccessor.ExtractPlayerLoadouts(export.Manifest.GameSpecificData);

        Dictionary<string, PlayerLoadoutEntry> loadoutBySubject = new(StringComparer.OrdinalIgnoreCase);
        foreach (PlayerLoadoutEntry loadout in loadouts)
        {
            if (!string.IsNullOrEmpty(loadout.Subject))
            {
                loadoutBySubject[loadout.Subject] = loadout;
            }
        }

        var result = new List<PlayerIdentity>(export.Manifest.Players.Count);
        foreach (ManifestPlayer player in export.Manifest.Players)
        {
            PlayerLoadoutEntry? loadout = player.Subject is not null && loadoutBySubject.TryGetValue(player.Subject, out PlayerLoadoutEntry? found)
                ? found
                : null;

            result.Add(new PlayerIdentity(
                Subject: player.Subject,
                ActorNetGuid: player.ActorNetGuid,
                CharacterNetGuid: player.CharacterNetGuid is > 0 ? player.CharacterNetGuid : null,
                AgentName: agentCatalog.Resolve(loadout?.CharacterId),
                CharacterId: loadout?.CharacterId,
                SkinId: loadout?.SkinId,
                SprayIds: loadout?.SprayIds ?? Array.Empty<string>()));
        }

        return result;
    }
}
