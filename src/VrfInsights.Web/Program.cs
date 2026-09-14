using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using VrfInsights.Analysis;
using VrfInsights.Pipeline;

// -----------------------------------------------------------------------------------------------
// The hosted "upload your .vrf, get the 2D replay" backend. This is a thin HTTP wrapper around the
// exact same VrfInsights.Pipeline code the desktop CLI/GUI already call -- it does NOT open a
// .vrf file or contain any decoding/descrambling logic itself. All it does is:
//   1. save the uploaded file to a scratch directory,
//   2. launch vrfkit.exe as an external process via FullPipeline (VrfkitExportRunner underneath),
//   3. run the existing analysis pipeline on vrfkit's own decoded output,
//   4. hand the resulting JSON straight back in the HTTP response, and
//   5. delete the scratch directory.
// See server/README.md for how to build/deploy this as a container (Cloud Run, or anywhere else
// that can run a Linux container) and how it's meant to sit behind Firebase Hosting.
// -----------------------------------------------------------------------------------------------

const long DefaultMaxUploadMb = 100; // a full competitive match's .vrf is typically 40-70MB.

long maxUploadBytes = ReadMaxUploadBytes();
string? configuredVrfkitPath = Environment.GetEnvironmentVariable("VRFKIT_EXE_PATH");

var builder = WebApplication.CreateBuilder(args);

// Cloud Run (and most container hosts) tell you which port to listen on via $PORT rather than
// letting you pick one -- default to 8080 for local `dotnet run` testing.
string port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(int.Parse(port));
    // Kestrel's own default request-body cap (~28.6MB) sits underneath FormOptions below and
    // would silently reject any realistic-sized .vrf (typical full matches are 40-70MB) even
    // with MAX_UPLOAD_MB set higher -- both limits have to move together.
    o.Limits.MaxRequestBodySize = maxUploadBytes;
});

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxUploadBytes;
});

// Permissive CORS so the API also works when called directly from a Cloud Run URL during setup/
// testing, before a Firebase Hosting rewrite makes it same-origin (the recommended long-term
// setup -- see server/README.md). No cookies/credentials are used, so AllowAnyOrigin is fine here.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapPost("/api/parse", async (HttpRequest request, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "expected multipart/form-data with a 'vrf' file field" });
    }

    IFormCollection form = await request.ReadFormAsync(ct);
    IFormFile? vrfFile = form.Files["vrf"];
    if (vrfFile is null || vrfFile.Length == 0)
    {
        return Results.BadRequest(new { error = "no file uploaded under the 'vrf' field" });
    }

    if (!vrfFile.FileName.EndsWith(".vrf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "expected a .vrf file" });
    }

    if (vrfFile.Length > maxUploadBytes)
    {
        return Results.BadRequest(new { error = $"file is larger than the {maxUploadBytes / 1_000_000}MB limit this server accepts" });
    }

    string? vrfkitExePath = configuredVrfkitPath ?? VrfkitBootstrapper.FindExisting();
    if (vrfkitExePath is null || !File.Exists(vrfkitExePath))
    {
        // A misconfigured deployment (image built without vrfkit baked in, or VRFKIT_EXE_PATH
        // pointing at the wrong place) -- not something an end user uploading a file caused, so
        // this is a 500, not a 400.
        return Results.Problem(
            "vrfkit executable not found on this server -- check the VRFKIT_EXE_PATH environment variable / how the container image was built (see server/README.md).",
            statusCode: 500);
    }

    string jobDir = Path.Combine(Path.GetTempPath(), "vrf-insights-web", Guid.NewGuid().ToString("N"));
    string inputPath = Path.Combine(jobDir, "input", "replay.vrf");
    string exportDir = Path.Combine(jobDir, "export");
    string outputDir = Path.Combine(jobDir, "output");

    var logTail = new List<string>();
    void CaptureLogLine(string line)
    {
        logTail.Add(line);
        if (logTail.Count > 200) logTail.RemoveAt(0); // bounded -- a failed run can log a lot
        app.Logger.LogInformation("{Line}", line);
    }

    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        await using (FileStream fs = File.Create(inputPath))
        {
            await vrfFile.CopyToAsync(fs, ct);
        }

        // A generous ceiling separate from the caller's own cancellation -- see server/README.md
        // for why (and for the matching --timeout you need to set when deploying to Cloud Run).
        // NOTE: this stops the request from waiting on vrfkit past 4 minutes, but doesn't kill
        // vrfkit's own process if it's still running -- ExternalProcessRunner (shared with the
        // desktop CLI/GUI) awaits Process.WaitForExitAsync(ct), which throws on cancellation
        // without terminating the child process. Vanishingly unlikely to matter in practice
        // (vrfkit decodes in well under a second per its own published benchmark), but a
        // legitimate leak if it ever did trigger repeatedly on a long-lived container instance.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(4));

        FullPipeline.FullPipelineResult result = await FullPipeline.RunAsync(
            new FullPipeline.FullPipelineOptions(
                VrfFilePath: inputPath,
                VrfkitExePath: vrfkitExePath,
                ExportDirectory: exportDir,
                OutputDirectory: outputDir,
                AnalysisOptions: new AnalysisPipelineOptions()), // defaults: no vision cones, 10Hz movement -- see AnalysisPipelineOptions
            onStatus: CaptureLogLine,
            onLogLine: CaptureLogLine,
            ct: timeoutCts.Token);

        if (!result.ExportSucceeded)
        {
            return Results.Json(new
            {
                error = $"vrfkit could not decode this file (exit code {result.ExportExitCode})",
                log = logTail,
            }, statusCode: 422);
        }

        Dictionary<string, JsonElement?> bundle = ReadOutputBundle(outputDir);
        return Results.Json(bundle);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.StatusCode(499); // client went away -- not a server error
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "parse failed");
        return Results.Json(new { error = "unexpected server error while parsing", log = logTail }, statusCode: 500);
    }
    finally
    {
        TryDeleteDirectory(jobDir);
    }
});

app.Run();

static long ReadMaxUploadBytes()
{
    string? raw = Environment.GetEnvironmentVariable("MAX_UPLOAD_MB");
    long mb = long.TryParse(raw, out long parsed) && parsed > 0 ? parsed : DefaultMaxUploadMb;
    return mb * 1_000_000;
}

// Reads back every JSON file AnalysisPipeline wrote (see AnalysisPipeline.RunAsync) into one
// bundle keyed the same way viewer/app.js's loadFromDataBundle() expects -- match.json/
// movement.json become "match"/"movement", etc. Each value is parsed once (JsonElement) and
// re-serialized as-is rather than round-tripped through this project's C# types again, since this
// endpoint's only job is to hand the already-correct JSON along.
static Dictionary<string, JsonElement?> ReadOutputBundle(string outputDir)
{
    // (bundle key, file name) -- file name matches AnalysisPipeline.RunAsync's WriteJsonAsync calls.
    (string Key, string FileName)[] files =
    [
        ("match", "match.json"),
        ("movement", "movement.json"),
        ("utility", "utility.json"),
        ("ability_casts", "ability_casts.json"),
        ("vision_cones", "vision_cones.json"), // only present when --with-vision was used
        ("events", "events.json"),
        ("economy", "economy.json"),
        ("combat_interactions", "combat_interactions.json"),
        ("shots", "shots.json"),
        ("hits", "hits.json"),
        ("armor_purchases", "armor_purchases.json"),
    ];

    var bundle = new Dictionary<string, JsonElement?>();
    foreach ((string key, string fileName) in files)
    {
        string path = Path.Combine(outputDir, fileName);
        if (!File.Exists(path))
        {
            continue; // matches how the local file-picker treats a missing optional file
        }

        using FileStream fs = File.OpenRead(path);
        using JsonDocument doc = JsonDocument.Parse(fs);
        bundle[key] = doc.RootElement.Clone(); // Clone() so it outlives the disposed JsonDocument
    }

    return bundle;
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
    catch (Exception)
    {
        // Best-effort cleanup -- a leftover temp dir in a container that gets recycled between
        // requests anyway isn't worth failing the response over.
    }
}
