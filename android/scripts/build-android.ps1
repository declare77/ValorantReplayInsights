#requires -Version 5.1
<#
.SYNOPSIS
    One-shot build script for the Android app: fetches map/agent art (if missing), copies the
    viewer into the app's Assets, cross-compiles vrfkit for arm64 Android (if missing), then
    builds the .apk.

.DESCRIPTION
    Run this from a normal PowerShell prompt on Windows, from anywhere -- it locates everything
    relative to this script's own path. Safe to re-run; each step skips work that's already done
    unless -Force is passed for that step.

    Prerequisites (one-time):
      1. .NET 10 SDK - https://dotnet.microsoft.com/download
      2. Android workload:      dotnet workload install android
         (this alone pulls in a JDK + the Android SDK/build-tools/platform it needs -- you do NOT
         need Android Studio installed, though it's fine if you have it)
      3. Rust + the Android target + cargo-ndk, only needed the first time (to build vrfkit):
           rustup target add aarch64-linux-android
           cargo install cargo-ndk
         cargo-ndk needs the Android NDK on your machine. If you installed the Android workload
         above, one usually already exists under
         %LOCALAPPDATA%\Android\Sdk\ndk\<version>\ -- this script tries to find it automatically
         via $env:ANDROID_NDK_HOME / $env:ANDROID_SDK_ROOT. If it can't, install the NDK via
         Android Studio's SDK Manager (SDK Tools tab -> NDK) once, or set $env:ANDROID_NDK_HOME
         yourself.

.PARAMETER Force
    Re-run every step (re-fetch art, rebuild vrfkit) even if its output already exists.

.PARAMETER SkipVrfkit
    Skip building vrfkit (use this if you already dropped a working libvrfkit.so in
    android/VrfInsights.Android/lib/arm64-v8a/ yourself).

.EXAMPLE
    .\build-android.ps1
#>
param(
    [switch]$Force,
    [switch]$SkipVrfkit
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # .../ValorantReplayInsights
$androidProj = Join-Path $root 'android\VrfInsights.Android'
$assetsOut = Join-Path $androidProj 'Assets\viewer'
$libOut = Join-Path $androidProj 'lib\arm64-v8a'

Write-Host "== 1/4: map/agent/weapon art ==" -ForegroundColor Cyan
$catalogPath = Join-Path $root 'assets\catalog.js'
if ($Force -or -not (Test-Path $catalogPath)) {
    & (Join-Path $root 'scripts\Fetch-Assets.ps1') @(if ($Force) { '-Force' })
} else {
    Write-Host "  assets/catalog.js already exists, skipping (pass -Force to redo)."
}

Write-Host "== 2/4: copy viewer into Assets ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $assetsOut, (Join-Path $assetsOut '..\assets') | Out-Null
Copy-Item (Join-Path $root 'viewer\*') $assetsOut -Recurse -Force
# index.html references ../assets/catalog.js (one level above viewer/) -- mirror that layout
# inside Assets/ so the relative path still resolves under file:///android_asset/.
Copy-Item (Join-Path $root 'assets\*') (Join-Path $assetsOut '..\assets') -Recurse -Force
Write-Host "  copied viewer/ + assets/ -> android/VrfInsights.Android/Assets/"

Write-Host "== 3/4: vrfkit (arm64 Android) ==" -ForegroundColor Cyan
$vrfkitOut = Join-Path $libOut 'libvrfkit.so'
if ($SkipVrfkit) {
    Write-Host "  -SkipVrfkit passed, leaving whatever's already at $vrfkitOut"
} elseif ((Test-Path $vrfkitOut) -and -not $Force) {
    Write-Host "  already built, skipping (pass -Force to rebuild)."
} else {
    $vrfkitSrc = Join-Path $root '..\vrfkit'
    if (-not (Test-Path $vrfkitSrc)) {
        Write-Host "  cloning vrfkit next to this project (https://github.com/yakisoba0728/vrfkit)..."
        git clone https://github.com/yakisoba0728/vrfkit.git $vrfkitSrc
    }

    $ndk = $env:ANDROID_NDK_HOME
    if (-not $ndk) {
        $sdkRoot = $env:ANDROID_SDK_ROOT
        if (-not $sdkRoot) { $sdkRoot = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
        $ndkParent = Join-Path $sdkRoot 'ndk'
        if (Test-Path $ndkParent) {
            $ndk = (Get-ChildItem $ndkParent | Sort-Object Name -Descending | Select-Object -First 1).FullName
        }
    }
    if (-not $ndk -or -not (Test-Path $ndk)) {
        throw "Couldn't find the Android NDK. Install it via Android Studio's SDK Manager (SDK Tools -> NDK), or set `$env:ANDROID_NDK_HOME` to its path, then re-run."
    }
    Write-Host "  using NDK: $ndk"
    $env:ANDROID_NDK_HOME = $ndk

    Push-Location $vrfkitSrc
    try {
        Write-Host "  cargo ndk build (this is a big workspace with LTO on -- first build can take several minutes)..."
        & cargo ndk -t arm64-v8a -o (Join-Path $vrfkitSrc 'target\ndk-out') build --release -p vrfkit --features export --locked
        if ($LASTEXITCODE -ne 0) { throw "cargo ndk build failed (exit $LASTEXITCODE) -- see output above." }
    } finally {
        Pop-Location
    }

    New-Item -ItemType Directory -Force -Path $libOut | Out-Null
    Copy-Item (Join-Path $vrfkitSrc 'target\ndk-out\arm64-v8a\vrfkit') $vrfkitOut -Force
    Write-Host "  built -> $vrfkitOut"
}

Write-Host "== 4/4: dotnet build ==" -ForegroundColor Cyan
Push-Location $androidProj
try {
    & dotnet build -c Release -f net10.0-android
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE) -- see output above." }
} finally {
    Pop-Location
}

$apk = Get-ChildItem (Join-Path $androidProj 'bin\Release\net10.0-android') -Recurse -Filter '*-Signed.apk' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $apk) {
    $apk = Get-ChildItem (Join-Path $androidProj 'bin\Release\net10.0-android') -Recurse -Filter '*.apk' -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($apk) {
    Write-Host ""
    Write-Host "Done: $($apk.FullName)" -ForegroundColor Green
    Write-Host "Install on a connected device/emulator with: adb install -r `"$($apk.FullName)`""
} else {
    Write-Host ""
    Write-Host "Build finished but no .apk was found under bin\Release\net10.0-android -- check the build output above." -ForegroundColor Yellow
}
