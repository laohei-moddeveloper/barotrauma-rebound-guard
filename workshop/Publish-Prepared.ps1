#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDir,
    [string]$PackageDir=(Join-Path $PSScriptRoot 'publish/ReboundGuard')
)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Workshop upload requires the installed Windows Steam SDK and the owning Steam account. This script does not launch the game.'}
# This entry point explicitly publishes the documented experimental snapshot.
& (Join-Path $PSScriptRoot 'Publish-Workshop.ps1') -Action Publish -GameDir $GameDir -PackageDir $PackageDir -ResearchSnapshot
& (Join-Path $PSScriptRoot 'Update-Localizations.ps1') -GameDir $GameDir
Write-Output 'Published existing item 3822098302; Chinese and English metadata independently read back. No game client or server was launched.'
