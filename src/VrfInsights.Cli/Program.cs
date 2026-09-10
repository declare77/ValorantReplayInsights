using System.Text.Json;
using VrfInsights.Analysis;
using VrfInsights.Analysis.Identity;
using VrfInsights.Analysis.Vision;
using VrfInsights.Data;

namespace VrfInsights.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "analyze" => await RunAnalyzeAsync(args[1..]),
                "dump-fields" => await RunDumpFieldsAsync(args[1..]),
                "dump-classes" => await RunDumpClassesAsync(args[1..]),
                "-h" or "--help" or "help" => PrintUsageAndReturn(),
                _ => PrintUnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // vrf-insights analyze <export-dir> --out <out-dir> [--fov 103] [--range 18000] [--eye-height 155] [--with-vision]
    private static async Task<int> RunAnalyzeAsync(string[] args)
    {
        string? exportDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        string outDir = GetOption(args, "--out") ?? "vrf-insights-out";
        double fov = double.Parse(GetOption(args, "--fov") ?? VisionConeCalculator.DefaultFovDegrees.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        double range = double.Parse(GetOption(args, "--range") ?? VisionConeCalculator.DefaultRangeCm.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        double eyeHeight = double.Parse(GetOption(args, "--eye-height") ?? VisionConeCalculator.EyeHeightCm.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        bool withVision = HasFlag(args, "--with-vision");

        if (exportDir is null)
        {
            Console.Error.WriteLine("usage: vrf-insights analyze <vrfkit-export-dir> --out <output-dir> [--fov 103] [--range 18000] [--eye-height 155] [--with-vision]");
            return 1;
        }

        Console.WriteLine($"Loading vrfkit export from: {exportDir}");
        VrfExportSet export = await VrfExportSet.LoadAsync(exportDir);

        Console.WriteLine($"Build: {export.Manifest.ReplayBuild}   Duration: {export.Manifest.DurationMs} ms");
        Console.WriteLine($"Tables: fields={export.Fields.Count:N0} movement={export.Movement.Count:N0} actors={export.Actors.Count:N0} net_guids={export.NetGuids.Count:N0} events={export.Events.Count:N0}");

        AgentCatalog agentCatalog = AgentCatalog.LoadEmbedded();
        MatchAnalysis analysis = MatchAnalysis.Build(export, agentCatalog, new VisionConeOptions(fov, range, eyeHeight));

        Directory.CreateDirectory(outDir);

        await WriteJsonAsync(Path.Combine(outDir, "match.json"), new
        {
            analysis.ReplayBuild,
            analysis.DurationMs,
            Players = analysis.Players,
            Rounds = analysis.Rounds,
        });

        await WriteJsonAsync(Path.Combine(outDir, "events.json"), analysis.Events);
        await WriteJsonAsync(Path.Combine(outDir, "movement.json"), analysis.MovementTracks);
        await WriteJsonAsync(Path.Combine(outDir, "utility.json"), analysis.Utility);
        await WriteJsonAsync(Path.Combine(outDir, "ability_casts.json"), analysis.AbilityCasts);
        await WriteJsonAsync(Path.Combine(outDir, "ultimate_usages.json"), analysis.UltimateUsages);
        await WriteJsonAsync(Path.Combine(outDir, "combat_interactions.json"), analysis.CombatInteractions);
        await WriteJsonAsync(Path.Combine(outDir, "economy.json"), analysis.Economy);

        if (withVision)
        {
            var visionByPlayer = new Dictionary<string, object>();
            foreach (var track in analysis.MovementTracks)
            {
                string label = track.Player.Subject ?? $"actor_{track.Player.ActorNetGuid}";
                visionByPlayer[label] = analysis.BuildVisionCones(track, new VisionConeOptions(fov, range, eyeHeight));
            }

            await WriteJsonAsync(Path.Combine(outDir, "vision_cones.json"), visionByPlayer);
        }

        Console.WriteLine($"Wrote analysis output to: {Path.GetFullPath(outDir)}");
        Console.WriteLine($"Players: {analysis.Players.Count}   Rounds: {analysis.Rounds.Count}   Utility events: {analysis.Utility.Count}   Ability casts: {analysis.AbilityCasts.Count}   Combat interactions: {analysis.CombatInteractions.Count}");
        return 0;
    }

    // Diagnostic helper: print distinct field_name values under a group_path substring, so you
    // can confirm the exact naming this project's builders assume (see AbilityCastBuilder's
    // CastLocation.X/.Y/.Z assumption) against your own export.
    private static async Task<int> RunDumpFieldsAsync(string[] args)
    {
        string? exportDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        string? groupFilter = GetOption(args, "--group");
        int limit = int.Parse(GetOption(args, "--limit") ?? "50", System.Globalization.CultureInfo.InvariantCulture);

        if (exportDir is null)
        {
            Console.Error.WriteLine("usage: vrf-insights dump-fields <vrfkit-export-dir> [--group <substring>] [--limit 50]");
            return 1;
        }

        VrfExportSet export = await VrfExportSet.LoadAsync(exportDir);
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in export.Fields)
        {
            if (row.FieldName is null) continue;
            if (groupFilter is not null && !row.GroupPath.Contains(groupFilter, StringComparison.OrdinalIgnoreCase)) continue;
            seen.Add($"{row.GroupPath} :: {row.FieldName}");
        }

        int shown = 0;
        foreach (string line in seen)
        {
            Console.WriteLine(line);
            if (++shown >= limit) break;
        }

        Console.WriteLine($"({seen.Count} distinct group::field_name pairs matched; showing {shown})");
        return 0;
    }

    // Diagnostic helper: print distinct actor class_path values, to help extend
    // UtilityEffectClassifier's keyword table against your own export.
    private static async Task<int> RunDumpClassesAsync(string[] args)
    {
        string? exportDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        if (exportDir is null)
        {
            Console.Error.WriteLine("usage: vrf-insights dump-classes <vrfkit-export-dir>");
            return 1;
        }

        VrfExportSet export = await VrfExportSet.LoadAsync(exportDir);
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in export.Actors)
        {
            if (row.ClassPath is not null) seen.Add(row.ClassPath);
        }

        foreach (string cls in seen)
        {
            Console.WriteLine(cls);
        }

        Console.WriteLine($"({seen.Count} distinct class paths)");
        return 0;
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        await using FileStream fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, value, JsonOptions);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) => Array.Exists(args, a => string.Equals(a, name, StringComparison.Ordinal));

    private static int PrintUsageAndReturn()
    {
        PrintUsage();
        return 0;
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            vrf-insights — analysis/visualization layer on top of vrfkit's decoded VALORANT replay tables.
            This tool never opens a .vrf file itself: run vrfkit first, then point this at its output.

              1) vrfkit export <file.vrf> --out <export-dir>      (https://github.com/yakisoba0728/vrfkit)
              2) vrf-insights analyze <export-dir> --out <output-dir> [options]

            Commands:
              analyze <export-dir> --out <dir> [--fov 103] [--range 18000] [--eye-height 155] [--with-vision]
                  Build the full match analysis and write it out as JSON files.

              dump-fields <export-dir> [--group <substring>] [--limit 50]
                  Print distinct (group_path, field_name) pairs — useful for confirming this
                  project's assumptions about field naming against your own export.

              dump-classes <export-dir>
                  Print distinct actor class_path values seen in actors.parquet — useful for
                  extending VrfInsights.Analysis.Utility.UtilityEffectClassifier's keyword table.
            """);
    }
}
