param([string]$GameDir='E:\steam\steamapps\common\Barotrauma')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$path=Join-Path $root 'research\deep\environment.json'
$manifest=Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable
foreach($entry in $manifest.files) {
    $actual=(Get-FileHash -LiteralPath (Join-Path $GameDir $entry.file) -Algorithm SHA256).Hash
    if($actual -ne $entry.sha256){throw ('Installed dependency changed after the audited baseline: '+$entry.file)}
}
function FileList([string]$directory) {
    @(Get-ChildItem -LiteralPath $directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        @{path=[IO.Path]::GetRelativePath($directory,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
$manifest.mod_version='0.4.0'
$manifest.date_utc=[DateTime]::UtcNow.ToString('o')
$manifest.mod_files=FileList (Join-Path $root 'LocalMod\ReboundGuard')
$manifest.test_files=FileList (Join-Path $root 'tests')
$manifest.compat_files=FileList (Join-Path $root 'compat')
$manifest.script_files=@(@('Install.ps1','Install-All.ps1','Install-KnownModFixes.ps1','Collect-Reports.ps1') | ForEach-Object {
    @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $root $_)).Hash}
})
$manifest.validation_files=@(@('validation-client.json','validation-server.json') | ForEach-Object {
    @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $root ('research\deep\'+$_))).Hash}
})
$manifest.known_mod_original_source_sha256=@{
    mudraptor_shared_lua='631DCB17A38167AFEFDD6A0D64678F1D90314DB8AB6554CCF7CFA4E612C973B0'
    tsm_accessoriesbuff_lua='DDA5991659E6B1FE0398E612FC3D92317427E004AE79ED6B3CCAF24847AF77BE'
}
$manifest.note='Installed audited game dependencies rechecked unchanged. Isolated method tests, no new live combat or host crash reproduction. No game binaries in release.'
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8
Write-Output 'PASS: unchanged native dependency hashes and 0.4.0 code/test/compat/script/report manifest written.'
