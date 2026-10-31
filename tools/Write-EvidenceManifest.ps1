param([string]$ValidationDir='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$xml=[xml][IO.File]::ReadAllText((Join-Path $root 'LocalMod\ReboundGuard\filelist.xml'))
$version=$xml.contentpackage.modversion
if(-not $ValidationDir){$ValidationDir=Join-Path $root ('docs\validation\'+$version)}
$path=Join-Path $ValidationDir 'manifest.json'
$required=@('client-all.json','server-all.json','client-dispatch.json','server-dispatch.json','client-queue.json','server-queue.json','client-compatibility.json','server-compatibility.json','client-stress.json')
foreach($name in $required) {
    $report=Get-Content -Raw -LiteralPath (Join-Path $ValidationDir $name) | ConvertFrom-Json
    if(-not $report.harness_context -or $report.harness_context.source_changed_during_run){throw "Missing or stale harness provenance: $name"}
    foreach($entry in $report.harness_context.source_sha256.PSObject.Properties) {
        if((Get-FileHash -LiteralPath (Join-Path $root $entry.Name)).Hash -ne $entry.Value){throw "Source no longer matches $name : $($entry.Name)"}
    }
}
$stress=Get-Content -Raw -LiteralPath (Join-Path $ValidationDir 'client-stress.json') | ConvertFrom-Json
if(-not $stress.complete -or $stress.elapsed_seconds -lt $stress.requested_seconds){throw 'The requested stress verification has not completed'}
$files=[Collections.Generic.List[object]]::new()
function AddFile([string]$relative) {
    $relative=$relative.Replace('\','/')
    if($relative -eq [IO.Path]::GetRelativePath($root,$path).Replace('\','/')){return}
    $full=Join-Path $root $relative
    if((Get-Item -LiteralPath $full).LinkType){throw "Symlink not allowed in release inventory: $relative"}
    $files.Add(@{path=$relative;sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash})
}
foreach($folder in @('LocalMod\ReboundGuard','tests','tools','compat')) {
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $root $folder) -File -Recurse | Sort-Object FullName) {
        if($file.Extension -notin @('.cs','.ps1','.py','.xml','.lua')){continue}
        AddFile ([IO.Path]::GetRelativePath($root,$file.FullName))
    }
}
foreach($file in @('Install.ps1','Install-All.ps1','Install-KnownModFixes.ps1','Collect-Reports.ps1','README.md','README.zh-CN.md','README-0.4.1.zh-CN.md','README-0.4.zh-CN.md','docs/HANDOFF.md','docs/OPTIMIZATION-0.4.1.zh-CN.md')){AddFile $file}
foreach($file in $required + @('client-scaffold.json','server-scaffold.json','server-stress.json','compatibility-audit.json','interrupted-endurance.json')){AddFile ([IO.Path]::GetRelativePath($root,(Join-Path $ValidationDir $file)))}
$manifest=[ordered]@{mod_version=$version;game_version=$xml.contentpackage.gameversion;date_utc=[DateTime]::UtcNow.ToString('o');files=@($files | Sort-Object path);note='Public source/test/tooling and synthetic validation only. Native Windows gameplay, rendering and live multiplayer A/B are not established by these isolated Linux-host runs. Historical 0.4.0 evidence is unchanged.'}
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
Write-Output "PASS: $($files.Count) public release files tied to current source and the actual completed stress duration."
