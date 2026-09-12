using System.Text.Json;

namespace VrfInsights.Analysis.Weapons;

/// <summary>
/// Resolves vrfkit's replay-specific gameplay-tag handle numbers (the numeric <c>tag</c> in a
/// decoded shot-effect blob element, e.g. <c>{"tag":282,"value":23.0}</c>) back to their real
/// dotted names (e.g. <c>"FiringState.AmmoRemaining"</c>).
///
/// <para><b>Confirmed by reading vrfkit's own source</b> (<c>crates/vrf-decode/src/effect.rs</c>,
/// whose doc comment states tag indices are "replay-specific... resolved from the
/// <c>NetworkGameplayTagNodeIndex</c> table"; and vrfkit's own reference downstream consumer,
/// <c>tools/to_valplay_bundle.py</c>, which builds this exact <c>handle -&gt; name</c> map from
/// <c>manifest.json</c>'s <c>net_field_export_groups</c> array, from the one entry whose
/// <c>path</c> equals <c>"NetworkGameplayTagNodeIndex"</c>, reading its own flat <c>fields[]</c>
/// list of <c>{handle, name}</c> pairs) — <b>NOT yet independently confirmed against a real
/// decoded export by this project</b>, unlike most of the rest of this codebase's field-name
/// assumptions. The tag numbers are NOT stable across different replays (they depend on what
/// order that replay's own client happened to register gameplay tags in), so this table must be
/// rebuilt fresh per replay from that replay's own manifest — never hardcoded or cached across
/// files.</para>
/// </summary>
public sealed class GameplayTagTable
{
    private const string TagGroupPath = "NetworkGameplayTagNodeIndex";

    private readonly IReadOnlyDictionary<long, string> _byHandle;

    private GameplayTagTable(IReadOnlyDictionary<long, string> byHandle)
    {
        _byHandle = byHandle;
    }

    /// <summary>An empty table — <see cref="Resolve"/> always returns null. Used when a replay's
    /// manifest doesn't carry <c>net_field_export_groups</c> at all, or doesn't have the expected
    /// entry, rather than throwing.</summary>
    public static readonly GameplayTagTable Empty = new(new Dictionary<long, string>());

    public string? Resolve(long handle) => _byHandle.TryGetValue(handle, out string? name) ? name : null;

    public static GameplayTagTable Build(JsonElement? netFieldExportGroups)
    {
        if (netFieldExportGroups is not { ValueKind: JsonValueKind.Array } groups)
        {
            return Empty;
        }

        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!group.TryGetProperty("path", out JsonElement pathEl) ||
                pathEl.ValueKind != JsonValueKind.String ||
                pathEl.GetString() != TagGroupPath)
            {
                continue;
            }

            if (!group.TryGetProperty("fields", out JsonElement fieldsEl) || fieldsEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var map = new Dictionary<long, string>();
            foreach (JsonElement field in fieldsEl.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!field.TryGetProperty("handle", out JsonElement handleEl))
                {
                    continue;
                }

                if (!field.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                long? handle = handleEl.ValueKind switch
                {
                    JsonValueKind.Number => handleEl.TryGetInt64(out long h) ? h : null,
                    JsonValueKind.String => long.TryParse(handleEl.GetString(), out long h) ? h : null,
                    _ => null,
                };

                string? name = nameEl.GetString();
                if (handle.HasValue && name is not null)
                {
                    map[handle.Value] = name;
                }
            }

            // Return on the first matching group rather than merging across groups sharing the
            // same path (shouldn't happen, but a single well-formed match beats a speculative merge).
            return new GameplayTagTable(map);
        }

        return Empty;
    }
}
