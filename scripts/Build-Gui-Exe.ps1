#requires -Version 5.1
<#
.SYNOPSIS
    Builds VrfInsights.Gui into a single, standalone, double-clickable .exe — so you don't need
    a .bat file or a `dotnet run` command to launch it.

.DESCRIPTION
    `dotnet run --project src/VrfInsights.Gui` (see the README) is fine for development, but it
    requires a terminal and the .NET SDK to be present every time. This script instead runs
    `dotnet publish` with the settings that produce ONE self-contained .exe: the .NET runtime is
    bundled inside it, so the machine that eventually double-clicks it doesn't even need the SDK
    (or any .NET runtime) installed.

    The result is written to:
        publish\gui\VrfInsights.Gui.exe

    That file is standalone — copy it anywhere (a desktop shortcut, a pinned taskbar icon, a USB
    stick) and it still runs. Nothing here touches a .vrf file or vrfkit; it only builds this
    project's existing WinForms GUI (src/VrfInsights.Gui) into a different output shape.

.PARAMETER Shortcut
    Also create a "VRF Insights.lnk" shortcut on your Desktop pointing at the built .exe.

.EXAMPLE
    .\Build-Gui-Exe.ps1
.EXAMPLE
    .\Build-Gui-Exe.ps1 -Shortcut
#>
[CmdletBinding()]
param(
    [switch]$Shortcut
)

$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptRoot
$guiProject = Join-Path $projectRoot 'src\VrfInsights.Gui\VrfInsights.Gui.csproj'
$publishDir = Join-Path $projectRoot 'publish\gui'

if (-not (Test-Path $guiProject)) {
    throw "Couldn't find $guiProject — run this script from inside the project (or leave it in scripts\)."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK ('dotnet') isn't on PATH. Install it from https://dotnet.microsoft.com/download first -- building the .exe still needs the SDK once, even though the .exe it produces won't."
}

Write-Host "Publishing $guiProject as a single-file, self-contained win-x64 .exe ..."
Write-Host "(this bundles the whole .NET runtime in, so first run may take a little longer to build than a normal 'dotnet run')"
Write-Host ""

dotnet publish $guiProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (see output above) — the .exe was not created."
}

$exePath = Join-Path $publishDir 'VrfInsights.Gui.exe'
if (-not (Test-Path $exePath)) {
    throw "Publish reported success but $exePath is missing — something unexpected happened."
}

Write-Host ""
Write-Host "Built: $exePath"

if ($Shortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shortcutPath = Join-Path $desktop 'VRF Insights.lnk'
    $wshShell = New-Object -ComObject WScript.Shell
    $lnk = $wshShell.CreateShortcut($shortcutPath)
    $lnk.TargetPath = $exePath
    $lnk.WorkingDirectory = $publishDir
    $lnk.Description = 'VALORANT Replay Insights'
    $lnk.Save()
    Write-Host "Desktop shortcut created: $shortcutPath"
}

Write-Host ""
Write-Host "Double-click VrfInsights.Gui.exe (or the desktop shortcut) to launch the GUI from now on"
Write-Host "-- no terminal, no .bat file, no 'dotnet run' needed. Re-run this script any time you"
Write-Host "pull code changes, to rebuild the .exe."
