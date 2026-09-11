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
      assets/catalog.json             - combined metadata, for reference/debugging
      assets/catalog.js               - the same data as a plain `window.VRF_CATALOG = {...}`
                                         script — viewer/index.html loads this file directly
                                         (browsers refuse to `fetch()` local JSON from a page
                                         opened as a plain file, but a <script src> tag works
                                         fine, so this is what actually gets used)

    Safe to re-run any time — already-downloaded files are skipped unless -Force is passed, and
    the catalog is always rewritten so name changes are picked up.

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
New-Item -ItemType Directory -Force -Path $mapsDir | Out-Null
New-Item -ItemType Directory -Force -Path $agentsDir | Out-Null
New-Item -ItemType Directory -Force -Path $abilitiesDir | Out-Null

$failures = New-Object System.Collections.Generic.List[string]

function Get-RemoteJson {
    param([string]$Url)
    Write-Host "Fetching $Url ..."
    return (Invoke-RestMethod -Uri $Url -UseBasicParsing).data
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

Write-Host ""
Write-Host "=== Maps ==="
$allMaps = Get-RemoteJson -Url 'https://valorant-api.com/v1/maps'

# Only maps with a real minimap coordinate transform are competitive/matchmaking maps -
# training/Skirmish/HURM entries valorant-api.com also returns don't have these set.
$competitiveMaps = $allMaps | Where-Object { $null -ne $_.xMultiplier -and $null -ne $_.yMultiplier -and $_.displayIcon }

$mapCatalog = @()
foreach ($map in $competitiveMaps) {
    $dest = Join-Path $mapsDir "$($map.uuid).png"
    $ok = Save-Image -Url $map.displayIcon -DestinationPath $dest -Label "map: $($map.displayName)"
    $mapCatalog += [ordered]@{
        uuid          = $map.uuid
        displayName   = $map.displayName
        image         = "maps/$($map.uuid).png"
        imageAvailable = $ok
        xMultiplier   = $map.xMultiplier
        yMultiplier   = $map.yMultiplier
        xScalarToAdd  = $map.xScalarToAdd
        yScalarToAdd  = $map.yScalarToAdd
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

$catalog = [ordered]@{
    fetchedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    source       = 'https://valorant-api.com'
    maps         = $mapCatalog
    agents       = $agentCatalog
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
Write-Host "Maps downloaded: $($mapCatalog.Count)   Agents downloaded: $($agentCatalog.Count)   Ability icons downloaded: $abilityIconCount"

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Warning "$($failures.Count) download(s) failed:"
    $failures | ForEach-Object { Write-Warning "  - $_" }
    Write-Warning "Re-run this script to retry just the missing ones (already-downloaded files are skipped)."
} else {
    Write-Host ""
    Write-Host "All assets downloaded successfully. Open viewer/index.html to use the 2D replay viewer."
}
