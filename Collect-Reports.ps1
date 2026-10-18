param([string]$GameDir='E:\steam\steamapps\common\Barotrauma',[int]$Hours=3)
$ErrorActionPreference='Stop'
$game=(Resolve-Path -LiteralPath $GameDir).Path
$since=[DateTime]::UtcNow.AddHours(-$Hours)
$out=Join-Path $PSScriptRoot ('CollectedReports_'+[DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $out | Out-Null
$manifest=[Collections.Generic.List[object]]::new()
foreach($file in @(Get-ChildItem -LiteralPath $game -File | Where-Object {$_.Name -match '^crashreport.*\.log$'}) +
    @(Get-ChildItem -LiteralPath (Join-Path $game 'ReboundGuardReports') -File -ErrorAction SilentlyContinue)) {
    $manifest.Add(@{name=$file.Name;size=$file.Length;last_write_utc=$file.LastWriteTimeUtc.ToString('o');included=($file.LastWriteTimeUtc -ge $since)})
    if($file.LastWriteTimeUtc -ge $since){Copy-Item -LiteralPath $file.FullName -Destination $out}
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'file-times.json') -Encoding utf8
try {
    $events=Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=$since.ToLocalTime();Level=@(1,2)} -ErrorAction Stop |
        Where-Object {$_.Message -match 'Barotrauma|DedicatedServer'} | Select-Object TimeCreated,ProviderName,Id,Message
    $events | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'windows-application-errors.json') -Encoding utf8
} catch { [IO.File]::WriteAllText((Join-Path $out 'windows-event-note.txt'),$_.Exception.Message) }
$archive=$out+'.zip'
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive
Write-Output ('Read-only collection complete: '+$archive)
Write-Output 'Older crash reports are listed but not copied as evidence for a newer incident. No registry, dump policy, game binary or game configuration was changed.'
