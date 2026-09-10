using System.Reflection;
using System.Text.Json;

namespace VrfInsights.Analysis.Identity;

/// <summary>
/// Resolves a loadout's <c>characterId</c> (an asset GUID VALORANT's own client reports) to a
/// display name. The mapping is Riot's own public content data
/// (<see href="https://valorant-api.com/v1/agents"/>), not anything reverse-engineered from a
/// replay, and it's intentionally small and easy to refresh — see
/// <c>Identity/agents.json</c>. An unknown id degrades gracefully to a labeled raw GUID rather
/// than throwing, since new agents ship after any snapshot of this table is taken.
/// </summary>
public sealed class AgentCatalog
{
    private readonly Dictionary<string, string> _byId;

    private AgentCatalog(Dictionary<string, string> byId)
    {
        _byId = byId;
    }

    public static AgentCatalog LoadEmbedded()
    {
        Assembly asm = typeof(AgentCatalog).Assembly;
        const string resourceName = "VrfInsights.Analysis.Identity.agents.json";
        using Stream? stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Should only happen if the csproj's EmbeddedResource entry or this resource name
            // ever drift apart — fail soft with an empty catalog rather than crashing the CLI.
            return new AgentCatalog(new Dictionary<string, string>());
        }

        using JsonDocument doc = JsonDocument.Parse(stream);
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith('_'))
            {
                continue; // "_comment"
            }

            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                byId[prop.Name] = prop.Value.GetString() ?? prop.Name;
            }
        }

        return new AgentCatalog(byId);
    }

    /// <summary>Merges additional/overriding id-&gt;name pairs on top of the embedded table —
    /// use this to point at a freshly re-fetched valorant-api.com dump without rebuilding.</summary>
    public AgentCatalog WithOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(_byId, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> kvp in overrides)
        {
            merged[kvp.Key] = kvp.Value;
        }

        return new AgentCatalog(merged);
    }

    public string Resolve(string? characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId))
        {
            return "(unknown agent)";
        }

        return _byId.TryGetValue(characterId, out string? name)
            ? name
            : $"(unrecognized agent id: {characterId})";
    }
}
