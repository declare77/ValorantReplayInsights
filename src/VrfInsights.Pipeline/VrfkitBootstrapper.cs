namespace VrfInsights.Pipeline;

/// <summary>
/// Automates the two manual commands vrfkit's own README asks you to run — <c>git clone</c> and
/// <c>cargo build --release -p vrfkit --features export</c> — so a person only has to click one
/// button instead of using a terminal. <b>This fetches and compiles vrfkit's own published
/// source code, unmodified; it does not add, patch, or reimplement any of vrfkit's decoding or
/// payload-transform logic.</b> It's the same thing as running those two commands by hand,
/// automated.
///
/// The built binary is cached at a well-known location
/// (<see cref="DefaultInstallDirectory"/>), so after the first successful setup, every later
/// call to <see cref="FindExisting"/> finds it instantly with no network or build step — the
/// whole point being that a person (or the GUI) never has to browse to vrfkit.exe by hand again.
/// </summary>
public static class VrfkitBootstrapper
{
    public const string RepoUrl = "https://github.com/yakisoba0728/vrfkit.git";

    public sealed record ToolCheckResult(bool GitAvailable, bool CargoAvailable)
    {
        public bool AllAvailable => GitAvailable && CargoAvailable;
    }

    public sealed record SetupResult(bool Success, string? VrfkitExePath, string? FailureReason);

    /// <summary>Where this project clones and builds vrfkit, by default:
    /// <c>%LocalAppData%\VrfInsights\vrfkit-src</c> (or the platform equivalent).</summary>
    public static string DefaultInstallDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "VrfInsights", "vrfkit-src");
    }

    private static string ExpectedExeName => OperatingSystem.IsWindows() ? "vrfkit.exe" : "vrfkit";

    /// <summary>Where <c>cargo build --release</c> places the binary, given a clone at
    /// <paramref name="installDirectory"/>.</summary>
    public static string ExpectedBinaryPath(string? installDirectory = null) =>
        Path.Combine(installDirectory ?? DefaultInstallDirectory(), "target", "release", ExpectedExeName);

    /// <summary>The already-built vrfkit binary from a previous <see cref="SetupAsync"/> run, or
    /// null if none has been built yet. Pure file check — never builds anything, so it's safe to
    /// call on every app startup to decide whether setup is even needed.</summary>
    public static string? FindExisting(string? installDirectory = null)
    {
        string path = ExpectedBinaryPath(installDirectory);
        return File.Exists(path) ? path : null;
    }

    public static async Task<ToolCheckResult> CheckToolsAsync(CancellationToken ct = default)
    {
        bool git = await ExternalProcessRunner.IsAvailableAsync("git", "--version", ct);
        bool cargo = await ExternalProcessRunner.IsAvailableAsync("cargo", "--version", ct);
        return new ToolCheckResult(git, cargo);
    }

    /// <summary>
    /// If a built vrfkit already exists at <paramref name="installDirectory"/>, returns it
    /// immediately. Otherwise checks for Git and Rust, clones (or updates) vrfkit's repository,
    /// builds it, and returns the resulting binary's path. Every step's output is streamed to
    /// <paramref name="onLogLine"/> and high-level progress to <paramref name="onStatus"/>, the
    /// same way <see cref="VrfkitExportRunner"/> streams vrfkit's own output.
    /// </summary>
    public static async Task<SetupResult> SetupAsync(
        string? installDirectory = null,
        Action<string>? onStatus = null,
        Action<string>? onLogLine = null,
        CancellationToken ct = default)
    {
        installDirectory ??= DefaultInstallDirectory();

        string? existing = FindExisting(installDirectory);
        if (existing is not null)
        {
            onStatus?.Invoke($"vrfkit is already set up at: {existing}");
            return new SetupResult(true, existing, null);
        }

        onStatus?.Invoke("Checking for Git and Rust (cargo)...");
        ToolCheckResult tools = await CheckToolsAsync(ct);
        if (!tools.AllAvailable)
        {
            var missing = new List<string>();
            if (!tools.GitAvailable) missing.Add("Git — https://git-scm.com/download/win");
            if (!tools.CargoAvailable) missing.Add("Rust — https://rustup.rs (on Windows you also need the \"Desktop development with C++\" workload from Visual Studio Build Tools, for its linker)");

            string reason = "vrfkit needs to be built from source once, which needs: " + string.Join(" and ", missing) +
                ". Install whichever is missing, restart this app, and click Set up vrfkit again.";
            onStatus?.Invoke(reason);
            return new SetupResult(false, null, reason);
        }

        bool alreadyCloned = Directory.Exists(Path.Combine(installDirectory, ".git"));
        if (!alreadyCloned)
        {
            string? parent = Path.GetDirectoryName(installDirectory);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            onStatus?.Invoke($"Downloading vrfkit from {RepoUrl} ...");
            var cloneArgs = new List<string> { "clone", RepoUrl, installDirectory };
            ExternalProcessRunner.RunResult cloneResult = await ExternalProcessRunner.RunAsync(
                "git",
                cloneArgs,
                workingDirectory: null,
                onOutputLine: onLogLine,
                onErrorLine: onLogLine,
                ct: ct);

            if (!cloneResult.Success)
            {
                string reason = $"Downloading vrfkit failed (git exit code {cloneResult.ExitCode}) — see the log above for details.";
                onStatus?.Invoke(reason);
                return new SetupResult(false, null, reason);
            }
        }
        else
        {
            onStatus?.Invoke("vrfkit source already downloaded — checking for updates...");
            // Best-effort only: an offline machine, or a checkout with local changes, shouldn't
            // block the build if a usable clone is already sitting there.
            await ExternalProcessRunner.RunAsync(
                "git",
                new List<string> { "pull", "--ff-only" },
                workingDirectory: installDirectory,
                onOutputLine: onLogLine,
                onErrorLine: onLogLine,
                ct: ct);
        }

        onStatus?.Invoke("Building vrfkit with cargo — this can take a few minutes the first time...");
        var buildArgs = new List<string> { "build", "--release", "-p", "vrfkit", "--features", "export" };
        ExternalProcessRunner.RunResult buildResult = await ExternalProcessRunner.RunAsync(
            "cargo",
            buildArgs,
            workingDirectory: installDirectory,
            onOutputLine: onLogLine,
            onErrorLine: onLogLine,
            ct: ct);

        if (!buildResult.Success)
        {
            string reason = $"Building vrfkit failed (cargo exit code {buildResult.ExitCode}) — see the log above for details.";
            onStatus?.Invoke(reason);
            return new SetupResult(false, null, reason);
        }

        string? builtPath = FindExisting(installDirectory);
        if (builtPath is null)
        {
            string reason = $"cargo reported success but the built binary wasn't found at the expected path: {ExpectedBinaryPath(installDirectory)}";
            onStatus?.Invoke(reason);
            return new SetupResult(false, null, reason);
        }

        onStatus?.Invoke($"vrfkit is ready: {builtPath}");
        return new SetupResult(true, builtPath, null);
    }
}
