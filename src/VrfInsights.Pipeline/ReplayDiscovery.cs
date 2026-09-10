namespace VrfInsights.Pipeline;

/// <summary>
/// Finds <c>.vrf</c> replay files the VALORANT client has already saved locally, so a GUI can
/// offer a convenience picker instead of making the user hunt for the folder themselves. Purely
/// a file-listing helper — it never opens or reads the contents of a <c>.vrf</c> file.
/// </summary>
public static class ReplayDiscovery
{
    public sealed record ReplayFile(string FullPath, string FileName, DateTime LastWriteTimeUtc, long SizeBytes);

    /// <summary>The VALORANT client's default save location for local replays
    /// (<c>%LOCALAPPDATA%\VALORANT\Saved\Demos</c>). Returns null if <c>LOCALAPPDATA</c> isn't
    /// set (e.g. non-Windows), since that's the only location the client itself uses.</summary>
    public static string? DefaultReplayDirectory()
    {
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData)) return null;
        return Path.Combine(localAppData, "VALORANT", "Saved", "Demos");
    }

    /// <summary>Lists <c>.vrf</c> files under <paramref name="directory"/> (defaults to
    /// <see cref="DefaultReplayDirectory"/>), newest first. Returns an empty list — never
    /// throws — if the directory doesn't exist or can't be read, so callers can fall back to a
    /// manual file picker without special-casing this.</summary>
    public static IReadOnlyList<ReplayFile> FindReplays(string? directory = null)
    {
        directory ??= DefaultReplayDirectory();
        if (directory is null || !Directory.Exists(directory))
        {
            return Array.Empty<ReplayFile>();
        }

        try
        {
            var results = new List<ReplayFile>();
            foreach (string path in Directory.EnumerateFiles(directory, "*.vrf", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(path);
                results.Add(new ReplayFile(info.FullName, info.Name, info.LastWriteTimeUtc, info.Length));
            }

            results.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            return results;
        }
        catch (IOException)
        {
            return Array.Empty<ReplayFile>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<ReplayFile>();
        }
    }
}
