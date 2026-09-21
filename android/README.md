# VRF Insights — Android

A fully on-device Android port of the 2D replay viewer: pick a `.vrf` file, it's decoded by a
bundled copy of [vrfkit](https://github.com/yakisoba0728/vrfkit) and analyzed by this project's
own **unmodified** `VrfInsights.Analysis`/`VrfInsights.Data`/`VrfInsights.Pipeline`, and the
result is handed straight to the existing `viewer/` (map, scrubber, round nav, utility/ability
icons, everything) running in a WebView. No server, no network permission, nothing leaves the
phone.

## Why it's built this way

This app is a **`.NET for Android`** project (the modern name for Xamarin.Android — a normal
target of the .NET SDK, not a separate ecosystem), specifically so it can add
`VrfInsights.Data`/`VrfInsights.Analysis`/`VrfInsights.Pipeline` as ordinary `ProjectReference`s —
**zero lines of the analysis logic were ported or rewritten.** The same classes the desktop
CLI/GUI/server already call (`FullPipeline`, `VrfkitExportRunner`, `AnalysisPipeline`, ...) run
here unchanged. The only new code is `MainActivity.cs`: a file picker, a WebView, and glue that
calls `FullPipeline.RunAsync` and passes its output to the page's own `loadFromDataBundle()` —
the exact function `VrfInsights.Web`'s browser-upload path already calls today.

`viewer/` itself is used as-is; the only change made to it *at runtime* (not on disk) is hiding
its `#loadPanel` upload UI, since this app drives loading natively instead. See `MainActivity.cs`
for the one-line injected script that does that.

## One-time setup (on your own Windows machine — not this sandbox)

1. **.NET 10 SDK**: https://dotnet.microsoft.com/download
2. **Android workload**: `dotnet workload install android` — this alone pulls in a JDK and the
   Android SDK/build-tools/platform this app needs. Android Studio is not required (fine if you
   have it anyway).
3. **Rust + cargo-ndk**, only to build vrfkit itself:
   ```
   rustup target add aarch64-linux-android
   cargo install cargo-ndk
   ```
   `cargo-ndk` needs the Android NDK, which is usually already present once you've installed the
   Android workload (under `%LOCALAPPDATA%\Android\Sdk\ndk\<version>\`) or via Android Studio's
   SDK Manager → SDK Tools → NDK.

## Building

```powershell
cd android\scripts
.\build-android.ps1
```

This does everything: fetches map/agent/weapon art from valorant-api.com if you haven't already
(same `scripts/Fetch-Assets.ps1` the desktop app uses), copies `viewer/` into the app's `Assets/`,
clones + cross-compiles vrfkit for `arm64-v8a` if `lib/arm64-v8a/libvrfkit.so` isn't already
there, and builds the `.apk`. Re-run it any time you pull changes; each step skips work it's
already done (pass `-Force` to redo everything).

Install it on a connected device: `adb install -r <path the script prints>`.

## What's genuinely unverified — read this before assuming it works end-to-end

I wrote and wired all of this in a sandboxed environment with **no Android SDK, no way to
download one (Google's Maven/`dl.google.com` and Maven Central are both blocked by this
sandbox's network policy), and consequently no way to actually build or run this project myself.**
Everything above is real, reused, unmodified code wired together correctly as far as static
reading of both codebases can confirm — but it has not been through a compiler or a device. Three
specific risk areas, roughly in order of how likely they are to bite:

1. **Parquet.Net's native codec coverage on Android.** `VrfInsights.Data` reads vrfkit's Parquet
   output via the `Parquet.Net` NuGet package. If vrfkit writes any column with a compression
   codec that library resolves via a platform-native codec (Snappy/ZSTD are the usual suspects
   for some .NET Parquet libraries), and that package doesn't ship an `android-arm64` runtime
   asset for it, reading will throw at runtime even though everything builds fine. If you hit
   this: check whether vrfkit's `export` command has an uncompressed/codec flag, or whether
   `Parquet.Net`'s GitHub issues mention Android/mobile support — I couldn't check either from
   here.
2. **vrfkit cross-compiles cleanly for `aarch64-linux-android`.** Good news going in: the whole
   workspace is pure Rust (`#![forbid(unsafe_code)]` in every vrfkit crate) and its one
   Oodle-decompression dependency, `oozextract`, is also a pure-Rust reimplementation — no native
   C library to cross-link, which is normally the thing that breaks an NDK cross-compile. I read
   this from vrfkit's own README/Cargo.toml but never actually ran the build.
3. **`System.Diagnostics.Process` launching a bundled `lib/arm64-v8a/libvrfkit.so` executable
   from `ApplicationInfo.NativeLibraryDir`.** This is a known, documented pattern for shipping a
   CLI tool inside an Android app (it's how several existing apps bundle native binaries — files
   under `nativeLibraryDir` keep their execute bit on API 29+ where arbitrary app-private files
   don't), and `ExternalProcessRunner`/`VrfkitExportRunner` are already OS-agnostic
   (`System.Diagnostics.Process`, no shell, no Windows-specific paths). But I haven't run it on an
   actual device.

None of these are guesses I'm hiding — they're the three places this design leans on something I
could reason about correctly but not verify by executing it. If you hit an error in any of them,
it'll be an easy, specific fix (a missing runtime asset, a linker flag, a permission tweak), not a
sign the architecture is wrong.

## What's deliberately out of scope right now

- **x86_64 / emulator support** — only `arm64-v8a` is built. Add `-t x86_64` to the `cargo ndk`
  invocation in `build-android.ps1` and a matching `lib/x86_64/libvrfkit.so` + `RuntimeIdentifiers`
  entry in the `.csproj` if you need to run this in the Android emulator rather than a real phone.
- **App icon** — `AndroidManifest.xml` has no `android:icon` so the project builds without a
  mipmap resource set I couldn't verify. Add one after your first successful build.
- **Release signing** — `dotnet build -c Release` produces a build signed with a debug key,
  installable via `adb install` but not distributable as-is. Fine for personal/community
  sideloading; ask if you want a proper release-signing setup (your own keystore) added.
