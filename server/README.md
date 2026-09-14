# Hosted parsing backend (upload a `.vrf`, skip the local CLI/GUI step)

This is the piece that lets the 2D replay viewer accept a `.vrf` file directly in the browser,
instead of a person running `vrf-insights`/the GUI locally and picking a folder of JSON files.
It's `src/VrfInsights.Web` — a small ASP.NET Core API that wraps the exact same
`VrfInsights.Pipeline` code the desktop CLI/GUI already call. **It still never opens a `.vrf` file
or contains any decoding logic itself** — it saves the upload, launches `vrfkit` as an external
process (via `FullPipeline`/`VrfkitExportRunner`, unchanged), runs the existing analysis pipeline
on vrfkit's own output, and returns the resulting JSON in the HTTP response. Same rule this whole
project has followed from the start, just reachable over HTTP now instead of only from a local
CLI/GUI.

## How it fits together

```
Browser (viewer/index.html)
   │  POST a .vrf file to /api/parse
   ▼
Firebase Hosting  ──rewrite──▶  Cloud Run (this Dockerfile)
                                   │  launches vrfkit.exe as a subprocess (baked into the image)
                                   │  runs VrfInsights.Analysis on its output
                                   ▼
                                returns one JSON object back to the browser,
                                which feeds straight into the existing viewer
```

Nothing is persisted server-side — each request does its work in a scratch temp directory and
deletes it before responding. That also means there's no "come back later and re-watch without
re-uploading" — every viewing session re-uploads and re-parses. Fine for how this is used today;
worth revisiting if that ever feels wrong.

## Building and testing locally

You need Docker (simplest, matches production exactly) or Rust 1.86+ and .NET 10 SDK installed
directly.

**With Docker:**

```bash
docker build -t vrf-insights-web .
docker run --rm -p 8080:8080 vrf-insights-web
# in another terminal:
curl -F "vrf=@/path/to/a/replay.vrf" http://localhost:8080/api/parse | head -c 500
```

**Without Docker** (if you already have vrfkit built from an earlier desktop-app setup):

```bash
export VRFKIT_EXE_PATH=$HOME/.local/share/VrfInsights/vrfkit-src/target/release/vrfkit  # adjust to your OS's actual path -- see VrfkitBootstrapper.DefaultInstallDirectory()
dotnet run --project src/VrfInsights.Web
```

## Deploying to Cloud Run

Requires the [`gcloud` CLI](https://cloud.google.com/sdk/docs/install) signed in to the same
Google Cloud project your Firebase project uses (Firebase projects *are* GCP projects — same
project ID).

```bash
gcloud run deploy vrf-insights-web \
  --source . \
  --region us-central1 \
  --allow-unauthenticated \
  --memory 2Gi \
  --cpu 2 \
  --timeout 180 \
  --max-instances 5 \
  --set-env-vars MAX_UPLOAD_MB=100
```

Notes on those flags:
- `--source .` tells `gcloud` to build the `Dockerfile` at the repo root itself — no separate
  `docker push` step needed.
- `--allow-unauthenticated` — required for a public upload page. If you'd rather gate access,
  see "Access control" below instead of removing this.
- `--memory 2Gi --cpu 2` — comfortably more than vrfkit's own measured ~109MB peak; adjust down
  later if you want to shave a fraction of a cent off the per-parse cost once you've seen it run
  for real.
- `--timeout 180` — Cloud Run's own per-request ceiling; `Program.cs` has its own internal 4-minute
  cap, so this flag is the one that actually matters. Raise both together if a very long match
  ever needs more time.
- `--max-instances 5` — caps how many parses can run *at the same time*, which is a simple, blunt
  guard against a burst of concurrent uploads driving cost/load up quickly. Not a monthly spend
  cap (see "Tripwires" below for that) — just a concurrency ceiling.

The command prints a `*.run.app` URL when it finishes — that's your backend's real address, for
testing directly (`curl`, or pasting into the viewer's "Backend URL" field) before wiring the
Hosting rewrite below.

## Wiring it behind Firebase Hosting (recommended)

This makes the browser call a same-origin `/api/parse` (no CORS involved, and the viewer's default
"Backend URL" field of `/api/parse` just works with no configuration). Add a rewrite to your
`firebase.json`:

```json
{
  "hosting": {
    "public": "viewer",
    "rewrites": [
      {
        "source": "/api/parse",
        "run": { "serviceId": "vrf-insights-web", "region": "us-central1" }
      }
    ]
  }
}
```

Then `firebase deploy --only hosting`. If you'd rather call the Cloud Run URL directly instead
(skipping this step), the viewer's "Backend URL" field under **Upload a .vrf** already supports
pasting the full `https://...run.app` address instead — the API already sends permissive CORS
headers for exactly that case.

## Access control

The Cloud Run deploy above (`--allow-unauthenticated`) is open to anyone with the URL. Options if
you want to gate it, roughly in order of effort:
- **Simple shared secret**: check for a fixed header/query value in `Program.cs` before doing any
  work, and bake the value into the viewer's fetch call. Quick, but the secret lives in
  client-side JS, so it only deters casual/automated abuse, not a determined person.
- **Firebase Auth**: require a signed-in user, verify their ID token in `Program.cs` (Cloud Run
  supports this without much ceremony). More setup, real per-user identity.
- **Cloud Run's own IAM-based auth** (drop `--allow-unauthenticated`): tightest option, but then
  only callers with a Google identity/service account can reach it at all — not appropriate for a
  public upload page unless every intended user already has one.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | `8080` | Set automatically by Cloud Run; `Program.cs` binds Kestrel to it directly. |
| `VRFKIT_EXE_PATH` | *(falls back to `VrfkitBootstrapper.FindExisting()`)* | Path to the vrfkit binary. The `Dockerfile` sets this to `/app/vrfkit/vrfkit`, where it bakes the binary in. |
| `MAX_UPLOAD_MB` | `100` | Upload size ceiling — typical full matches are 40–70MB per vrfkit's own docs. |

## Cost tripwires (do this before making the URL public)

Covered in more depth in chat, but as a checklist:
1. Firebase console → gear icon → **Usage and billing** → **Modify plan** → **Blaze**. This is
   required for Cloud Run regardless of expected cost — Spark blocks it outright.
2. Right after upgrading, Firebase prompts you to set a budget alert — do it.
3. For an actual hard stop (not just an email), go to
   [console.cloud.google.com/billing/budgets](https://console.cloud.google.com/billing/budgets) →
   **Create new budget** → **Spend cap enforcement**, scoped to Cloud Run, with a small dollar
   amount. When crossed, Google automatically pauses the service for the rest of the month until
   you manually lift it — a real safety net, not just a warning.
4. `--max-instances` on the `gcloud run deploy` command above is a second, independent guard
   against a *burst* of concurrent uploads specifically (separate from the monthly spend cap).

## Honesty about what's untested here

Same spirit as the rest of this project's README: this backend was written and reasoned through
carefully, but **not run against a real Cloud Run deployment or a real `.vrf` file** in the
environment this was built in (no Docker/cloud credentials available there). Before trusting it
with real traffic:
- Run the local Docker build-and-curl test above against a real replay file.
- Watch Cloud Run's own logs (`gcloud run services logs read vrf-insights-web`) on the first few
  real uploads — `Program.cs` logs every vrfkit/analysis status line, so a failure should be
  diagnosable there without adding anything.
- Time a real request end-to-end and compare it against the cost estimate from chat — that
  estimate leaned on vrfkit's own published benchmark plus a guess for the .NET analysis step, not
  a real measurement of this exact backend.
- **Known, unlikely-to-matter gap**: if a request ever hits the 4-minute internal timeout in
  `Program.cs`, it stops waiting on vrfkit but doesn't kill vrfkit's own process (a property of
  `ExternalProcessRunner`, shared with the desktop CLI/GUI, which don't have this concern the same
  way a long-lived server does). Given vrfkit decodes in well under a second per its own published
  benchmark, this should be rare-to-never in practice — but if Cloud Run logs ever show repeated
  timeouts, that's the first place to look.
