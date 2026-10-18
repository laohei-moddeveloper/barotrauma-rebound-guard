param(
    [string]$MudraptorSource='E:\steam\steamapps\common\Barotrauma\LocalMods\MudraptorGirl_CrashFix_1_0_0\Lua\Scripts\shared.lua',
    [string]$TSMSource="$env:LOCALAPPDATA\Daedalic Entertainment GmbH\Barotrauma\WorkshopMods\Installed\2852411866\Lua\Autorun\accessoriesbuff.lua"
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$testRoot=Join-Path $root ('build\known-mod-check-'+[Guid]::NewGuid().ToString('N'))
$packages=@((Join-Path $testRoot 'Mudraptor'),(Join-Path $testRoot 'TSM'))
$relative=@('Lua\Scripts\shared.lua','Lua\Autorun\accessoriesbuff.lua');$sources=@($MudraptorSource,$TSMSource)
for($i=0;$i -lt 2;$i++) {
    $target=Join-Path $packages[$i] $relative[$i]
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $sources[$i] -Destination $target
    [IO.File]::WriteAllText((Join-Path $packages[$i] 'filelist.xml'),('<contentpackage name="'+@('Mudraptor fixture','TSM fixture')[$i]+'" />'))
}
& (Join-Path $root 'Install-KnownModFixes.ps1') -PackageDir $packages
for($i=0;$i -lt 2;$i++) {
    $target=Join-Path $packages[$i] $relative[$i]
    if((Get-FileHash -LiteralPath ($target+'.ReboundGuard040.original')).Hash -ne (Get-FileHash -LiteralPath $sources[$i]).Hash){throw 'Original backup differs'}
    if(-not([IO.File]::ReadAllText($target).Contains('ReboundGuard native bridge v0.4.0'))){throw 'Bridge source missing'}
}
& (Join-Path $root 'Install-KnownModFixes.ps1') -PackageDir $packages
& (Join-Path $root 'Install-KnownModFixes.ps1') -PackageDir $packages -Restore
for($i=0;$i -lt 2;$i++) {
    if((Get-FileHash -LiteralPath (Join-Path $packages[$i] $relative[$i])).Hash -ne (Get-FileHash -LiteralPath $sources[$i]).Hash){throw 'Restore is not byte-exact'}
}
& (Join-Path $root 'Install-KnownModFixes.ps1') -PackageDir $packages
$changed=Join-Path $packages[1] $relative[1]
[IO.File]::AppendAllText($changed,'-- local edit')
$refused=$false
try {& (Join-Path $root 'Install-KnownModFixes.ps1') -PackageDir $packages -Restore}catch{$refused=$_.Exception.Message -match 'changed after'}
if(-not $refused){throw 'Restore overwrote local edits'}
$first=Join-Path $packages[0] $relative[0]
if(-not([IO.File]::ReadAllText($first).Contains('native bridge'))){throw 'Restore preflight partially changed first package'}
$game=Join-Path $testRoot 'combined-game';New-Item -ItemType Directory -Path $game | Out-Null
[IO.File]::WriteAllBytes((Join-Path $game 'Barotrauma.exe'),[byte[]]@())
[IO.File]::WriteAllText((Join-Path $game 'LuaCsSetupConfig.xml'),'<LuaCsSetupConfig EnableCsScripting="true" />')
$entries=''
for($i=0;$i -lt 2;$i++) {
    $package=Join-Path $game @('Mudraptor','TSM')[$i];$target=Join-Path $package $relative[$i]
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $sources[$i] -Destination $target
    [IO.File]::WriteAllText((Join-Path $package 'filelist.xml'),('<contentpackage name="'+@('Mudraptor fixture','TSM fixture')[$i]+'" />'))
    $entries+='<package path="'+([Security.SecurityElement]::Escape((Join-Path $package 'filelist.xml')))+'" />'
}
[IO.File]::WriteAllText((Join-Path $game 'config_player.xml'),('<config><contentpackages><regularpackages>'+$entries+'</regularpackages></contentpackages></config>'))
& (Join-Path $root 'Install-All.ps1') -GameDir $game
if(-not(Test-Path -LiteralPath (Join-Path $game 'LocalMods\ReboundGuard_Experimental_040\filelist.xml'))){throw 'Combined installer omitted Rebound Guard'}
Write-Output ('PASS: bridge source replacement, exact originals, idempotency, exact restore/reapply and refusal of edited files; real mods untouched. '+$testRoot)
