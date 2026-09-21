#requires -Version 5.1
<#
.SYNOPSIS
    Downloads map, agent, and per-ability icon artwork from Riot's own public content mirror
    (https://valorant-api.com) for the 2D replay viewer (viewer/index.html).

.DESCRIPTION
    This project's own sandbox environment cannot reach valorant-api.com directly (its outbound
    network policy blocks it), so this script exists to do that part on YOUR machine instead,
    which has normal internet access. It only downloads Riot's own already-public artwork and
    metadata — nothing here touches a .vrf file or vrfkit.

    Run it once. It writes into an "assets" folder next to this project's root (a sibling of
    this script's own "scripts" folder, and of "viewer"):
      assets/maps/<map-uuid>.png                     - each competitive map's minimap image
      assets/agents/<agent-uuid>.png                 - each agent's icon
      assets/abilities/<agent-uuid>/<slot>.png        - each agent's per-ability icon (slot is
                                                        valorant-api's own name: ability1,
                                                        ability2, grenade, ultimate, passive --
                                                        NOT this project's internal codename/slot
                                                        tokens, which don't reliably map to these)
      assets/weapons/<weapon-uuid>.png                - each weapon's icon (real name/cost/uuid,
                                                        all straight from valorant-api.com -- see
                                                        "Honesty about what's verified vs.
                                                        assumed": the viewer does NOT yet show a
                                                        player's actual equipped weapon anywhere,
                                                        since that needs a class-path-to-weapon
                                                        codename table this project doesn't have
                                                        confirmed evidence for; this just fetches
                                                        the real reference data/art ahead of that)
      assets/catalog.json             - combined metadata, for reference/debugging
      assets/catalog.js               - the same data as a plain `window.VRF_CATALOG = {...}`
                                         script — viewer/index.html loads this file directly
                                         (browsers refuse to `fetch()` local JSON from a page
                                         opened as a plain file, but a <script src> tag works
                                         fine, so this is what actually gets used)

    Each map's catalog entry also gets a small precomputed "alpha mask" (a 48x48 opaque/
    transparent grid sampled from its own downloaded image, via .NET's System.Drawing here on
    YOUR machine) baked right into catalog.js alongside it. The viewer's automatic map-orientation
    calibration (see the README) needs to know which parts of the map image are transparent
    padding versus the real playable map, but reading a local image's pixels back out of a canvas
    is something browsers flatly refuse to do for a page opened as a plain file (same restriction
    as the fetch() one above, just enforced at the canvas API instead) — so that pixel reading has
    to happen here, once, rather than in the browser every time you load a match.

    Safe to re-run any time — already-downloaded files are skipped unless -Force is passed, and
    the catalog (alpha masks included) is always rewritten so name changes are picked up. If you
    pull an update to this project that adds the alpha-mask feature, just re-run this script (no
    -Force needed, since the images themselves haven't changed) to backfill masks for maps you
    already downloaded.

.PARAMETER Force
    Re-download every image even if it's already present locally.

.EXAMPLE
    .\Fetch-Assets.ps1
.EXAMPLE
    .\Fetch-Assets.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Older Windows PowerShell defaults to TLS 1.0, which valorant-api.com's host will reject.
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {
    Write-Warning "Couldn't force TLS 1.2 (continuing anyway): $($_.Exception.Message)"
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptRoot
$assetsRoot = Join-Path $projectRoot 'assets'
$mapsDir = Join-Path $assetsRoot 'maps'
$agentsDir = Join-Path $assetsRoot 'agents'
$abilitiesDir = Join-Path $assetsRoot 'abilities'
$weaponsDir = Join-Path $assetsRoot 'weapons'
New-Item -ItemType Directory -Force -Path $mapsDir | Out-Null
New-Item -ItemType Directory -Force -Path $agentsDir | Out-Null
New-Item -ItemType Directory -Force -Path $abilitiesDir | Out-Null
New-Item -ItemType Directory -Force -Path $weaponsDir | Out-Null

$failures = New-Object System.Collections.Generic.List[string]

function Get-RemoteJson {
    # CI runners occasionally hit a transient blip (timeout / rate limit / DNS hiccup) talking to
    # valorant-api.com, and unlike Save-Image below (which already tolerates a failed image and
    # just logs it), a failure here happens before there's anything to iterate over at all --
    # so previously it took the whole script down immediately with no retry. Small exponential
    # backoff here keeps a one-off blip from failing the entire GitHub Actions build.
    param([string]$Url, [int]$MaxAttempts = 4)
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Write-Host "Fetching $Url ... (attempt $attempt/$MaxAttempts)"
            return (Invoke-RestMethod -Uri $Url -UseBasicParsing).data
        } catch {
            if ($attempt -eq $MaxAttempts) { throw }
            $delaySeconds = [Math]::Pow(2, $attempt)
            Write-Warning "  fetch failed ($($_.Exception.Message)) -- retrying in ${delaySeconds}s..."
            Start-Sleep -Seconds $delaySeconds
        }
    }
}

function Save-Image {
    param(
        [string]$Url,
        [string]$DestinationPath,
        [string]$Label
    )

    if ((Test-Path $DestinationPath) -and -not $Force) {
        Write-Host "  [skip] $Label (already downloaded)"
        return $true
    }

    try {
        Invoke-WebRequest -Uri $Url -OutFile $DestinationPath -UseBasicParsing | Out-Null
        Write-Host "  [ok]   $Label"
        return $true
    } catch {
        Write-Warning "  [fail] $Label - $($_.Exception.Message)"
        $failures.Add($Label) | Out-Null
        return $false
    }
}

$alphaMaskAvailable = $true
try {
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop
} catch {
    $alphaMaskAvailable = $false
    Write-Warning "System.Drawing isn't available on this machine ($($_.Exception.Message)) -- map images will still download, but automatic orientation calibration in the viewer will fall back to its (more limited) in-browser method for every map."
}

# A coarse opaque/transparent grid read from a map's own downloaded image, flattened row-major
# into a string of '1' (opaque -- real map) / '0' (transparent -- padding outside the map's actual
# shape) characters. GridSize x GridSize samples is coarse enough to stay fast (a few thousand
# GetPixel calls per map) while still being plenty precise for what the viewer uses it for: telling
# whether a *recorded player/utility position* landed on the map or in the padding around it, not
# rendering anything pixel-perfect.
function Get-AlphaMask {
    param(
        [string]$ImagePath,
        [int]$GridSize = 48
    )
    if (-not $alphaMaskAvailable) { return $null }
    if (-not (Test-Path $ImagePath)) { return $null }

    $bmp = $null
    try {
        $bmp = [System.Drawing.Bitmap]::new($ImagePath)
        $w = $bmp.Width
        $h = $bmp.Height
        $sb = New-Object System.Text.StringBuilder ($GridSize * $GridSize)
        for ($row = 0; $row -lt $GridSize; $row++) {
            $py = [Math]::Min($h - 1, [int]((($row + 0.5) / $GridSize) * $h))
            for ($col = 0; $col -lt $GridSize; $col++) {
                $px = [Math]::Min($w - 1, [int]((($col + 0.5) / $GridSize) * $w))
                $pixel = $bmp.GetPixel($px, $py)
                if ($pixel.A -gt 40) { [void]$sb.Append('1') } else { [void]$sb.Append('0') }
            }
        }
        return $sb.ToString()
    } catch {
        Write-Warning "  [mask] couldn't read pixels from $ImagePath for alpha-mask calibration data - $($_.Exception.Message)"
        return $null
    } finally {
        if ($bmp) { $bmp.Dispose() }
    }
}

Write-Host ""
Write-Host "=== Maps ==="
$allMaps = Get-RemoteJson -Url 'https://valorant-api.com/v1/maps'

# Only maps with a real minimap coordinate transform are competitive/matchmaking maps -
# training/Skirmish/HURM entries valorant-api.com also returns don't have these set.
$competitiveMaps = $allMaps | Where-Object { $null -ne $_.xMultiplier -and $null -ne $_.yMultiplier -and $_.displayIcon }

$MAP_ALPHA_MASK_GRID_SIZE = 48
$mapCatalog = @()
foreach ($map in $competitiveMaps) {
    $dest = Join-Path $mapsDir "$($map.uuid).png"
    $ok = Save-Image -Url $map.displayIcon -DestinationPath $dest -Label "map: $($map.displayName)"
    $alphaMask = $null
    $alphaMaskSize = 0
    if ($ok) {
        $alphaMask = Get-AlphaMask -ImagePath $dest -GridSize $MAP_ALPHA_MASK_GRID_SIZE
        if ($alphaMask) {
            $alphaMaskSize = $MAP_ALPHA_MASK_GRID_SIZE
            Write-Host "  [mask] computed alpha mask for $($map.displayName)"
        }
    }
    $mapCatalog += [ordered]@{
        uuid          = $map.uuid
        displayName   = $map.displayName
        image         = "maps/$($map.uuid).png"
        imageAvailable = $ok
        xMultiplier   = $map.xMultiplier
        yMultiplier   = $map.yMultiplier
        xScalarToAdd  = $map.xScalarToAdd
        yScalarToAdd  = $map.yScalarToAdd
        alphaMask     = $alphaMask
        alphaMaskSize = $alphaMaskSize
    }
}

Write-Host ""
Write-Host "=== Agents ==="
$allAgents = Get-RemoteJson -Url 'https://valorant-api.com/v1/agents?isPlayableCharacter=true&language=en-US'

$agentCatalog = @()
foreach ($agent in $allAgents) {
    $dest = Join-Path $agentsDir "$($agent.uuid).png"
    $ok = Save-Image -Url $agent.displayIcon -DestinationPath $dest -Label "agent: $($agent.displayName)"

    # Per-ability icons, keyed by valorant-api's own slot name (ability1/ability2/grenade/
    # ultimate/passive) -- NOT this project's internal per-agent codename/slot tokens (see
    # UtilityEffectClassifier.ExtractDescriptiveKeyword's doc comment), which is why the viewer
    # matches markers to abilities by comparing text (DescriptiveKeyword vs displayName/
    # description below), not by slot name.
    $agentAbilitiesDir = Join-Path $abilitiesDir $agent.uuid
    $abilityCatalog = @()
    foreach ($ability in $agent.abilities) {
        if (-not $ability.displayIcon) {
            # Some slots (e.g. a passive with no standalone icon) legitimately have none.
            continue
        }
        $slot = $ability.slot.ToLowerInvariant()
        New-Item -ItemType Directory -Force -Path $agentAbilitiesDir | Out-Null
        $abilityDest = Join-Path $agentAbilitiesDir "$slot.png"
        $abilityOk = Save-Image -Url $ability.displayIcon -DestinationPath $abilityDest -Label "ability: $($agent.displayName) / $($ability.displayName)"
        $abilityCatalog += [ordered]@{
            slot           = $ability.slot
            displayName    = $ability.displayName
            description    = $ability.description
            image          = "abilities/$($agent.uuid)/$slot.png"
            imageAvailable = $abilityOk
        }
    }

    $agentCatalog += [ordered]@{
        uuid           = $agent.uuid
        displayName    = $agent.displayName
        image          = "agents/$($agent.uuid).png"
        imageAvailable = $ok
        abilities      = $abilityCatalog
    }
}

Write-Host ""
Write-Host "=== Weapons ==="
# valorant-api.com's own bulk /v1/weapons response already returns only the ~18-19 real base
# weapons (Classic through Odin, plus Melee) as top-level data[] entries -- unlike agents there's
# no isPlayableCharacter-style flag needed, since skins live nested under each weapon's own
# `skins[]` array (which this deliberately never reads: it's dozens of cosmetic variants per gun,
# irrelevant here and large enough that a naive tool reading the whole response back at once can
# choke on it -- Invoke-RestMethod handles it fine either way).
$allWeapons = Get-RemoteJson -Url 'https://valorant-api.com/v1/weapons?language=en-US'

$weaponCatalog = @()
foreach ($weapon in $allWeapons) {
    if (-not $weapon.displayIcon) { continue }
    $dest = Join-Path $weaponsDir "$($weapon.uuid).png"
    $ok = Save-Image -Url $weapon.displayIcon -DestinationPath $dest -Label "weapon: $($weapon.displayName)"
    $weaponCatalog += [ordered]@{
        uuid           = $weapon.uuid
        displayName    = $weapon.displayName
        # e.g. "EEquippableCategory::Rifle" -> "Rifle" -- valorant-api's own enum-style prefix,
        # stripped for a plain readable label; the full raw string isn't needed for anything here.
        category       = ($weapon.category -replace '^EEquippableCategory::', '')
        cost           = if ($weapon.shopData) { $weapon.shopData.cost } else { $null }
        image          = "weapons/$($weapon.uuid).png"
        imageAvailable = $ok
    }
}

$catalog = [ordered]@{
    fetchedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    source       = 'https://valorant-api.com'
    maps         = $mapCatalog
    agents       = $agentCatalog
    weapons      = $weaponCatalog
}

$catalogJson = $catalog | ConvertTo-Json -Depth 6

$catalogJsonPath = Join-Path $assetsRoot 'catalog.json'
Set-Content -Path $catalogJsonPath -Value $catalogJson -Encoding UTF8

# viewer/index.html is opened directly as a local file (file://), and browsers block a page
# loaded that way from fetch()-ing local JSON (a CORS restriction on file:// origins) even
# though a plain <script src="..."> tag loads a local file just fine. So the viewer actually
# reads this .js file, not catalog.json above.
$catalogJsPath = Join-Path $assetsRoot 'catalog.js'
Set-Content -Path $catalogJsPath -Value "window.VRF_CATALOG = $catalogJson;" -Encoding UTF8

Write-Host ""
Write-Host "Wrote $catalogJsonPath"
Write-Host "Wrote $catalogJsPath"
$abilityIconCount = ($agentCatalog | ForEach-Object { $_.abilities.Count } | Measure-Object -Sum).Sum
$maskedMapCount = ($mapCatalog | Where-Object { $_.alphaMaskSize -gt 0 } | Measure-Object).Count
Write-Host "Maps downloaded: $($mapCatalog.Count)   Agents downloaded: $($agentCatalog.Count)   Ability icons downloaded: $abilityIconCount   Weapons downloaded: $($weaponCatalog.Count)"
Write-Host "Maps with an alpha mask for automatic orientation calibration: $maskedMapCount / $($mapCatalog.Count)"
if ($maskedMapCount -lt $mapCatalog.Count) {
    Write-Warning "Some maps are missing an alpha mask (see [mask]/[fail] lines above) -- the viewer's automatic orientation calibration will fall back to its in-browser method for those, which most browsers block for a page opened as a plain file. The manual Map orientation controls always work regardless."
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Warning "$($failures.Count) download(s) failed:"
    $failures | ForEach-Object { Write-Warning "  - $_" }
    Write-Warning "Re-run this script to retry just the missing ones (already-downloaded files are skipped)."
} else {
    Write-Host ""
    Write-Host "All assets downloaded successfully. Open viewer/index.html to use the 2D replay viewer."
}
