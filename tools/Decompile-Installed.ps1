param([string]$GameDir='E:\steam\steamapps\common\Barotrauma', [string]$Assembly='DedicatedServer.dll', [string[]]$Types=@('Barotrauma.GameMain','Barotrauma.Character','Barotrauma.MapEntity','Barotrauma.PhysicsBody'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$library=Join-Path $root 'build\decompiler\lib\netstandard2.0\ICSharpCode.Decompiler.dll'
Add-Type -LiteralPath $library
$settings=[ICSharpCode.Decompiler.DecompilerSettings]::new()
$settings.ThrowOnAssemblyResolveErrors=$false
$settings.ShowXmlDocumentation=$false
$decompiler=[ICSharpCode.Decompiler.CSharp.CSharpDecompiler]::new((Join-Path $GameDir $Assembly),$settings)
$destination=Join-Path $root ('research\deep\installed\'+[IO.Path]::GetFileNameWithoutExtension($Assembly))
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($type in $Types) {
    $text=$decompiler.DecompileTypeAsString([ICSharpCode.Decompiler.TypeSystem.FullTypeName]::new($type))
    [IO.File]::WriteAllText((Join-Path $destination ($type+'.cs')),$text)
    Write-Output "Decompiled $Assembly :: $type ($($text.Length) characters)"
}
