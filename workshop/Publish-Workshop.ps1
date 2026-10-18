param([ValidateSet('Create','Publish','Verify')][string]$Action='Verify',[string]$GameDir='E:\steam\steamapps\common\Barotrauma')
$ErrorActionPreference='Stop'
$env:SteamAppId='602960'
$folder=Join-Path $PSScriptRoot 'publish\ReboundGuard'
$statePath=Join-Path $PSScriptRoot 'publish-state.json'
$title=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'title.txt')).Trim()
$description=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'description.bbcode.txt')).Trim()
$preview=Join-Path $folder 'preview.jpg'
$contentId='reboundguard-experimental-040'
if(-not(Test-Path -LiteralPath (Join-Path $folder 'filelist.xml'))){throw 'Public package missing'}
if((Get-Item -LiteralPath $preview).Length -ge 1048576){throw 'Preview is too large'}
if(Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object {$_.Extension -match '^\.(dll|exe|log|save|sub|nettrace|etlx)$'}){throw 'Unexpected binary/private data in public package'}
$sdk=[Reflection.Assembly]::LoadFrom((Join-Path $GameDir 'Facepunch.Steamworks.Win64.dll'))
try {
    [Steamworks.SteamClient]::Init(602960,$true)
    if(-not[Steamworks.SteamClient]::IsLoggedOn -or [uint][Steamworks.SteamClient]::AppId -ne 602960){throw 'Expected logged-on Barotrauma Steam session'}
    if($Action -eq 'Create') {
        if(Test-Path -LiteralPath $statePath){throw 'A publication state already exists; inspect it instead of creating duplicates'}
        $query=[Steamworks.Ugc.Query]::Items
        $query=$query.WhereUserPublished([Steamworks.SteamClient]::SteamId)
        $page=$query.GetPageAsync(1).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
        if(-not$page){throw 'Could not check existing owned publications'}
        try {
            if($page.TotalCount -gt $page.ResultCount){throw 'Existing items span multiple pages; inspect all pages before creating'}
            if(@($page.Entries | Where-Object {$_.Title -match 'Rebound Guard|深潜稳帧'}).Count){throw 'Potential existing Rebound Guard publication; inspect before creating'}
        } finally {$page.Dispose()}
        $editor=[Steamworks.Ugc.Editor]::NewCommunityFile
        $editor=$editor.WithTitle($title).WithDescription($description).WithVisibility([Steamworks.Ugc.Visibility]::Private)
        $result=$editor.SubmitAsync($null,$null).WaitAsync([TimeSpan]::FromSeconds(120)).GetAwaiter().GetResult()
        if(-not$result.Success){throw ('Create failed: '+$result.Result)}
        $state=[ordered]@{project=$contentId;file_id=[string]$result.FileId;title=$title;app_id=602960;status='created_private';needs_workshop_agreement=[bool]$result.NeedsWorkshopAgreement;created_utc=[DateTime]::UtcNow.ToString('o')}
        $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding utf8
        Write-Output ('Created private draft '+$result.FileId+'; needs_agreement='+$result.NeedsWorkshopAgreement)
        return
    }
    $state=Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json -AsHashtable
    if($state.project -ne $contentId -or $state.app_id -ne 602960){throw 'Unexpected publication state'}
    $id=[Steamworks.Data.PublishedFileId]::op_Implicit([ulong]$state.file_id)
    $item=[Steamworks.Ugc.Item]::GetAsync($id,0).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
    if(-not$item -or $item.Result -ne [Steamworks.Result]::OK){throw 'Unable to verify workshop item'}
    if($item.Owner.Id -ne [Steamworks.SteamClient]::SteamId -or [uint]$item.ConsumerApp -ne 602960){throw 'Target is not owned by the current Barotrauma account'}
    if($Action -eq 'Publish') {
        $xml=[xml][IO.File]::ReadAllText((Join-Path $folder 'filelist.xml'))
        $xml.contentpackage.SetAttribute('steamworkshopid',$state.file_id)
        $xml.Save((Join-Path $folder 'filelist.xml'))
        $editor=[Steamworks.Ugc.Editor]::new($id)
        $editor=$editor.InLanguage('schinese').WithTitle($title).WithDescription($description).WithContent($folder).WithPreviewFile($preview)
        $editor=$editor.WithTag('Client-side').WithTag('Server-side').WithVisibility([Steamworks.Ugc.Visibility]::Public)
        $editor=$editor.WithMetaData($contentId).WithChangeLog([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'changenote.txt')).Trim())
        $result=$editor.SubmitAsync($null,$null).WaitAsync([TimeSpan]::FromSeconds(240)).GetAwaiter().GetResult()
        $state['last_submit_result']=[string]$result.Result
        $state['needs_workshop_agreement']=[bool]$result.NeedsWorkshopAgreement
        $state['status']=if($result.Success){'uploaded_public'}else{'upload_failed'}
        $state['submitted_utc']=[DateTime]::UtcNow.ToString('o')
        $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding utf8
        if(-not$result.Success){throw ('Upload failed: '+$result.Result)}
        Write-Output ('Upload result='+$result.Result+'; item='+$result.FileId+'; needs_agreement='+$result.NeedsWorkshopAgreement)
        $item=[Steamworks.Ugc.Item]::GetAsync($id,0).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
        $dependency=[Steamworks.Data.PublishedFileId]::op_Implicit([ulong]2559634234)
        $added=$item.AddDependency($dependency).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
        $state['luacs_dependency_added']=[bool]$added
        $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding utf8
        Write-Output ('LuaCs prerequisite linked='+$added)
    }
    $item=[Steamworks.Ugc.Item]::GetAsync($id,0).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
    $verification=[ordered]@{id=[string]$item.Id;title=$item.Title;visibility=[string]$item.Visibility;result=[string]$item.Result;banned=$item.IsBanned;bytes=$item.SizeOfFileInBytes;preview=$item.PreviewImageUrl;tags=$item.Tags;description_matches=($item.Description -eq $description);url=$item.Url;updated_utc=$item.Updated.ToUniversalTime().ToString('o')}
    $verification | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'verification.json') -Encoding utf8
    $verification | ConvertTo-Json -Depth 5
} finally {if([Steamworks.SteamClient]::IsValid){[Steamworks.SteamClient]::Shutdown()}}
