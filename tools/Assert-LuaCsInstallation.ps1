param([Parameter(Mandatory=$true)][string]$GameDir)
$ErrorActionPreference='Stop'
# Current LuaCs uses CsRunPolicy/IsCsEnabledForSession, not the old setup XML.
# Saved policy and a client session's prompt answer belong to the running game;
# this source installer must never enable C# or rewrite either configuration.
$settings=Join-Path $GameDir 'LocalMods\LuaCsForBarotrauma\Config\SettingsShared.xml'
if(Test-Path -LiteralPath $settings) {
    $xml=[xml][IO.File]::ReadAllText($settings)
    if($xml.SelectSingleNode('/Configuration/Settings/Setting[@Name="CsRunPolicy"]')) {
        Write-Output 'Current LuaCs settings detected. Enable/approve C# in LuaCs before enabling this mod; runtime policy was not changed.'
        return
    }
}
$legacy=Join-Path $GameDir 'LuaCsSetupConfig.xml'
if(-not(Test-Path -LiteralPath $legacy)){throw 'Install compatible LuaCs first. Current LuaCs SettingsShared.xml or legacy LuaCsSetupConfig.xml was not found.'}
$xml=[xml][IO.File]::ReadAllText($legacy)
if($xml.LuaCsSetupConfig.EnableCsScripting -ne 'true'){throw 'Enable C# scripting in LuaCs first.'}
