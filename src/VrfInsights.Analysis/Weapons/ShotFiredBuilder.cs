using System.Text.Json;
using VrfInsights.Data.Tables;

namespace VrfInsights.Analysis.Weapons;

/// <summary>
/// Extracts per-shot events from <c>fields.parquet</c>'s
/// <c>ReplayPlayContinuousEffectAtLocation</c> RPC parameter rows.
///
/// <para><b>How this was found, since nothing here has been checked against a real export yet:</b>
/// vrfkit's own source (<c>crates/vrfkit/src/sink/rpc.rs</c>, <c>crates/vrf-decode/src/effect.rs</c>,
/// <c>crates/vrf-decode/src/effect/json.rs</c>, <c>docs/DATA.md</c>, <c>docs/USAGE.md</c>, and its
/// reference downstream converter <c>tools/to_valplay_bundle.py</c>) documents that every weapon
/// shot arrives as a <c>ReplayPlayContinuousEffectAtLocation</c> RPC carrying three parameter
/// arrays — <c>FloatValues</c>, <c>ObjectValues</c>, <c>VectorValues</c> — each of which lands as
/// its own row in <c>fields.parquet</c> with <c>FieldName</c> exactly
/// <c>"ReplayPlayContinuousEffectAtLocation.FloatValues"</c> (etc., with NO vrfkit disambiguation
/// suffix — unlike <c>AbilityCastsThisRound</c>'s members, confirmed by reading
/// <c>sink/rpc.rs</c>'s field-naming code directly) and <c>GroupPath</c> set to the *enclosing*
/// <c>_ClassNetCache</c> group (not useful for filtering on its own — filter on <c>FieldName</c>
/// instead). Each row's decoded value (<c>ValueStr</c>) is itself a JSON array of
/// <c>{"tag": &lt;handle&gt;, "value": ...}</c> pairs — <c>tag</c> is a replay-specific
/// gameplay-tag handle that must be resolved to a real name (like
/// <c>"FiringState.AmmoRemaining"</c>) via <see cref="GameplayTagTable"/>, not a fixed array
/// index.</para>
///
/// <para><b>What's genuinely unconfirmed:</b> whether <c>ActorNetGuid</c> on these rows identifies
/// the firing player, their weapon actor, or something else in the ownership chain; the exact
/// JSON shape of a <c>value</c> for an object or vector element (this parses defensively — a
/// 3-element array or an <c>{x,y,z}</c>/<c>{X,Y,Z}</c> object for vectors, a number or numeric
/// string for objects/floats — rather than assuming one specific shape); and whether two shots
/// from the same actor can land in the same network packet (this builder's grouping key — actor +
/// channel + packet + time — would collide two such shots into one event; see
/// <see cref="Build"/>). None of this changes the never-touch-a-<c>.vrf</c>-file rule: everything
/// here reads only vrfkit's own already-decoded <c>fields.parquet</c>/<c>manifest.json</c>
/// output.</para>
/// </summary>
public static class ShotFiredBuilder
{
    private const string FloatValuesField = "ReplayPlayContinuousEffectAtLocation.FloatValues";
    private const string ObjectValuesField = "ReplayPlayContinuousEffectAtLocation.ObjectValues";
    private const string VectorValuesField = "ReplayPlayContinuousEffectAtLocation.VectorValues";

    private const string AmmoRemainingTag = "FiringState.AmmoRemaining";
    private const string NumProjectilesTag = "FiringState.NumProjectiles";
    private const string RandomSeedTag = "FiringState.RandomSeed";
    private const string TracerOptionTag = "FiringState.TracerOption";
    private const string BurstShotNumberTag = "FiringState.BurstShotNumber";
    private const string FiringPlayerStateTag = "FiringState.FiringPlayerState";
    private const string FiringStateTag = "FiringState.FiringState";
    private const string AttackVectorTagPrefix = "FiringState.AttackVector.";

    /// <summary>
    /// Groups the three per-invocation blob rows by (actor, channel, packet, time) as the best
    /// available proxy for "same RPC call" — none of these rows carry an explicit call-sequence
    /// id, so two genuinely distinct shots from the same actor that happened to land in the same
    /// network packet at the same millisecond would collide into a single event here. Documented
    /// as a known limitation rather than something to guess a fix for without real data.
    /// </summary>
    public static IReadOnlyList<ShotFiredEvent> Build(IReadOnlyList<FieldRow> fields, GameplayTagTable tagTable)
    {
        var groups = new Dictionary<(long Actor, long Channel, long Packet, long TimeMs), Dictionary<string, FieldRow>>();

        foreach (FieldRow row in fields)
        {
            if (row.FieldName != FloatValuesField && row.FieldName != ObjectValuesField && row.FieldName != VectorValuesField)
            {
                continue;
            }

            var key = (row.ActorNetGuid, row.ChannelIndex, row.PacketId, row.TimeMs);
            if (!groups.TryGetValue(key, out Dictionary<string, FieldRow>? members))
            {
                members = new Dictionary<string, FieldRow>();
                groups[key] = members;
            }

            members[row.FieldName] = row;
        }

        var results = new List<ShotFiredEvent>(groups.Count);
        foreach (KeyValuePair<(long Actor, long Channel, long Packet, long TimeMs), Dictionary<string, FieldRow>> kvp in groups)
        {
            Dictionary<string, FieldRow> members = kvp.Value;

            Dictionary<string, JsonElement> floatTags = DecodeBlob(members.GetValueOrDefault(FloatValuesField)?.ValueStr, tagTable);
            Dictionary<string, JsonElement> objectTags = DecodeBlob(members.GetValueOrDefault(ObjectValuesField)?.ValueStr, tagTable);
            Dictionary<string, JsonElement> vectorTags = DecodeBlob(members.GetValueOrDefault(VectorValuesField)?.ValueStr, tagTable);

            if (floatTags.Count == 0 && objectTags.Count == 0 && vectorTags.Count == 0)
            {
                // Nothing decoded from any of the three blobs (all missing, or all failed to
                // parse as the expected array-of-tag/value shape) -- not a usable shot record.
                continue;
            }

            var attackVectors = new List<ShotVector>();
            foreach (KeyValuePair<string, JsonElement> tag in vectorTags)
            {
                if (tag.Key.StartsWith(AttackVectorTagPrefix, StringComparison.Ordinal) &&
                    TryParseVector(tag.Value, out ShotVector? vector))
                {
                    attackVectors.Add(vector);
                }
            }

            var raw = new Dictionary<string, object?>();
            foreach (KeyValuePair<string, JsonElement> tag in floatTags)
            {
                raw[tag.Key] = JsonValueToObject(tag.Value);
            }
            foreach (KeyValuePair<string, JsonElement> tag in objectTags)
            {
                raw["object." + tag.Key] = JsonValueToObject(tag.Value);
            }
            foreach (KeyValuePair<string, JsonElement> tag in vectorTags)
            {
                if (!tag.Key.StartsWith(AttackVectorTagPrefix, StringComparison.Ordinal))
                {
                    raw["vector." + tag.Key] = JsonValueToObject(tag.Value);
                }
            }

            results.Add(new ShotFiredEvent(
                ActorNetGuid: kvp.Key.Actor,
                TimeMs: kvp.Key.TimeMs,
                AmmoRemaining: GetDouble(floatTags, AmmoRemainingTag),
                NumProjectiles: GetDouble(floatTags, NumProjectilesTag),
                RandomSeed: GetDouble(floatTags, RandomSeedTag),
                TracerOption: GetDouble(floatTags, TracerOptionTag),
                BurstShotNumber: GetDouble(floatTags, BurstShotNumberTag),
                AttackVectors: attackVectors,
                FiringPlayerStateNetGuid: GetLong(objectTags, FiringPlayerStateTag),
                FiringStateNetGuid: GetLong(objectTags, FiringStateTag),
                RawTags: raw));
        }

        results.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return results;
    }

    /// <summary>Parses one blob's decoded JSON array of <c>{"tag": handle, "value": ...}</c>
    /// elements into a <c>{resolvedName: rawJsonValue}</c> map. Returns an empty map (never
    /// throws) for a missing/malformed blob, an unresolvable tag handle, or anything not shaped
    /// like the expected array — this is speculative parsing of an unconfirmed format, so it
    /// degrades to "nothing decoded" rather than risk misreading garbage as real values.</summary>
    private static Dictionary<string, JsonElement> DecodeBlob(string? json, GameplayTagTable tagTable)
    {
        var result = new Dictionary<string, JsonElement>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!element.TryGetProperty("tag", out JsonElement tagEl) || tagEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                if (!element.TryGetProperty("value", out JsonElement valueEl) || valueEl.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (!tagEl.TryGetInt64(out long handle))
                {
                    continue;
                }

                string name = tagTable.Resolve(handle) ?? $"tag_{handle}";
                // Clone -- valueEl is only valid while `doc` is alive, and `doc` is disposed at
                // the end of this using block.
                result[name] = valueEl.Clone();
            }
        }

        return result;
    }

    private static double? GetDouble(Dictionary<string, JsonElement> tags, string name) =>
        tags.TryGetValue(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) ? d : null;

    private static long? GetLong(Dictionary<string, JsonElement> tags, string name)
    {
        if (!tags.TryGetValue(name, out JsonElement el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out long l) => l,
            JsonValueKind.String when long.TryParse(el.GetString(), out long l) => l,
            _ => null,
        };
    }

    /// <summary>Parses a vector element's <c>value</c> — shape not confirmed against a real
    /// export, so this tries both plausible encodings: a 3-element numeric array
    /// <c>[x, y, z]</c>, or an object with an <c>x</c>/<c>y</c>/<c>z</c> (any case) property
    /// set.</summary>
    private static bool TryParseVector(JsonElement value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ShotVector? vector)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            List<JsonElement> items = value.EnumerateArray().ToList();
            if (items.Count == 3 &&
                items[0].ValueKind == JsonValueKind.Number && items[0].TryGetDouble(out double x) &&
                items[1].ValueKind == JsonValueKind.Number && items[1].TryGetDouble(out double y) &&
                items[2].ValueKind == JsonValueKind.Number && items[2].TryGetDouble(out double z))
            {
                vector = new ShotVector(x, y, z);
                return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            double? x = GetAnyCase(value, "x", "X");
            double? y = GetAnyCase(value, "y", "Y");
            double? z = GetAnyCase(value, "z", "Z");
            if (x.HasValue && y.HasValue && z.HasValue)
            {
                vector = new ShotVector(x.Value, y.Value, z.Value);
                return true;
            }
        }

        vector = null;
        return false;
    }

    private static double? GetAnyCase(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
            {
                return d;
            }
        }

        return null;
    }

    private static object? JsonValueToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => el.Clone(),
    };
}
