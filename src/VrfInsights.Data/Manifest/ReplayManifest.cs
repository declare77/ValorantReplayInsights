using System.Text.Json.Serialization;

namespace VrfInsights.Data.Manifest;

/// <summary>
/// Deserializes the top-level shape of vrfkit's <c>manifest.json</c> (see vrfkit's
/// <c>crates/vrfkit/src/manifest.rs</c> and docs/USAGE.md, section "manifest.json").
///
/// <para>Two things worth knowing when extending this:</para>
/// <list type="bullet">
/// <item><see cref="GameSpecificData"/> is an array of raw JSON <b>strings</b> copied verbatim
/// from the replay's own header — vrfkit deliberately does not parse or re-serialize them, to
/// avoid silently altering a document it didn't author. Use
/// <see cref="GameSpecificDataAccessor"/> to pull <c>playerLoadouts</c> out of whichever entry
/// contains it.</item>
/// <item><see cref="NetFieldExportGroups"/> and <c>quality</c> are left as raw
/// <see cref="System.Text.Json.JsonElement"/> rather than fully modeled — they're diagnostic /
/// completeness-accounting data this project doesn't currently need structured.</item>
/// </list>
/// </summary>
public sealed class ReplayManifest
{
    [JsonPropertyName("source_file")]
    public string? SourceFile { get; set; }

    [JsonPropertyName("source_size_bytes")]
    public long SourceSizeBytes { get; set; }

    [JsonPropertyName("replay_build")]
    public string? ReplayBuild { get; set; }

    [JsonPropertyName("replay_version")]
    public string? ReplayVersion { get; set; }

    [JsonPropertyName("replay_changelist")]
    public long ReplayChangelist { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("friendly_name")]
    public string? FriendlyName { get; set; }

    [JsonPropertyName("is_live")]
    public bool IsLive { get; set; }

    /// <summary>UE FDateTime — 100-nanosecond ticks since 0001-01-01. NOT a Windows FILETIME
    /// (interpreting it as one produces a date around the year 3626).</summary>
    [JsonPropertyName("timestamp_ticks")]
    public long TimestampTicks { get; set; }

    /// <summary>Raw JSON documents copied verbatim from the replay header. One of these
    /// (commonly index 1) contains the match roster under a <c>playerLoadouts</c> key — see
    /// <see cref="GameSpecificDataAccessor"/>.</summary>
    [JsonPropertyName("game_specific_data")]
    public List<string> GameSpecificData { get; set; } = new();

    /// <summary>Bridges wire actor GUIDs to stable account identity. Each entry is one
    /// BombPlayerState actor.</summary>
    [JsonPropertyName("players")]
    public List<ManifestPlayer> Players { get; set; } = new();
}

/// <param name="ActorNetGuid">The BombPlayerState actor's own NetGUID.</param>
/// <param name="Subject">Account UUID — the stable identity to join everything on, especially
/// when two players picked the same agent (characterId alone can't disambiguate them).</param>
/// <param name="CharacterNetGuid">The player's <c>SpawnedCharacter</c> NetGUID — this is exactly
/// <see cref="Tables.MovementRow.CharacterNetGuid"/>, so movement/fields/actors all join to a
/// player through this.</param>
public sealed record ManifestPlayer(
    [property: JsonPropertyName("actor_net_guid")] long ActorNetGuid,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("character_net_guid")] long? CharacterNetGuid);
