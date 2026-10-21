param([string]$GameDir = 'E:\steam\steamapps\common\Barotrauma', [ValidateSet('Client','Server')][string]$Side = 'Client', [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
public static class GameDependencies {
    public static void Register(string folder) {
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            string path = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }
}
'@
[GameDependencies]::Register($GameDir)
foreach ($dll in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll','Basic.Reference.Assemblies.Net80.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $GameDir $dll)) | Out-Null
}
$gameAssembly = if ($Side -eq 'Client') { 'Barotrauma.dll' } else { 'DedicatedServer.dll' }
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($ref in [Basic.Reference.Assemblies.Net80+References]::All) { $refs.Add($ref) }
$referenceFiles=@("Publicized\$gameAssembly",'Publicized\BarotraumaCore.dll','0Harmony.dll','Farseer.NetStandard.dll','XNATypes.dll','MoonSharp.Interpreter.dll','FluentResults.dll','Microsoft.CodeAnalysis.dll')
if($Side -eq 'Client') { $referenceFiles+= 'MonoGame.Framework.Windows.NetStandard.dll' }
foreach ($dll in $referenceFiles) {
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile((Join-Path $GameDir $dll)))
}
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'LocalMod\ReboundGuard\CSharp\Shared') -Filter '*.cs')
if ($Side -eq 'Client') { $sources += @(Get-ChildItem -LiteralPath (Join-Path $root 'LocalMod\ReboundGuard\CSharp\Client') -Filter '*.cs') }
if ($Side -eq 'Server') { $sources += @(Get-ChildItem -LiteralPath (Join-Path $root 'LocalMod\ReboundGuard\CSharp\Server') -Filter '*.cs') }
foreach ($source in $sources) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source.FullName)))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$options = $options.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create("ReboundGuard.$Side", $trees, $refs, $options)
$output = Join-Path $build "ReboundGuard.$Side.dll"
$stream = [IO.File]::Create($output)
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
foreach ($diagnostic in $result.Diagnostics) { Write-Output $diagnostic.ToString() }
if (-not $result.Success) { throw "Compilation failed: $Side" }
Write-Output "PASS: $Side compiled against installed LuaCs publicized references and .NET 8 reference assemblies."
if ($CompileOnly) { return }

if ($Side -eq 'Client') {
    $testSource = [IO.File]::ReadAllText((Join-Path $root 'tests\Regression.cs'))
    $testTrees = [Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($testSource))
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($output))
    $testCompilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('ReboundGuard.Regression', $testTrees, $refs, $options)
    $testStream = [IO.MemoryStream]::new()
    $testResult = $testCompilation.Emit($testStream)
    foreach ($diagnostic in $testResult.Diagnostics) { Write-Output $diagnostic.ToString() }
    if (-not $testResult.Success) { throw 'Test compilation failed' }
    [Reflection.Assembly]::LoadFrom($output) | Out-Null
    $testAssembly = [Reflection.Assembly]::Load($testStream.ToArray())
    $testStream.Dispose()
    try { $testAssembly.GetType('ReboundGuardTests.Regression').GetMethod('Run').Invoke($null, @()) }
    catch { throw $_.Exception.GetBaseException() }
}
else {
    [Reflection.Assembly]::LoadFrom((Join-Path $GameDir '0Harmony.dll')) | Out-Null
    $runtimeAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $GameDir $gameAssembly))
    $pluginAssembly = [Reflection.Assembly]::LoadFrom($output)
    $bindingFlags = [Reflection.BindingFlags]'Static,NonPublic'
    $pluginType = $pluginAssembly.GetType('ReboundGuard.Plugin')
    $monitor = $runtimeAssembly.GetType('Barotrauma.GameScreen').GetMethod('Update')
    $harmony = [HarmonyLib.Harmony]::new('reboundguard.offline.server.regression')
    try {
        $prefix = [HarmonyLib.HarmonyMethod]::new($pluginType.GetMethod('BeforeStep',$bindingFlags))
        $postfix = [HarmonyLib.HarmonyMethod]::new($pluginType.GetMethod('AfterStep',$bindingFlags))
        $harmony.Patch($monitor, $prefix, $postfix, $null, $null) | Out-Null
        if (-not ([HarmonyLib.Harmony]::GetPatchInfo($monitor).Owners.Contains($harmony.Id))) { throw 'Server monitor patch missing' }
    } finally { $harmony.UnpatchSelf() }
    Write-Output 'PASS: server monitor Harmony emission and cleanup against installed DedicatedServer.dll; no simulation run.'
}
