param([string]$ManifestPath='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$xml=[xml][IO.File]::ReadAllText((Join-Path $root 'LocalMod\ReboundGuard\filelist.xml'))
$version=$xml.contentpackage.modversion
if(-not $ManifestPath){$ManifestPath=Join-Path $root ('docs\validation\'+$version+'\manifest.json')}
$manifest=Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
if($manifest.mod_version -ne $version){throw 'Evidence manifest version mismatch'}
$release=Join-Path $root 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$staging=Join-Path $root ('build\release-stage-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($entry in $manifest.files) {
    $relative=[string]$entry.path
    if([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])(\.|\.\.|build|bin|obj|__pycache__|migration-private|handoff|\.git|\.env[^\\/]*)([\\/]|$)' -or
        $relative -match '\.(dll|exe|pdb|log|save|sub|nettrace|etlx|pem|key|pfx)$'){throw "Private/binary or invalid release entry: $relative"}
    if(-not $expected.Add($relative.Replace('\','/'))){throw "Duplicate release entry: $relative"}
    $source=Join-Path $root $relative
    if((Get-Item -LiteralPath $source).LinkType){throw "Symlink release entry: $relative"}
    if((Get-FileHash -LiteralPath $source).Hash -ne $entry.sha256){throw "Evidence manifest mismatch: $relative"}
    $target=Join-Path $staging $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    if((Get-FileHash -LiteralPath $target).Hash -ne $entry.sha256){throw "Staged copy mismatch: $relative"}
}
$manifestRelative='docs/validation/'+$version+'/manifest.json'
New-Item -ItemType Directory -Path (Join-Path $staging ('docs/validation/'+$version)) -Force | Out-Null
Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $staging $manifestRelative)
[void]$expected.Add($manifestRelative)
$destination=Join-Path $release ('ReboundGuard_Experimental_'+$version+'.zip')
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $destination -Force
$archive=[IO.Compression.ZipFile]::OpenRead($destination)
try {
    $actual=@($archive.Entries | Where-Object {-not $_.FullName.EndsWith('/')} | ForEach-Object {$_.FullName.Replace('\','/')})
    if($actual.Count -ne $expected.Count -or @($actual | Where-Object {-not $expected.Contains($_)}).Count){throw 'Archive differs from the explicit public inventory'}
} finally {$archive.Dispose()}
$hash=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $release ('SHA256-'+$version+'.txt')), "$hash  $([IO.Path]::GetFileName($destination))`r`n")
Write-Output "PASS: $($expected.Count) allowlisted public source/evidence files; no game binaries, saves or private reports."
Write-Output $destination
Write-Output "SHA256: $hash"
