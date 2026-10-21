[CmdletBinding()]
param([switch]$ResearchSnapshot,
    [ValidateSet('research_snapshot_native_validation_pending','isolated_methods_verified_gameplay_pending')]
    [string]$ValidationStatus='research_snapshot_native_validation_pending')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
# Experimental builds retain their explicit validation scope; never substitute
# historical 0.4.0 evidence for the current snapshot.
if(-not $ResearchSnapshot) {
    throw 'This experimental branch requires -ResearchSnapshot and an explicit validation scope. Isolated checks do not establish gameplay gains.'
}
$labelMatch=[regex]::Match([IO.File]::ReadAllText((Join-Path $root 'LocalMod/ReboundGuard/CSharp/Shared/Plugin.cs')), 'BuildLabel = "([a-zA-Z0-9_-]+)"')
if(-not $labelMatch.Success) { throw 'Missing safe build label' }
$label=$labelMatch.Groups[1].Value
$release=Join-Path $root 'release'
$stage=Join-Path $root ('build/public-release-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $stage 'source'
$package=Join-Path $source 'workshop/publish/ReboundGuard'
foreach($dir in @($release,$source,$package)){New-Item -ItemType Directory -Path $dir -Force | Out-Null}
function Copy-PublicFile([string]$relative,[string]$destination) {
    $inputFile=Join-Path $root $relative
    if(-not(Test-Path -LiteralPath $inputFile -PathType Leaf)){throw "Missing public input: $relative"}
    # No symlinks/reparse points, including ancestors inside the checkout.
    $check=Get-Item -LiteralPath $inputFile
    while($check.FullName -ne $root){
        if($check.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Linked public input rejected: $relative"}
        $check=Get-Item -LiteralPath (Split-Path $check.FullName -Parent)
        if(-not $check){throw 'Public input escaped the checkout'}
    }
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $inputFile -Destination $destination
}
# Explicit public extensions and locations, never recursive directory copies.
foreach($side in @('Shared','Client','Server')) {
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $root "LocalMod/ReboundGuard/CSharp/$side") -File -Filter '*.cs') {
        $relative="LocalMod/ReboundGuard/CSharp/$side/"+$file.Name
        Copy-PublicFile $relative (Join-Path $source $relative)
        Copy-PublicFile $relative (Join-Path $package "CSharp/$side/$($file.Name)")
    }
}
$publicRoot=@('Install.ps1','Install-All.ps1','Install-KnownModFixes.ps1','Collect-Reports.ps1','README.md','README.zh-CN.md','README-0.4.zh-CN.md','研究测试摘要.md','LocalMod/ReboundGuard/filelist.xml')
$publicDocs=@('docs/HANDOFF.md','docs/PERFORMANCE-ROADMAP.zh-CN.md','docs/PERFORMANCE-MAINTENANCE.zh-CN.md','docs/RELEASE-20261019.zh-CN.md','docs/validation/2026-10-19-windows-pr1.md',
    'docs/ENGINE-HOTPATHS-20261021.zh-CN.md','docs/RELEASE-20261021.zh-CN.md','docs/validation/2026-10-21-engine-hotpaths.json')
$publicTools=@('tools/Verify.ps1','tools/Verify-Deep.ps1','tools/Verify-Offline.ps1','tools/Verify-Managed.py','tools/Fetch-AuditedDependencies.py','tools/Build-Release.ps1','tools/Analyze-FieldReports.py')
$publicWorkshop=@('workshop/Publish-Workshop.ps1','workshop/Update-Localizations.ps1','workshop/Publish-Prepared.ps1','workshop/title.txt','workshop/title.en.txt','workshop/description.bbcode.txt','workshop/description.en.bbcode.txt','workshop/changenote.txt','workshop/publish-state.json','workshop/Workshop-README.md','workshop/Optional-README.md','workshop/assets/preview-multiplayer.jpg')
foreach($relative in ($publicRoot+$publicDocs+$publicTools+$publicWorkshop)){Copy-PublicFile $relative (Join-Path $source $relative)}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $root 'tests') -File -Filter '*.cs') {Copy-PublicFile ('tests/'+$file.Name) (Join-Path $source ('tests/'+$file.Name))}
foreach($relative in @('tests/Portable/Program.cs','tests/Portable/ReboundGuard.Portable.csproj','compat/Mudraptor-IsHuman.lua','compat/TSM-accessoriesbuff.lua')) {Copy-PublicFile $relative (Join-Path $source $relative)}
Copy-PublicFile 'LocalMod/ReboundGuard/filelist.xml' (Join-Path $package 'filelist.xml')
$xml=[xml][IO.File]::ReadAllText((Join-Path $package 'filelist.xml'))
$xml.contentpackage.SetAttribute('steamworkshopid','3822098302')
$xml.Save((Join-Path $package 'filelist.xml'))
foreach($pair in @(
    @('Collect-Reports.ps1','Collect-Reports.ps1'),
    @('workshop/Workshop-README.md','README.zh-CN.md'),
    @('workshop/assets/preview-multiplayer.jpg','preview.jpg'),
    @('Install-KnownModFixes.ps1','Optional/Install-KnownModFixes.ps1'),
    @('workshop/Optional-README.md','Optional/README.zh-CN.md'),
    @('compat/Mudraptor-IsHuman.lua','Optional/compat/Mudraptor-IsHuman.lua'),
    @('compat/TSM-accessoriesbuff.lua','Optional/compat/TSM-accessoriesbuff.lua')
)){Copy-PublicFile $pair[0] (Join-Path $package $pair[1])}
$manifest=[ordered]@{
    format=1;project='reboundguard-experimental-040';app_id=602960;file_id='3822098302';mod_version='0.4.0';build=$label
    validation_status=$ValidationStatus
    note='Experimental source snapshot. Validation status describes isolated methods only; no submarine, real multiplayer, rendering or end-to-end loading A/B. No whole-game FPS or rubberbanding guarantee.'
    files=@()
}
foreach($file in Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName){
    $relative=[IO.Path]::GetRelativePath($package,$file.FullName).Replace('\','/')
    if($relative -notmatch '^(CSharp/(Shared|Client|Server)/[^/]+\.cs|filelist\.xml|preview\.jpg|README\.zh-CN\.md|Collect-Reports\.ps1|Optional/(Install-KnownModFixes\.ps1|README\.zh-CN\.md|compat/(Mudraptor-IsHuman|TSM-accessoriesbuff)\.lua))$'){throw "Unexpected workshop file: $relative"}
    $manifest.files+=@{path=$relative;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'PUBLIC-PACKAGE.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $package 'PUBLIC-PACKAGE.json') -Destination (Join-Path $source 'PUBLIC-PACKAGE.json')
$sourceZip=Join-Path $release "ReboundGuard_0.4.0_${label}_experimental.zip"
$workshopZip=Join-Path $release "ReboundGuard_0.4.0_${label}_workshop.zip"
Compress-Archive -Path (Join-Path $source '*') -DestinationPath $sourceZip -Force
Compress-Archive -LiteralPath $package -DestinationPath $workshopZip -Force
$hashLines=@()
foreach($zip in @($sourceZip,$workshopZip)){
    $hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    $hashLines+="$hash  $([IO.Path]::GetFileName($zip))"
}
$hashLines | Set-Content -LiteralPath (Join-Path $release "SHA256-${label}.txt") -Encoding utf8
Write-Output "Public experimental source package: $sourceZip"
Write-Output "Workshop package: $workshopZip"
Write-Output "Prepared Workshop directory: $package"
Write-Output ('Validation status: '+$ValidationStatus+'; no additional tests were run by this packaging script.')
