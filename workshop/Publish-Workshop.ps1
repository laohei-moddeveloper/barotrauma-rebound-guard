#requires -Version 7.0
param([ValidateSet('Publish','Verify')][string]$Action='Verify',[string]$GameDir='E:\steam\steamapps\common\Barotrauma',[string]$PackageDir,[switch]$ResearchSnapshot)
$ErrorActionPreference='Stop'
$env:SteamAppId='602960'
$folder=if($PackageDir){[IO.Path]::GetFullPath($PackageDir)}else{Join-Path $PSScriptRoot 'publish/ReboundGuard'}
$statePath=Join-Path $PSScriptRoot 'publish-state.json'
$title=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'title.txt')).Trim()
$description=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'description.bbcode.txt')).Trim()
$preview=Join-Path $folder 'preview.jpg'
$contentId='reboundguard-experimental-040'
if($Action -eq 'Publish') {
    $manifest=Get-Content -Raw -LiteralPath (Join-Path $folder 'PUBLIC-PACKAGE.json') | ConvertFrom-Json
    if($manifest.format -ne 1 -or $manifest.project -ne $contentId -or $manifest.file_id -ne '3822098302' -or $manifest.app_id -ne 602960){throw 'Unexpected public package target'}
    if($manifest.validation_status -ne 'research_snapshot_native_validation_pending' -or -not $ResearchSnapshot){throw 'Use the prepared experimental snapshot entry point; validation status must be explicit'}
    $actual=@(Get-ChildItem -LiteralPath $folder -Recurse -File | ForEach-Object {[IO.Path]::GetRelativePath($folder,$_.FullName).Replace('\','/')} | Where-Object {$_ -ne 'PUBLIC-PACKAGE.json'} | Sort-Object)
    $expected=@($manifest.files | ForEach-Object {$_.path} | Sort-Object -Unique)
    if(($actual -join "`n") -cne ($expected -join "`n") -or $expected.Count -ne $manifest.files.Count){throw 'Public package file list differs from the prepared manifest'}
    foreach($entry in $manifest.files){
        if($entry.path -notmatch '^(CSharp/(Shared|Client|Server)/[^/]+\.cs|filelist\.xml|preview\.jpg|README\.zh-CN\.md|Collect-Reports\.ps1|Optional/(Install-KnownModFixes\.ps1|README\.zh-CN\.md|compat/(Mudraptor-IsHuman|TSM-accessoriesbuff)\.lua))$'){throw 'File outside the Workshop publication allowlist'}
        $file=Join-Path $folder $entry.path
        $check=Get-Item -LiteralPath $file
        while($check.FullName -ne $folder){
            if($check.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked Workshop content rejected'}
            $check=Get-Item -LiteralPath (Split-Path $check.FullName -Parent)
        }
        if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256){throw ('Prepared package bytes changed: '+$entry.path)}
    }
    if((Get-Item -LiteralPath $preview).Length -ge 1048576){throw 'Preview is too large'}
    $xml=[xml][IO.File]::ReadAllText((Join-Path $folder 'filelist.xml'))
    if($xml.contentpackage.steamworkshopid -ne '3822098302'){throw 'Package targets another item'}
}
$sdk=[Reflection.Assembly]::LoadFrom((Join-Path $GameDir 'Facepunch.Steamworks.Win64.dll'))
try {
    [Steamworks.SteamClient]::Init(602960,$true)
    if(-not[Steamworks.SteamClient]::IsLoggedOn -or [uint][Steamworks.SteamClient]::AppId -ne 602960){throw 'Expected logged-on Barotrauma Steam session'}
    $state=Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json -AsHashtable
    if($state.project -ne $contentId -or $state.app_id -ne 602960 -or $state.file_id -ne '3822098302'){throw 'Unexpected publication state'}
    $id=[Steamworks.Data.PublishedFileId]::op_Implicit([ulong]$state.file_id)
    $item=[Steamworks.Ugc.Item]::GetAsync($id,0).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
    if(-not$item -or $item.Result -ne [Steamworks.Result]::OK){throw 'Unable to verify workshop item'}
    if($item.Owner.Id -ne [Steamworks.SteamClient]::SteamId -or [uint]$item.ConsumerApp -ne 602960){throw 'Target is not owned by the current Barotrauma account'}
    if($Action -eq 'Publish') {
        $editor=[Steamworks.Ugc.Editor]::new($id)
        $editor=$editor.InLanguage('schinese').WithTitle($title).WithDescription($description).WithContent($folder).WithPreviewFile($preview)
        $editor=$editor.WithTag('Client-side').WithTag('Server-side').WithVisibility([Steamworks.Ugc.Visibility]::Public)
        $editor=$editor.WithMetaData($contentId).WithChangeLog([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'changenote.txt')).Trim())
        $result=$editor.SubmitAsync($null,$null).WaitAsync([TimeSpan]::FromSeconds(240)).GetAwaiter().GetResult()
        $state['title']=$title
        $state['build']=$manifest.build
        $state['validation_status']=$manifest.validation_status
        $state['last_submit_result']=[string]$result.Result
        $state['needs_workshop_agreement']=[bool]$result.NeedsWorkshopAgreement
        $state['status']=if($result.Success){'uploaded_public'}else{'upload_failed'}
        $state['submitted_utc']=[DateTime]::UtcNow.ToString('o')
        $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding utf8
        if(-not$result.Success -or $result.NeedsWorkshopAgreement){throw ('Upload failed or requires Steam agreement: '+$result.Result)}
        Write-Output ('Upload result='+$result.Result+'; item='+$result.FileId+'; needs_agreement='+$result.NeedsWorkshopAgreement)
        $item=[Steamworks.Ugc.Item]::GetAsync($id,0).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
        $dependency=[Steamworks.Data.PublishedFileId]::op_Implicit([ulong]2559634234)
        $added=$item.AddDependency($dependency).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
        $state['luacs_dependency_added']=[bool]$added
        $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding utf8
        Write-Output ('LuaCs prerequisite linked='+$added)
        if(-not $added){throw 'Content submitted, but LuaCs dependency could not be confirmed. Do not report a complete publication.'}
    }
    $item=[Steamworks.Ugc.Item]::GetAsync($id,0).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
    if(-not $item -or $item.Result -ne [Steamworks.Result]::OK -or $item.Id.ToString() -ne '3822098302'){throw 'Publication readback failed'}
    $verification=[ordered]@{id=[string]$item.Id;title=$item.Title;visibility=[string]$item.Visibility;result=[string]$item.Result;banned=$item.IsBanned;bytes=$item.SizeOfFileInBytes;preview=$item.PreviewImageUrl;tags=$item.Tags;description_matches=($item.Description -eq $description);url=$item.Url;updated_utc=$item.Updated.ToUniversalTime().ToString('o')}
    $verification | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'verification.json') -Encoding utf8
    $verification | ConvertTo-Json -Depth 5
} finally {if([Steamworks.SteamClient]::IsValid){[Steamworks.SteamClient]::Shutdown()}}
