param([Parameter(Mandatory=$true)][string]$GameDir,[string[]]$PackageDir)
$ErrorActionPreference='Stop'
$resolved=(Resolve-Path -LiteralPath $GameDir).Path
if(-not((Test-Path -LiteralPath (Join-Path $resolved 'Barotrauma.exe')) -or (Test-Path -LiteralPath (Join-Path $resolved 'DedicatedServer.exe')))){throw 'Choose a game/server directory.'}
& (Join-Path $PSScriptRoot 'tools\Assert-LuaCsInstallation.ps1') -GameDir $resolved
if(-not $PackageDir) {
    $config=Join-Path $resolved 'config_player.xml'
    if(-not(Test-Path -LiteralPath $config)){throw 'No config_player.xml. Pass -PackageDir with the active Mudraptor/TSM package folders.'}
    $xml=[xml][IO.File]::ReadAllText($config)
    $PackageDir=@($xml.SelectNodes('//contentpackages/regularpackages/package') | ForEach-Object {
        $file=$_.path
        if(-not [IO.Path]::IsPathRooted($file)){$file=Join-Path $resolved $file}
        if(Test-Path -LiteralPath $file){[IO.Path]::GetDirectoryName($file)}
    } | Select-Object -Unique)
}
if(-not $PackageDir){throw 'No active content package folders found. Pass -PackageDir explicitly.'}
& (Join-Path $PSScriptRoot 'Install-KnownModFixes.ps1') -PackageDir $PackageDir
$destination=Join-Path $resolved 'LocalMods\ReboundGuard_Experimental_041'
if(-not(Test-Path -LiteralPath $destination)){& (Join-Path $PSScriptRoot 'Install.ps1') -GameDir $resolved}
else {Write-Output 'Rebound Guard destination already exists; not overwritten.'}
Write-Output 'Enable Rebound Guard 0.4.1, disable every older Rebound Guard build, and fully restart. Apply the same known-mod fixes on all players and the actual server.'
