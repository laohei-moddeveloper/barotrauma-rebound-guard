#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$GameDir,
    [string]$ServerGameDir,
    [ValidateNotNullOrEmpty()][ValidateSet('Client','Server')][string[]]$Side=@('Client','Server'),
    [ValidateSet('All','Bridge','Native','Loading','StatusTags','Nearby','Lifecycle','Queue','Compatibility','Dispatch','Scaffold','Stress')][string]$Suite='All',
    [switch]$CompileOnly,
    [switch]$PrepareOnly
)
$ErrorActionPreference='Stop'
if(-not $PrepareOnly -and -not $IsWindows) {
    throw 'Native offline verification requires Windows. PrepareOnly may inspect/copy inputs on other systems; it does not compile or run tests.'
}
$repoRoot=Split-Path $PSScriptRoot -Parent
$commonDependencies=@(
    'BarotraumaCore.dll','Publicized/BarotraumaCore.dll',
    'coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll',
    'Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll','Basic.Reference.Assemblies.Net80.dll',
    '0Harmony.dll','Farseer.NetStandard.dll','XNATypes.dll','MoonSharp.Interpreter.dll','FluentResults.dll'
)
$targets=@()
$requestedSides=@($Side | Select-Object -Unique)
foreach($targetSide in $requestedSides) {
    $directory=$GameDir
    if($targetSide -eq 'Server') {
        if($ServerGameDir) { $directory=$ServerGameDir }
        elseif($requestedSides.Count -ne 1) {
            throw 'Supply -ServerGameDir for a two-sided run. The client root may contain a different vanilla DedicatedServer.dll; never assume it is the LuaCs server.'
        }
    }
    $directory=[IO.Path]::GetFullPath($directory)
    $assembly=if($targetSide -eq 'Client'){'Barotrauma.dll'}else{'DedicatedServer.dll'}
    $required=@($commonDependencies)+@($assembly,('Publicized/'+$assembly))
    $hostName=[IO.Path]::GetFileNameWithoutExtension($assembly)
    $required+=@(($hostName+'.exe'),($hostName+'.runtimeconfig.json'),($hostName+'.deps.json'))
    if($targetSide -eq 'Client') { $required+='MonoGame.Framework.Windows.NetStandard.dll' }
    $missing=@($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $directory $_) -PathType Leaf) })
    if($missing.Count -ne 0) { throw ($targetSide+' missing installed game/LuaCs dependencies: '+($missing -join ', ')) }
    $targets+=@{side=$targetSide;game_dir=$directory;required=$required;hashes=@()}
}

# Every invocation gets its own source/build/runtime/report directories. Never
# rename, delete or reuse an existing harness, checkout, game file or save.
$runName='offline-'+[DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff')+'-'+[Guid]::NewGuid().ToString('N')
$runRoot=Join-Path (Join-Path $repoRoot 'build') $runName
$snapshotRoot=Join-Path $runRoot 'workspace'
$reportRoot=Join-Path $runRoot 'reports'
$logRoot=Join-Path $runRoot 'logs'
foreach($folder in @($snapshotRoot,$reportRoot,$logRoot)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
$manifestPath=Join-Path $runRoot 'manifest.json'
$manifest=[ordered]@{
    format=1; status='preparing'; started_utc=[DateTime]::UtcNow.ToString('o')
    prepare_only=[bool]$PrepareOnly; compile_only=[bool]$CompileOnly; suite=$Suite
    source_files=@(); dependency_sets=@(); inputs_unchanged=$null; checks=@()
    note='Offline build/isolated TestRunner only. No game client or server entry point. Preparation verifies file presence/hashes, not binary compatibility. No live multiplayer or rendering validation.'
}
function Save-Manifest {
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}
function Copy-SourceFile([string]$path) {
    $relative=[IO.Path]::GetRelativePath($repoRoot,$path)
    $destination=Join-Path $snapshotRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $path -Destination $destination
    # Hash the actual snapshot tested, not the live file that could be edited.
    $manifest.source_files+=@{path=$relative;sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash}
}
try {
    foreach($target in $targets) {
        foreach($relative in $target.required) {
            $target.hashes+=@{path=$relative;sha256=(Get-FileHash -LiteralPath (Join-Path $target.game_dir $relative) -Algorithm SHA256).Hash}
        }
        $manifest.dependency_sets+=@{side=$target.side;game_dir=$target.game_dir;hashes=$target.hashes}
    }
    # Explicit source allowlist. Private handoffs, raw reports, old runtimes,
    # binaries, installed mod packages and all game content stay out of snapshot.
    foreach($source in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'LocalMod/ReboundGuard/CSharp') -Recurse -File -Filter '*.cs' | Sort-Object FullName) {
        Copy-SourceFile $source.FullName
    }
    foreach($source in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tests') -File -Filter '*.cs' | Sort-Object FullName) {
        Copy-SourceFile $source.FullName
    }
    foreach($source in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'compat') -File -Filter '*.lua' | Sort-Object FullName) {
        Copy-SourceFile $source.FullName
    }
    foreach($relative in @('tools/Verify.ps1','tools/Verify-Deep.ps1')) { Copy-SourceFile (Join-Path $repoRoot $relative) }
    Save-Manifest
    if($PrepareOnly) {
        $manifest.status='prepared_not_tested'
        Write-Output "Prepared only; no compilation or test process started: $manifestPath"
    } else {
        $pwshPath=Join-Path $PSHOME 'pwsh.exe'
        foreach($target in $targets) {
            $targetSide=$target.side; $installedDir=$target.game_dir
            $report=Join-Path $reportRoot ('validation-'+$targetSide.ToLowerInvariant()+'.json')
            $log=Join-Path $logRoot ($targetSide.ToLowerInvariant()+'.txt')
            $check=[ordered]@{side=$targetSide;game_dir=$installedDir;status='running';log=$log;report=$null;exit_code=$null}
            $manifest.checks+=,$check
            $manifest.status='running'; Save-Manifest
            $childArguments=@('-NoProfile','-NonInteractive','-File')
            if($CompileOnly) {
                $childArguments+=@((Join-Path $snapshotRoot 'tools/Verify.ps1'),'-GameDir',$installedDir,'-Side',$targetSide,'-CompileOnly')
            } else {
                $childArguments+=@((Join-Path $snapshotRoot 'tools/Verify-Deep.ps1'),'-GameDir',$installedDir,'-Side',$targetSide,'-Suite',$Suite,'-ReportPath',$report)
            }
            Push-Location $snapshotRoot
            try { & $pwshPath @childArguments *> $log; $childExit=$LASTEXITCODE }
            finally { Pop-Location }
            $check.exit_code=$childExit
            if($childExit -ne 0) { $check.status='failed'; throw "$targetSide offline verification failed; see $log" }
            if(-not $CompileOnly) {
                if(-not (Test-Path -LiteralPath $report -PathType Leaf)) { $check.status='failed'; throw "$targetSide exited without a report; see $log" }
                Get-Content -LiteralPath $report -Raw | ConvertFrom-Json | Out-Null
                $check.report=$report
            }
            $check.status=if($CompileOnly){'compiled'}else{'passed'}
            Save-Manifest
        }
        $manifest.status=if($CompileOnly){'compiled_not_executed'}else{'passed_offline_only'}
    }
    $manifest.inputs_unchanged=$true
    foreach($target in $targets) {
        foreach($entry in $target.hashes) {
            if((Get-FileHash -LiteralPath (Join-Path $target.game_dir $entry.path) -Algorithm SHA256).Hash -ne $entry.sha256) {
                $manifest.inputs_unchanged=$false
            }
        }
    }
    if(-not $manifest.inputs_unchanged) { throw 'Installed dependency bytes changed during verification. Results are invalid; no attempt was made to restore or overwrite game files.' }
} catch {
    $manifest.status='failed'; $manifest.error=$_.Exception.Message
    throw
} finally {
    $manifest.finished_utc=[DateTime]::UtcNow.ToString('o')
    Save-Manifest
    Write-Output "Offline validation manifest: $manifestPath"
}
