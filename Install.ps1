param([Parameter(Mandatory = $true)][string]$GameDir)
$ErrorActionPreference = 'Stop'
$resolvedGame = (Resolve-Path -LiteralPath $GameDir).Path
if (-not ((Test-Path -LiteralPath (Join-Path $resolvedGame 'Barotrauma.exe')) -or
          (Test-Path -LiteralPath (Join-Path $resolvedGame 'DedicatedServer.exe')))) {
    throw 'Choose the Barotrauma game folder.'
}
& (Join-Path $PSScriptRoot 'tools\Assert-LuaCsInstallation.ps1') -GameDir $resolvedGame
$destination = Join-Path $resolvedGame 'LocalMods\ReboundGuard_Experimental_041'
if (Test-Path -LiteralPath $destination) { throw 'Destination already exists. This installer will not overwrite it.' }
New-Item -ItemType Directory -Path (Join-Path $resolvedGame 'LocalMods') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LocalMod\ReboundGuard') -Destination $destination -Recurse
Write-Output "Copied to $destination"
Write-Output 'Disable old Rebound Guard builds. Enable Rebound Guard 0.4.1 (Experimental) and restart. See README-0.4.1.zh-CN.md.'
