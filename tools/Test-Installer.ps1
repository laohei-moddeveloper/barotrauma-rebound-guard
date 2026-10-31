$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $root ('build\installer-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$mockGame = Join-Path $testRoot 'mock-game'
New-Item -ItemType Directory -Path $mockGame -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $mockGame 'Barotrauma.exe'), [byte[]]@())
[IO.File]::WriteAllText((Join-Path $mockGame 'LuaCsSetupConfig.xml'), '<LuaCsSetupConfig EnableCsScripting="true" />')
& (Join-Path $root 'Install.ps1') -GameDir $mockGame
$source = Join-Path $root 'LocalMod\ReboundGuard'
$destination = Join-Path $mockGame 'LocalMods\ReboundGuard_Experimental_041'
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $copy = Join-Path $destination $relative
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) { throw "Copy mismatch: $relative" }
}
$refused = $false
try { & (Join-Path $root 'Install.ps1') -GameDir $mockGame } catch { $refused = $_.Exception.Message -match 'already exists' }
if (-not $refused) { throw 'Installer failed to refuse overwrite' }
$invalid = Join-Path $testRoot 'invalid'
New-Item -ItemType Directory -Path $invalid -Force | Out-Null
$refused = $false
try { & (Join-Path $root 'Install.ps1') -GameDir $invalid } catch { $refused = $_.Exception.Message -match 'game folder' }
if (-not $refused) { throw 'Installer accepted an invalid game folder' }
$disabled = Join-Path $testRoot 'disabled'
New-Item -ItemType Directory -Path $disabled -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $disabled 'DedicatedServer.exe'), [byte[]]@())
[IO.File]::WriteAllText((Join-Path $disabled 'LuaCsSetupConfig.xml'), '<LuaCsSetupConfig EnableCsScripting="false" />')
$refused = $false
try { & (Join-Path $root 'Install.ps1') -GameDir $disabled } catch { $refused = $_.Exception.Message -match 'Enable C#' }
if (-not $refused -or (Test-Path -LiteralPath (Join-Path $disabled 'LocalMods'))) { throw 'Installer wrote into a C#-disabled mock game' }
$current=Join-Path $testRoot 'current-luacs'
$settings=Join-Path $current 'LocalMods\LuaCsForBarotrauma\Config\SettingsShared.xml'
New-Item -ItemType Directory -Path (Split-Path $settings -Parent) -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $current 'Barotrauma.exe'), [byte[]]@())
[IO.File]::WriteAllText($settings, '<Configuration><Settings><Setting Name="CsRunPolicy" Value="Prompt" /></Settings></Configuration>')
# A migrated legacy setting must not override the current runtime's policy.
[IO.File]::WriteAllText((Join-Path $current 'LuaCsSetupConfig.xml'), '<LuaCsSetupConfig EnableCsScripting="false" />')
$before=(Get-FileHash -LiteralPath $settings).Hash
& (Join-Path $root 'Install.ps1') -GameDir $current
if(-not(Test-Path -LiteralPath (Join-Path $current 'LocalMods\ReboundGuard_Experimental_041\filelist.xml'))){throw 'Current LuaCs source installation failed'}
if((Get-FileHash -LiteralPath $settings).Hash -ne $before){throw 'Installer changed current C# policy'}
Write-Output 'PASS: isolated installer copy/hash checks, overwrite refusal, invalid-folder refusal and disabled-C# refusal. Real game untouched.'
