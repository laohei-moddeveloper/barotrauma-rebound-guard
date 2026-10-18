param([string]$GameDir='E:\steam\steamapps\common\Barotrauma',[ValidateSet('Client','Server')][string]$Side='Client',[ValidateSet('All','Bridge','Native','Loading')][string]$Suite='All',[string]$ReportPath='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Verify.ps1') -GameDir $GameDir -Side $Side -CompileOnly
$runtime=Join-Path $build ('net8-harness-'+$Side.ToLowerInvariant())
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
# Local-only test runtime. Never distribute game binaries or this folder.
$installedDlls=@(Get-ChildItem -LiteralPath $GameDir -Filter '*.dll' -File)
$installedNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($dll in $installedDlls) { [void]$installedNames.Add($dll.Name) }
foreach($cached in Get-ChildItem -LiteralPath $runtime -Filter '*.dll' -File) {
    if($cached.Name -eq 'TestRunner.dll' -or $cached.Name -eq ('ReboundGuard.'+$Side+'.dll')) { continue }
    if(-not $installedNames.Contains($cached.Name)) {
        throw "Cached runtime DLL is absent from installed game: $($cached.FullName). Archive the old harness before testing this build."
    }
}
foreach($dll in $installedDlls) {
    $copy=Join-Path $runtime $dll.Name
    if(Test-Path -LiteralPath $copy) {
        if((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash) {
            throw "Cached runtime DLL differs from installed game: $copy. Archive the old harness before testing this build."
        }
    } else {
        Copy-Item -LiteralPath $dll.FullName -Destination $copy
    }
}
Copy-Item -LiteralPath $output -Destination (Join-Path $runtime ([IO.Path]::GetFileName($output))) -Force
Copy-Item -LiteralPath (Join-Path $root 'compat') -Destination $runtime -Recurse -Force
$exeBytes=[IO.File]::ReadAllBytes((Join-Path $GameDir 'Barotrauma.exe'))
$old=[Text.Encoding]::ASCII.GetBytes('Barotrauma.dll'+[char]0)
$new=[Text.Encoding]::ASCII.GetBytes('TestRunner.dll'+[char]0)
$matches=0
for($i=0;$i -le $exeBytes.Length-$old.Length;$i++) {
    if($exeBytes[$i] -ne $old[0]) {continue}
    $match=$true
    for($j=1;$j -lt $old.Length;$j++) {if($exeBytes[$i+$j] -ne $old[$j]) {$match=$false;break}}
    if($match) {[Array]::Copy($new,0,$exeBytes,$i,$new.Length);$matches++}
}
if($matches -ne 1) {throw "Unknown apphost shape: $matches"}
[IO.File]::WriteAllBytes((Join-Path $runtime 'TestRunner.exe'),$exeBytes)
$runtimeConfig=Get-Content -Raw -LiteralPath (Join-Path $GameDir 'Barotrauma.runtimeconfig.json') | ConvertFrom-Json -AsHashtable
$runtimeConfig.runtimeOptions.configProperties['System.Runtime.TieredCompilation']=$false
$runtimeConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runtime 'TestRunner.runtimeconfig.json') -Encoding utf8
$deps=[IO.File]::ReadAllText((Join-Path $GameDir 'Barotrauma.deps.json')).Replace('Barotrauma/1.13.4.0','TestRunner/1.0.0').Replace('Barotrauma.dll','TestRunner.dll')
[IO.File]::WriteAllText((Join-Path $runtime 'TestRunner.deps.json'),$deps)
$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($output))
$testTrees=[Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach($name in @('Bootstrap.cs','DeepRegression.cs','PhysicsBenchmark.cs','ConstraintSolverTests.cs','ConstraintPreparationTests.cs','NearbyItemIndexTests.cs','ContainerEffectTests.cs','WorldReplayTests.cs','WorldPerformanceTests.cs','PluginLifecycleTests.cs','FieldIterationTests.cs','NativeModHotPathTests.cs','LuaBridgeTests.cs','LoadingLifecycleTests.cs')) {
    $testTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $root ('tests\'+$name)))))
}
$testOptions=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$testOptions=$testOptions.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('TestRunner',$testTrees,$refs,$testOptions)
$stream=[IO.File]::Create((Join-Path $runtime 'TestRunner.dll'))
try{$result=$compilation.Emit($stream)}finally{$stream.Dispose()}
foreach($diagnostic in $result.Diagnostics){if($diagnostic.Severity -ne 'Hidden'){Write-Output $diagnostic.ToString()}}
if(-not$result.Success){throw 'Deep test compilation failed'}
$report=Join-Path $root ('research\deep\validation-'+$Side.ToLowerInvariant()+'.json')
if($Suite -ne 'All'){$report=Join-Path $root ('research\deep\validation-'+$Side.ToLowerInvariant()+'-'+$Suite.ToLowerInvariant()+'.json')}
if($ReportPath){$report=$ReportPath}
$stdout=Join-Path $runtime 'stdout.txt'
$stderr=Join-Path $runtime 'stderr.txt'
$process=Start-Process -FilePath (Join-Path $runtime 'TestRunner.exe') -ArgumentList @(('"'+$GameDir+'"'),('"'+$report+'"'),$Suite) -WorkingDirectory $runtime -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
Get-Content -LiteralPath $stdout
Get-Content -LiteralPath $stderr
if($process.ExitCode -ne 0){throw "Native .NET 8 test failed: $($process.ExitCode)"}
