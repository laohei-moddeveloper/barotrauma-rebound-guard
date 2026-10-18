param([int]$TargetProcessId, [int]$Seconds=15, [string]$GameDir='E:\steam\steamapps\common\Barotrauma',[string]$ExistingTrace)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Verify.ps1') -GameDir $GameDir -Side Client -CompileOnly
$runtime=Join-Path $build 'net8-harness-client'
$libraries=@((Join-Path $build 'diagnostics-libs\microsoft.diagnostics.netcore.client\lib\net6.0\Microsoft.Diagnostics.NETCore.Client.dll'))
$libraries+=@(Get-ChildItem -LiteralPath (Join-Path $build 'diagnostics-libs\microsoft.diagnostics.tracing.traceevent\lib\netstandard2.0') -Filter '*.dll' | ForEach-Object FullName)
foreach($library in $libraries) {
    Copy-Item -LiteralPath $library -Destination $runtime -Force
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($library))
}
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Trace-Allocations.cs')))
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$options=$options.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('TraceAgent',[Microsoft.CodeAnalysis.SyntaxTree[]]@($tree),$refs,$options)
$stream=[IO.File]::Create((Join-Path $runtime 'TraceAgent.dll'))
try {$result=$compilation.Emit($stream)}finally{$stream.Dispose()}
foreach($diagnostic in $result.Diagnostics){if($diagnostic.Severity -ne 'Hidden'){Write-Output $diagnostic.ToString()}}
if(-not $result.Success){throw 'Trace collector compilation failed'}
$bytes=[IO.File]::ReadAllBytes((Join-Path $runtime 'TestRunner.exe'))
$old=[Text.Encoding]::ASCII.GetBytes('TestRunner.dll'+[char]0); $new=[Text.Encoding]::ASCII.GetBytes('TraceAgent.dll'+[char]0)
for($i=0;$i -le $bytes.Length-$old.Length;$i++) {
    $match=$true
    for($j=0;$j -lt $old.Length;$j++){if($bytes[$i+$j] -ne $old[$j]){$match=$false;break}}
    if($match){[Array]::Copy($new,0,$bytes,$i,$new.Length);break}
}
[IO.File]::WriteAllBytes((Join-Path $runtime 'TraceAgent.exe'),$bytes)
Copy-Item -LiteralPath (Join-Path $runtime 'TestRunner.runtimeconfig.json') -Destination (Join-Path $runtime 'TraceAgent.runtimeconfig.json') -Force
[IO.File]::WriteAllText((Join-Path $runtime 'TraceAgent.deps.json'),[IO.File]::ReadAllText((Join-Path $runtime 'TestRunner.deps.json')).Replace('TestRunner','TraceAgent'))
$trace=Join-Path $root ('research\field-test-round2\live-'+$TargetProcessId+'-'+[DateTime]::UtcNow.ToString('HHmmss')+'.nettrace')
if($ExistingTrace){$trace=(Resolve-Path -LiteralPath $ExistingTrace).Path;$TargetProcessId=-1}
$process=Start-Process -FilePath (Join-Path $runtime 'TraceAgent.exe') -ArgumentList @($TargetProcessId,$Seconds,('"'+$trace+'"')) -WorkingDirectory $runtime -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $runtime 'trace-stdout.txt') -RedirectStandardError (Join-Path $runtime 'trace-stderr.txt')
Get-Content -LiteralPath (Join-Path $runtime 'trace-stdout.txt')
Get-Content -LiteralPath (Join-Path $runtime 'trace-stderr.txt')
if($process.ExitCode -ne 0){throw ('Trace collector failed '+$process.ExitCode)}
