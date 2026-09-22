using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using Android.Widget;
using VrfInsights.Pipeline;

namespace VrfInsights.Android;

/// <summary>
/// The whole app. Hosts the existing 2D replay viewer (viewer/index.html, unmodified apart from
/// hiding its own upload UI, which doesn't apply here) in a WebView, and replaces its
/// "POST a .vrf to a server" upload path with a fully on-device one: pick a file with Storage
/// Access Framework, run it through <see cref="FullPipeline"/> (the exact same class
/// VrfInsights.Web's /api/parse endpoint calls) against the vrfkit binary bundled in this APK,
/// then hand the resulting JSON straight to the page's own <c>loadFromDataBundle()</c> function.
///
/// This activity never opens a .vrf file or contains decoding logic itself, same as every other
/// entry point in this repository -- it only launches vrfkit (bundled as lib/arm64-v8a/
/// libvrfkit.so, see android/README.md) as an external process, exactly like the CLI/GUI/server
/// already do.
/// </summary>
[Activity(Label = "VRF Insights", MainLauncher = true, Theme = "@android:style/Theme.Material.Light.NoActionBar", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    private const int PickVrfRequestCode = 1001;

    private WebView _webView = null!;
    private TextView _statusText = null!;
    private ProgressBar _progressBar = null!;
    private Button _btnLoadReplay = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.main);

        _webView = FindViewById<WebView>(Resource.Id.webView)!;
        _statusText = FindViewById<TextView>(Resource.Id.statusText)!;
        _progressBar = FindViewById<ProgressBar>(Resource.Id.progressBar)!;
        _btnLoadReplay = FindViewById<Button>(Resource.Id.btnLoadReplay)!;

        WebSettings settings = _webView.Settings!;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true; // the viewer uses localStorage for saved preferences
        settings.AllowFileAccess = true;

        _webView.SetWebViewClient(new HideWebUploadUiClient());
        _webView.LoadUrl("file:///android_asset/viewer/index.html");

        _btnLoadReplay.Click += (_, _) =>
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*"); // .vrf has no registered MIME type on Android
            StartActivityForResult(intent, PickVrfRequestCode);
        };
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickVrfRequestCode || resultCode != Result.Ok || data?.Data is null)
        {
            return;
        }

        global::Android.Net.Uri uri = data.Data;
        _ = ParseAndLoadAsync(uri);
    }

    private async Task ParseAndLoadAsync(global::Android.Net.Uri uri)
    {
        SetBusy(true, "Copying file...");

        string jobDir = Path.Combine(CacheDir!.AbsolutePath, "vrf-job", Guid.NewGuid().ToString("N"));
        string inputPath = Path.Combine(jobDir, "input", "replay.vrf");
        string exportDir = Path.Combine(jobDir, "export");
        string outputDir = Path.Combine(jobDir, "output");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);

            await using (Stream? input = ContentResolver!.OpenInputStream(uri))
            await using (FileStream output = File.Create(inputPath))
            {
                if (input is null)
                {
                    SetBusy(false, "Couldn't open that file.");
                    return;
                }
                await input.CopyToAsync(output);
            }

            string? vrfkitPath = ApplicationInfo?.NativeLibraryDir is string dir
                ? Path.Combine(dir, "libvrfkit.so")
                : null;

            if (vrfkitPath is null || !File.Exists(vrfkitPath))
            {
                SetBusy(false, "vrfkit binary missing from this build (see android/README.md).");
                return;
            }

            var logTail = new List<string>();
            void CaptureLine(string line)
            {
                logTail.Add(line);
                if (logTail.Count > 200) logTail.RemoveAt(0);
            }

            FullPipeline.FullPipelineResult result = await FullPipeline.RunAsync(
                new FullPipeline.FullPipelineOptions(
                    VrfFilePath: inputPath,
                    VrfkitExePath: vrfkitPath,
                    ExportDirectory: exportDir,
                    OutputDirectory: outputDir,
                    AnalysisOptions: new AnalysisPipelineOptions()), // defaults: no vision cones, 10Hz movement -- matches VrfInsights.Web
                onStatus: line => RunOnUiThread(() => _statusText.Text = line),
                onLogLine: CaptureLine);

            if (!result.ExportSucceeded)
            {
                string tail = logTail.Count > 0 ? " — " + logTail[^1] : "";
                SetBusy(false, $"vrfkit couldn't decode this file (exit code {result.ExportExitCode}){tail}");
                return;
            }

            string bundleJson = BuildBundleJson(outputDir);
            RunOnUiThread(() =>
            {
                _webView.EvaluateJavascript($"loadFromDataBundle({bundleJson});", null);
                SetBusy(false, "Loaded.");
            });
        }
        catch (Exception ex)
        {
            SetBusy(false, "Failed: " + ex.Message);
        }
        finally
        {
            TryDeleteDirectory(jobDir);
        }
    }

    /// <summary>
    /// Concatenates the raw JSON text AnalysisPipeline already wrote for each output file into
    /// one <c>{"match": ..., "movement": ..., ...}</c> object literal, keyed exactly the way
    /// viewer/app.js's loadFromDataBundle() expects -- the same key set VrfInsights.Web's
    /// ReadOutputBundle uses. No JSON parsing needed here: each file is already valid JSON, so
    /// splicing the raw bytes in as-is is both simpler and cheaper than round-tripping through a
    /// parsed representation just to re-serialize it unchanged.
    /// </summary>
    private static string BuildBundleJson(string outputDir)
    {
        (string Key, string FileName)[] files =
        [
            ("match", "match.json"),
            ("movement", "movement.json"),
            ("utility", "utility.json"),
            ("ability_casts", "ability_casts.json"),
            ("vision_cones", "vision_cones.json"),
            ("events", "events.json"),
            ("economy", "economy.json"),
            ("combat_interactions", "combat_interactions.json"),
            ("shots", "shots.json"),
            ("hits", "hits.json"),
            ("armor_purchases", "armor_purchases.json"),
        ];

        var sb = new StringBuilder("{");
        bool first = true;
        foreach ((string key, string fileName) in files)
        {
            string path = Path.Combine(outputDir, fileName);
            if (!File.Exists(path)) continue;

            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(key).Append("\":").Append(File.ReadAllText(path));
        }
        sb.Append('}');
        return sb.ToString();
    }

    private void SetBusy(bool busy, string status)
    {
        RunOnUiThread(() =>
        {
            _progressBar.Visibility = busy ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone;
            _btnLoadReplay.Enabled = !busy;
            _statusText.Text = status;
        });
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort cleanup, same as VrfInsights.Web */ }
    }

    /// <summary>
    /// Hides viewer/index.html's own #loadPanel (the file-upload / backend-URL UI) once the page
    /// finishes loading -- this app drives loading entirely through the native "Load Replay"
    /// button + ParseAndLoadAsync above, so that UI would otherwise sit there doing nothing.
    /// Done by injected script rather than by editing index.html, so the viewer stays identical
    /// whether it's opened standalone, served by VrfInsights.Web, or hosted in this app.
    /// </summary>
    private sealed class HideWebUploadUiClient : WebViewClient
    {
        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            view?.EvaluateJavascript(
                "(function(){var p=document.getElementById('loadPanel'); if(p) p.style.display='none';})();",
                null);
        }
    }
}
