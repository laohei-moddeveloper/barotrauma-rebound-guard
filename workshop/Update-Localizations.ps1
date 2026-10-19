param([string]$GameDir='E:\steam\steamapps\common\Barotrauma',[switch]$VerifyOnly)
$ErrorActionPreference='Stop'
$env:SteamAppId='602960'
[Reflection.Assembly]::LoadFrom((Join-Path $GameDir 'Facepunch.Steamworks.Win64.dll')) | Out-Null
$state=Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'publish-state.json') | ConvertFrom-Json
if($state.project -ne 'reboundguard-experimental-040' -or $state.file_id -ne '3822098302'){throw 'Unexpected workshop target'}
try {
    [Steamworks.SteamClient]::Init(602960,$true)
    if(-not [Steamworks.SteamClient]::IsLoggedOn -or [uint][Steamworks.SteamClient]::AppId -ne 602960){throw 'Expected logged-on Barotrauma Steam account'}
    $id=[Steamworks.Data.PublishedFileId]::op_Implicit([ulong]$state.file_id)
    $item=[Steamworks.Ugc.Item]::GetAsync($id,0).WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
    if(-not$item -or $item.Result -ne [Steamworks.Result]::OK -or $item.Owner.Id -ne [Steamworks.SteamClient]::SteamId -or [uint]$item.ConsumerApp -ne 602960){throw 'Current Steam account does not own the item'}
    if(-not$VerifyOnly){foreach($locale in @(@{Language='schinese';Title='title.txt';Description='description.bbcode.txt'},@{Language='english';Title='title.en.txt';Description='description.en.bbcode.txt'})) {
        $title=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $locale.Title)).Trim()
        $description=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $locale.Description)).Trim()
        $editor=[Steamworks.Ugc.Editor]::new($id)
        $editor=$editor.InLanguage($locale.Language).WithTitle($title).WithDescription($description)
        $result=$editor.SubmitAsync($null,$null).WaitAsync([TimeSpan]::FromSeconds(120)).GetAwaiter().GetResult()
        if(-not$result.Success -or $result.NeedsWorkshopAgreement){throw ('Localization submit failed or needs agreement: '+$locale.Language+' '+$result.Result)}
        Write-Output ('Updated '+$locale.Language+': '+$title)
    }}
    $verified=@()
    foreach($language in @('schinese','english')) {
        # This bundled SDK stores Query.InLanguage but never applies it.
        # Use its native interface for a genuine localized readback.
        $flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
        $api=([Steamworks.SteamUGC].GetProperty('Internal',$flags)).GetValue($null)
        function Invoke-Ugc([string]$name,[object[]]$arguments){$api.GetType().GetMethod($name,$flags).Invoke($api,$arguments)}
        $handle=Invoke-Ugc 'CreateQueryUGCDetailsRequest' @([Steamworks.Data.PublishedFileId[]]@($id),[uint]1)
        if(-not(Invoke-Ugc 'SetLanguage' @($handle,$language))){throw 'SetLanguage refused'}
        if(-not(Invoke-Ugc 'SetReturnLongDescription' @($handle,$true))){throw 'Long description refused'}
        Invoke-Ugc 'SetAllowCachedResponse' @($handle,[uint]0) | Out-Null
        $call=Invoke-Ugc 'SendQueryUGCRequest' @($handle)
        $callType=$call.GetType();$deadline=[DateTime]::UtcNow.AddSeconds(45)
        while(-not$callType.GetProperty('IsCompleted',$flags).GetValue($call)){
            if([DateTime]::UtcNow -gt $deadline){throw 'Localized query timed out'}
            [Threading.Thread]::Sleep(20)
        }
        $callback=$callType.GetMethod('GetResult',$flags).Invoke($call,@())
        if(-not$callback){throw 'Localized query returned no callback'}
        $callbackType=$callback.GetType()
        $queryResult=$callbackType.GetField('Result',$flags).GetValue($callback)
        if($queryResult -ne [Steamworks.Result]::OK){throw ('Localized query failed: '+$queryResult)}
        $page=[Activator]::CreateInstance([Steamworks.Ugc.ResultPage])
        [Steamworks.Ugc.ResultPage].GetField('Handle',$flags).SetValue($page,$handle)
        $page.ResultCount=[int]$callbackType.GetField('NumResultsReturned',$flags).GetValue($callback)
        $page.TotalCount=[int]$callbackType.GetField('TotalMatchingResults',$flags).GetValue($callback)
        try {
            $entry=@($page.Entries)[0]
            $titleFile=if($language -eq 'english'){'title.en.txt'}else{'title.txt'}
            $descriptionFile=if($language -eq 'english'){'description.en.bbcode.txt'}else{'description.bbcode.txt'}
            $ok=$entry.Title -eq [IO.File]::ReadAllText((Join-Path $PSScriptRoot $titleFile)).Trim() -and $entry.Description -eq [IO.File]::ReadAllText((Join-Path $PSScriptRoot $descriptionFile)).Trim()
            if(-not$ok){throw ('Localized readback mismatch: '+$language)}
            $verified+=@{language=$language;title=$entry.Title;description_matches=$ok;visibility=[string]$entry.Visibility;bytes=$entry.SizeOfFileInBytes}
        } finally {$page.Dispose()}
    }
    $verified | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'localization-verification.json') -Encoding utf8
    $verified | ConvertTo-Json -Depth 5
} finally {if([Steamworks.SteamClient]::IsValid){[Steamworks.SteamClient]::Shutdown()}}
