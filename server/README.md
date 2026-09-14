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

It also serves the viewer webpage itself (`viewer/`, baked into the image — see the `Dockerfile`),
so **one deployed container is the whole site** — no separate static host needed.

## How it fits together

```
Browser
   │  GET /              → the viewer webpage (served by this same container)
   │  POST /api/parse    → a .vrf file
   ▼
This container (Render, or any other Docker host)
   │  launches vrfkit as a subprocess (baked into the image)
   │  runs VrfInsights.Analysis on its output
   ▼
returns one JSON object back to the browser, which feeds straight into the viewer
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
# then open http://localhost:8080 in a browser, or test the API directly:
curl -F "vrf=@/path/to/a/replay.vrf" http://localhost:8080/api/parse | head -c 500
```

**Without Docker** (if you already have vrfkit built from an earlier desktop-app setup):

```bash
export VRFKIT_EXE_PATH=$HOME/.local/share/VrfInsights/vrfkit-src/target/release/vrfkit  # adjust to your OS's actual path -- see VrfkitBootstrapper.DefaultInstallDirectory()
dotnet run --project src/VrfInsights.Web
```
(this path won't serve the viewer webpage itself — `wwwroot/viewer` only exists once the Docker
build copies it in — so use the Docker route above if you want to test the whole site, not just
the API.)

## Deploying to Render (recommended — no card, no hold)

Google Cloud's billing setup puts a temporary authorization hold on your card just to verify it,
even before anything would ever actually cost money — [Render](https://render.com) doesn't require
a card at all for its free tier, and can run this same Docker image as-is. The one tradeoff: a
free Render web service spins down after 15 minutes with no traffic and takes about a minute to
wake back up on the next request — a non-issue for occasional personal use, since you're not
paying to keep it warm 24/7 anyway.

1. Push this repo to GitHub if it isn't already there (Render deploys from a connected repo).
2. Go to [dashboard.render.com](https://dashboard.render.com/) → sign up (no card needed) →
   **New +** → **Web Service**.
3. Connect your GitHub account/repo when prompted.
4. Render should auto-detect the root `Dockerfile` and offer **Docker** as the runtime — confirm
   that (don't let it guess a different runtime).
5. Under **Instance Type**, pick **Free**.
6. Under **Environment Variables**, add `MAX_UPLOAD_MB` = `100` (optional — this is already the
   default; only add it if you want a different limit).
7. Click **Create Web Service**. The first build takes a while (it's compiling vrfkit from source
   plus the .NET backend) — Render streams the build log live so you can watch it.
8. Once it's live, Render gives you a URL like `https://your-service-name.onrender.com` — open it
   directly in a browser. That's the whole site: the viewer loads, and its upload form already
   points at the right place with zero configuration (same-origin, since this one service serves
   both).

That's it — no Firebase, no Blaze plan, no billing account needed anywhere in this path. Your
separate notes app on Firebase is completely untouched.

## Alternative: Cloud Run (only if you outgrow Render's free tier)

Cloud Run avoids the 15-minute spin-down and scales further, at the cost of needing a Blaze
billing account (and its card-verification hold) on a Google Cloud project — worth it later if
this gets real, frequent traffic; not needed to get started.

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
  cap (see "Cost tripwires" below for that) — just a concurrency ceiling.

The command prints a `*.run.app` URL — that IS the whole site too (same container, same static
files), same as the Render URL above. A Firebase Hosting rewrite in front of it is optional, only
worth doing if you specifically want it to live at a Firebase-hosted domain instead of the raw
`*.run.app` one:

```json
{
  "hosting": {
    "rewrites": [
      { "source": "/api/parse", "run": { "serviceId": "vrf-insights-web", "region": "us-central1" } },
      { "source": "**", "run": { "serviceId": "vrf-insights-web", "region": "us-central1" } }
    ]
  }
}
```

### Cost tripwires (Cloud Run path only — skip entirely if you're using Render)

1. Firebase console → **Usage and billing** (`https://console.firebase.google.com/project/YOUR-PROJECT-ID/usage`)
   → **Modify plan** → **Blaze**. Required for Cloud Run regardless of expected cost — Spark
   blocks it outright. This is also where the card-verification hold happens.
2. Right after upgrading, Firebase prompts you to set a budget alert — do it.
3. For an actual hard stop (not just an email), go to
   [console.cloud.google.com/billing/budgets](https://console.cloud.google.com/billing/budgets) →
   **Create new budget** → **Spend cap enforcement**, scoped to Cloud Run, with a small dollar
   amount. When crossed, Google automatically pauses the service for the rest of the month until
   you manually lift it — a real safety net, not just a warning.
4. `--max-instances` on the `gcloud run deploy` command above is a second, independent guard
   against a *burst* of concurrent uploads specifically (separate from the monthly spend cap).

## Access control

Both deploy paths above are open to anyone with the URL by default. Options if you want to gate
it, roughly in order of effort:
- **Simple shared secret**: check for a fixed header/query value in `Program.cs` before doing any
  work, and bake the value into the viewer's fetch call. Quick, but the secret lives in
  client-side JS, so it only deters casual/automated abuse, not a determined person.
- **A real auth provider** (Firebase Auth, or anything else): require a signed-in user, verify
  their token in `Program.cs`. More setup, real per-user identity.
- **Cloud Run's own IAM-based auth** (drop `--allow-unauthenticated`, Cloud Run path only):
  tightest option, but then only callers with a Google identity/service account can reach it at
  all — not appropriate for a public upload page unless every intended user already has one.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | `8080` | Set automatically by Render/Cloud Run; `Program.cs` binds Kestrel to it directly. |
| `VRFKIT_EXE_PATH` | *(falls back to `VrfkitBootstrapper.FindExisting()`)* | Path to the vrfkit binary. The `Dockerfile` sets this to `/app/vrfkit/vrfkit`, where it bakes the binary in. |
| `MAX_UPLOAD_MB` | `100` | Upload size ceiling — typical full matches are 40–70MB per vrfkit's own docs. |

## Honesty about what's untested here

Same spirit as the rest of this project's README: this backend was written and reasoned through
carefully, but **not run against a real Render or Cloud Run deployment, or a real `.vrf` file**, in
the environment this was built in (no Docker/cloud credentials available there). Before trusting
it with real traffic:
- Run the local Docker build-and-curl test above against a real replay file.
- Watch the platform's own logs (Render's build/deploy log page, or
  `gcloud run services logs read vrf-insights-web`) on the first few real uploads —
  `Program.cs` logs every vrfkit/analysis status line, so a failure should be diagnosable there
  without adding anything.
- Time a real request end-to-end. For the Cloud Run path specifically, compare it against the cost
  estimate from chat — that estimate leaned on vrfkit's own published benchmark plus a guess for
  the .NET analysis step, not a real measurement of this exact backend.
- **Known, unlikely-to-matter gap**: if a request ever hits the 4-minute internal timeout in
  `Program.cs`, it stops waiting on vrfkit but doesn't kill vrfkit's own process (a property of
  `ExternalProcessRunner`, shared with the desktop CLI/GUI, which don't have this concern the same
  way a long-lived server does). Given vrfkit decodes in well under a second per its own published
  benchmark, this should be rare-to-never in practice — but if the logs ever show repeated
  timeouts, that's the first place to look.
