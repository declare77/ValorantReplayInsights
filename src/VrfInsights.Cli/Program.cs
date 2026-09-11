using VrfInsights.Analysis.Vision;
using VrfInsights.Data;
using VrfInsights.Pipeline;

namespace VrfInsights.Cli;

public static class Program
{
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
                "run" => await RunFullPipelineAsync(args[1..]),
                "analyze" => await RunAnalyzeAsync(args[1..]),
                "dump-fields" => await RunDumpFieldsAsync(args[1..]),
                "dump-classes" => await RunDumpClassesAsync(args[1..]),
                "dump-values" => await RunDumpValuesAsync(args[1..]),
                "dump-actors" => await RunDumpActorsAsync(args[1..]),
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

    // vrf-insights run <file.vrf> --vrfkit <path-to-vrfkit-exe> [--export-dir <dir>] --out <dir>
    //     [--fov 103] [--range 18000] [--eye-height 155] [--with-vision] [--movement-hz 10]
    //
    // The "one smooth command": decodes the replay with vrfkit, then runs the analysis on
    // vrfkit's output, in a single invocation. Equivalent to running `vrfkit export` yourself
    // and then `vrf-insights analyze`, just without the two-tool hop.
    private static async Task<int> RunFullPipelineAsync(string[] args)
    {
        string? vrfFile = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        string? vrfkitExe = GetOption(args, "--vrfkit");
        string outDir = GetOption(args, "--out") ?? "vrf-insights-out";
        string exportDir = GetOption(args, "--export-dir") ?? Path.Combine(outDir, "export");
        AnalysisPipelineOptions analysisOptions = ParseAnalysisOptions(args);

        if (vrfFile is null || vrfkitExe is null)
        {
            Console.Error.WriteLine("usage: vrf-insights run <file.vrf> --vrfkit <path-to-vrfkit(.exe)> --out <output-dir> [--export-dir <dir>] [--fov 103] [--range 18000] [--eye-height 155] [--with-vision] [--movement-hz 10]");
            return 1;
        }

        var options = new FullPipeline.FullPipelineOptions(
            VrfFilePath: vrfFile,
            VrfkitExePath: vrfkitExe,
            ExportDirectory: exportDir,
            OutputDirectory: outDir,
            AnalysisOptions: analysisOptions);

        FullPipeline.FullPipelineResult result = await FullPipeline.RunAsync(
            options,
            onStatus: Console.WriteLine,
            onLogLine: line => Console.WriteLine($"  {line}"));

        return result.ExportSucceeded ? 0 : 1;
    }

    // vrf-insights analyze <export-dir> --out <out-dir> [--fov 103] [--range 18000] [--eye-height 155] [--with-vision] [--movement-hz 10]
    private static async Task<int> RunAnalyzeAsync(string[] args)
    {
        string? exportDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        string outDir = GetOption(args, "--out") ?? "vrf-insights-out";
        AnalysisPipelineOptions analysisOptions = ParseAnalysisOptions(args);

        if (exportDir is null)
        {
            Console.Error.WriteLine("usage: vrf-insights analyze <vrfkit-export-dir> --out <output-dir> [--fov 103] [--range 18000] [--eye-height 155] [--with-vision] [--movement-hz 10]");
            return 1;
        }

        await AnalysisPipeline.RunAsync(exportDir, outDir, analysisOptions, onStatus: Console.WriteLine);
        return 0;
    }

    private static AnalysisPipelineOptions ParseAnalysisOptions(string[] args)
    {
        double fov = double.Parse(GetOption(args, "--fov") ?? VisionConeCalculator.DefaultFovDegrees.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        double range = double.Parse(GetOption(args, "--range") ?? VisionConeCalculator.DefaultRangeCm.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        double eyeHeight = double.Parse(GetOption(args, "--eye-height") ?? VisionConeCalculator.EyeHeightCm.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        bool withVision = HasFlag(args, "--with-vision");
        double movementHz = double.Parse(GetOption(args, "--movement-hz") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
        return new AnalysisPipelineOptions(
            FovDegrees: fov,
            RangeCm: range,
            EyeHeightCm: eyeHeight,
            WithVision: withVision,
            MovementSamplesPerSecond: movementHz);
    }

    // Diagnostic helper: print distinct field_name values under a group_path substring, so you
    // can confirm the exact naming this project's builders assume against your own export (this
    // is exactly how AbilityCastBuilder's old CastLocation.X/.Y/.Z assumption was caught wrong).
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

    // Diagnostic helper: print the actual row-level values (not just names) for fields matching
    // a substring, so you can see how a given member is really encoded (a plain scalar via one of
    // the Value* overlay columns, a formatted string, or something only decodable from RawBits)
    // instead of guessing from the name alone. Complements dump-fields.
    private static async Task<int> RunDumpValuesAsync(string[] args)
    {
        string? exportDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        string? fieldFilter = GetOption(args, "--field");
        string? groupFilter = GetOption(args, "--group");
        int limit = int.Parse(GetOption(args, "--limit") ?? "20", System.Globalization.CultureInfo.InvariantCulture);

        if (exportDir is null || fieldFilter is null)
        {
            Console.Error.WriteLine("usage: vrf-insights dump-values <vrfkit-export-dir> --field <substring> [--group <substring>] [--limit 20]");
            return 1;
        }

        VrfExportSet export = await VrfExportSet.LoadAsync(exportDir);
        int shown = 0;
        foreach (var row in export.Fields)
        {
            if (row.FieldName is null || !row.FieldName.Contains(fieldFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (groupFilter is not null && !row.GroupPath.Contains(groupFilter, StringComparison.OrdinalIgnoreCase)) continue;

            string rawHex = row.RawBits is null
                ? "(none)"
                : Convert.ToHexString(row.RawBits.Length > 32 ? row.RawBits[..32] : row.RawBits) + (row.RawBits.Length > 32 ? "..." : "");

            Console.WriteLine(
                $"t={row.TimeMs} actor={row.ActorNetGuid} field={row.FieldName} bits={row.BitCount} " +
                $"i64={row.ValueI64?.ToString() ?? "null"} f64={row.ValueF64?.ToString() ?? "null"} " +
                $"bool={row.ValueBool?.ToString() ?? "null"} str={row.ValueStr ?? "null"} raw={rawHex}");

            if (++shown >= limit) break;
        }

        if (shown == 0)
        {
            Console.WriteLine("(no rows matched -- check --field/--group substrings against dump-fields output)");
        }
        else
        {
            Console.WriteLine($"(showing {shown} matching row(s), --limit {limit})");
        }

        return 0;
    }

    // Diagnostic helper: print actors.parquet's own row-level lifecycle (time, event, spawn
    // position) for actors whose class_path matches a substring -- to see whether a given
    // ability's actor is a single instance reused across the whole match (recorded spawn position
    // is stale from creation) or a fresh instance per cast (recorded spawn position is the launch
    // point, not necessarily where the effect ends up). Complements dump-values, which only looks
    // at fields.parquet.
    private static async Task<int> RunDumpActorsAsync(string[] args)
    {
        string? exportDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        string? classFilter = GetOption(args, "--class");
        int limit = int.Parse(GetOption(args, "--limit") ?? "30", System.Globalization.CultureInfo.InvariantCulture);

        if (exportDir is null || classFilter is null)
        {
            Console.Error.WriteLine("usage: vrf-insights dump-actors <vrfkit-export-dir> --class <substring> [--limit 30]");
            return 1;
        }

        VrfExportSet export = await VrfExportSet.LoadAsync(exportDir);
        var sorted = export.Actors
            .Where(a => a.ClassPath is not null && a.ClassPath.Contains(classFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.TimeMs)
            .ToList();

        int shown = 0;
        foreach (var row in sorted)
        {
            Console.WriteLine(
                $"t={row.TimeMs} actor={row.ActorNetGuid} event={row.Event} class={row.ClassPath} " +
                $"spawnX={row.SpawnX?.ToString() ?? "null"} spawnY={row.SpawnY?.ToString() ?? "null"} spawnZ={row.SpawnZ?.ToString() ?? "null"}");

            if (++shown >= limit) break;
        }

        if (shown == 0)
        {
            Console.WriteLine("(no rows matched -- check --class against dump-classes output)");
        }
        else
        {
            Console.WriteLine($"(showing {shown} of {sorted.Count} matching row(s), --limit {limit})");
        }

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
            This tool never opens a .vrf file itself: it shells out to vrfkit (an external, independent
            tool) to decode, then only reads vrfkit's already-decoded output.

            Easiest path — one command that does both steps:
              vrf-insights run <file.vrf> --vrfkit <path-to-vrfkit(.exe)> --out <output-dir> [options]

            Or run the two steps yourself:
              1) vrfkit export <file.vrf> --out <export-dir>      (https://github.com/yakisoba0728/vrfkit)
              2) vrf-insights analyze <export-dir> --out <output-dir> [options]

            Commands:
              run <file.vrf> --vrfkit <path> --out <dir> [--export-dir <dir>] [--fov 103] [--range 18000] [--eye-height 155] [--with-vision] [--movement-hz 10]
                  Decode the replay with vrfkit and analyze it, in one step. --export-dir defaults
                  to <out>/export if not given (so vrfkit's raw tables are kept alongside the
                  analysis JSON, in case you want to inspect them with dump-fields/dump-classes).

              analyze <export-dir> --out <dir> [--fov 103] [--range 18000] [--eye-height 155] [--with-vision] [--movement-hz 10]
                  Build the full match analysis from an existing vrfkit export and write it out
                  as JSON files. --movement-hz caps movement.json (and, with --with-vision,
                  vision_cones.json) to at most that many samples/sec per player, by minimum time
                  spacing (always keeping each player's first and last sample) — a full-length
                  match at full tick rate can otherwise produce a movement.json hundreds of MB in
                  size, too large for a browser to load in the 2D replay viewer. Defaults to 10;
                  pass --movement-hz 0 for full, undownsampled fidelity.

              dump-fields <export-dir> [--group <substring>] [--limit 50]
                  Print distinct (group_path, field_name) pairs — useful for confirming this
                  project's assumptions about field naming against your own export.

              dump-values <export-dir> --field <substring> [--group <substring>] [--limit 20]
                  Print actual row-level values (time, actor, bit count, every Value* column, and
                  a hex preview of RawBits) for fields whose name contains --field — use this
                  after dump-fields to see how a field is really encoded, not just what it's named.

              dump-classes <export-dir>
                  Print distinct actor class_path values seen in actors.parquet — useful for
                  extending VrfInsights.Analysis.Utility.UtilityEffectClassifier's keyword table.

              dump-actors <export-dir> --class <substring> [--limit 30]
                  Print actors.parquet's own row-level lifecycle (time, event, spawn position) for
                  actors whose class_path contains --class — use this after dump-classes to see
                  whether an ability's actor is reused across the match or fresh per cast, and
                  whether its recorded spawn position tracks the real effect location.
            """);
    }
}
