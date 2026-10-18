$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$release = Join-Path $root 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$destination = Join-Path $release 'ReboundGuard_Experimental_0.4.0.zip'
$manifest=Get-Content -Raw -LiteralPath (Join-Path $root 'research\deep\environment.json') | ConvertFrom-Json
if($manifest.mod_version -ne '0.4.0'){throw 'Evidence manifest version mismatch'}
foreach($group in @(@{Entries=$manifest.mod_files;Base='LocalMod\ReboundGuard'},@{Entries=$manifest.test_files;Base='tests'},@{Entries=$manifest.compat_files;Base='compat'},@{Entries=$manifest.script_files;Base=''},@{Entries=$manifest.validation_files;Base='research\deep'})) {
    foreach($entry in $group.Entries) {
        $file=Join-Path (Join-Path $root $group.Base) $entry.path
        if((Get-FileHash -LiteralPath $file).Hash -ne $entry.sha256){throw ('Evidence manifest mismatch: '+$file)}
    }
}
$staging = Join-Path $root ('build\release-stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
# Explicit allowlist: do not redistribute game DLLs, decompiler packages,
# external source downloads, saves or private mod configuration.
foreach ($name in @('LocalMod','tests','tools','compat','Install.ps1','Install-All.ps1','Install-KnownModFixes.ps1','Collect-Reports.ps1','README.zh-CN.md','README-0.4.zh-CN.md','README-0.3.1.zh-CN.md','第二轮分析与通用优化-20261018.md','实战日志分析-20261018.md','底层分析与工坊对照.md','研究测试摘要.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $staging -Recurse
}
$evidence = Join-Path $staging 'research\deep'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
foreach ($name in @('validation-client.json','validation-server.json','download-manifest.json','environment.json')) {
    Copy-Item -LiteralPath (Join-Path $root ('research\deep\' + $name)) -Destination $evidence
}
$fieldEvidence = Join-Path $staging 'research\field-test-20261018'
New-Item -ItemType Directory -Path $fieldEvidence -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'research\field-test-20261018\analysis.json') -Destination $fieldEvidence
$round2 = Join-Path $staging 'research\field-test-round2'
New-Item -ItemType Directory -Path $round2 -Force | Out-Null
foreach ($name in @('analysis.json','health.json','allocation-summary.json')) {
    Copy-Item -LiteralPath (Join-Path $root ('research\field-test-round2\'+$name)) -Destination $round2
}
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $destination -Force
$archive = [IO.Compression.ZipFile]::OpenRead($destination)
try {
    if ($archive.Entries | Where-Object { $_.FullName -match '\.(dll|exe|sub|save|log|nettrace|etlx)$' }) { throw 'Unexpected binary/save/raw report in archive' }
    foreach ($required in @('NearbyItemIndex.cs','PatchSafety.cs','StepHealth.cs','ParallelConstraintPreparation.cs','DiagnosticLog.cs','LuaBridgeOptimizer.cs','LoadingLifecyclePatch.cs','ExitLifetimePatch.cs','NativeModHotPaths.cs','CrashEvidence.cs','LuaBridgeTests.cs','LoadingLifecycleTests.cs','NativeModHotPathTests.cs','Collect-Reports.ps1','Install-All.ps1','Mudraptor-IsHuman.lua','第二轮分析与通用优化-20261018.md','validation-server.json','allocation-summary.json')) {
        if (-not ($archive.Entries | Where-Object { $_.Name -eq $required })) { throw "Missing archive entry: $required" }
    }
    $entries = $archive.Entries.Count
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $release 'SHA256-0.4.0.txt'), "$hash  ReboundGuard_Experimental_0.4.0.zip`r`n")
Write-Output "PASS: release contains $entries allowlisted entries; no game binaries or saves."
Write-Output $destination
Write-Output "SHA256: $hash"
