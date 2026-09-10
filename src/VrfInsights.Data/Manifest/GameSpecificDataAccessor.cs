using System.Text.Json;

namespace VrfInsights.Data.Manifest;

/// <param name="Subject">Account UUID — joins to <see cref="ManifestPlayer.Subject"/>.</param>
/// <param name="CharacterId">The agent's identifier as VALORANT's own client reports it (an
/// asset GUID in every sample vrfkit's docs describe). Resolve to a display name with
/// <c>VrfInsights.Analysis.Identity.AgentCatalog</c>.</param>
/// <param name="SkinId">Weapon skin identifier for the player's loadout, if present in this
/// replay's blob.</param>
/// <param name="SprayIds">Equipped spray identifiers, if present.</param>
public sealed record PlayerLoadoutEntry(
    string? Subject,
    string? CharacterId,
    string? SkinId,
    IReadOnlyList<string> SprayIds);

/// <summary>
/// <see cref="ReplayManifest.GameSpecificData"/> is a list of raw JSON strings that vrfkit
/// copies through verbatim from the replay header (it deliberately does not parse or
/// re-serialize a document it did not author — see vrfkit's <c>manifest.rs</c>). This class does
/// the one nested parse a consumer needs to recover <c>playerLoadouts</c> from whichever entry
/// contains it.
///
/// <para>The inner document is VALORANT's own client JSON, not something vrfkit or this project
/// defines — its exact key casing is asserted defensively here (a small set of plausible
/// spellings per field) rather than assumed from a single sample. If a replay from your own
/// client uses different keys, widen the candidate lists below; nothing else in this project
/// depends on the exact casing.</para>
/// </summary>
public static class GameSpecificDataAccessor
{
    private static readonly string[] PlayerLoadoutsKeyCandidates = { "playerLoadouts", "PlayerLoadouts" };
    private static readonly string[] SubjectKeyCandidates = { "subject", "Subject", "playerId", "PlayerId", "playerID" };
    private static readonly string[] CharacterIdKeyCandidates = { "characterId", "CharacterId", "character", "Character" };
    private static readonly string[] SkinKeyCandidates = { "skinId", "SkinId", "skin", "Skin" };
    private static readonly string[] SprayKeyCandidates = { "sprays", "Sprays", "spraySelections", "SpraySelections" };

    /// <summary>Scans every entry in <paramref name="gameSpecificData"/> for one that parses as
    /// JSON and contains a <c>playerLoadouts</c> array, and returns it flattened into records.
    /// Returns an empty list (never throws) if no entry matches — callers should treat that as
    /// "loadouts unavailable for this replay" rather than a hard error.</summary>
    public static IReadOnlyList<PlayerLoadoutEntry> ExtractPlayerLoadouts(IEnumerable<string> gameSpecificData)
    {
        foreach (string raw in gameSpecificData)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                if (TryFindProperty(doc.RootElement, PlayerLoadoutsKeyCandidates, out JsonElement loadouts) &&
                    loadouts.ValueKind == JsonValueKind.Array)
                {
                    return ParseLoadoutArray(loadouts);
                }
            }
        }

        return Array.Empty<PlayerLoadoutEntry>();
    }

    private static List<PlayerLoadoutEntry> ParseLoadoutArray(JsonElement array)
    {
        var results = new List<PlayerLoadoutEntry>();
        foreach (JsonElement entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? subject = TryFindProperty(entry, SubjectKeyCandidates, out JsonElement s) ? AsString(s) : null;
            string? characterId = TryFindProperty(entry, CharacterIdKeyCandidates, out JsonElement c) ? AsString(c) : null;
            string? skin = TryFindProperty(entry, SkinKeyCandidates, out JsonElement sk) ? AsString(sk) : null;

            var sprays = new List<string>();
            if (TryFindProperty(entry, SprayKeyCandidates, out JsonElement sprayEl) && sprayEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement sprayItem in sprayEl.EnumerateArray())
                {
                    string? sprayValue = AsString(sprayItem);
                    if (sprayValue is not null)
                    {
                        sprays.Add(sprayValue);
                    }
                }
            }

            results.Add(new PlayerLoadoutEntry(subject, characterId, skin, sprays));
        }

        return results;
    }

    private static bool TryFindProperty(JsonElement obj, IReadOnlyList<string> candidates, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (string candidate in candidates)
            {
                if (obj.TryGetProperty(candidate, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? AsString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.ToString(),
        _ => null
    };
}
