param([Parameter(Mandatory=$true)][string[]]$PackageDir, [switch]$Restore)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$rules=@(
    @{Relative='Lua\Scripts\shared.lua';Hash='631DCB17A38167AFEFDD6A0D64678F1D90314DB8AB6554CCF7CFA4E612C973B0';Kind='Mudraptor';NamePattern='(?i)mudraptor|TF Mix'},
    @{Relative='Lua\Autorun\accessoriesbuff.lua';Hash='DDA5991659E6B1FE0398E612FC3D92317427E004AE79ED6B3CCAF24847AF77BE';Kind='TSM';NamePattern='(?i)T[.]?S[.]?M'}
)
$planned=[Collections.Generic.List[object]]::new()
foreach($package in $PackageDir) {
    $resolved=(Resolve-Path -LiteralPath $package).Path
    if(-not(Test-Path -LiteralPath (Join-Path $resolved 'filelist.xml'))){throw ('Not a content package: '+$resolved)}
    $packageName=([xml][IO.File]::ReadAllText((Join-Path $resolved 'filelist.xml'))).contentpackage.name
    foreach($rule in $rules) {
        if($packageName -notmatch $rule.NamePattern){continue}
        $path=Join-Path $resolved $rule.Relative
        if(-not(Test-Path -LiteralPath $path)){continue}
        $backup=$path+'.ReboundGuard040.original'
        $installedHashPath=$path+'.ReboundGuard040.installed.sha256'
        $marker='ReboundGuard native bridge v0.4.0'
        if($Restore) {
            if(-not(Test-Path -LiteralPath $backup)){continue}
            if((Get-FileHash -LiteralPath $backup).Hash -ne $rule.Hash){throw ('Backup hash mismatch: '+$backup)}
            if(-not(Test-Path -LiteralPath $installedHashPath) -or (Get-FileHash -LiteralPath $path).Hash -ne [IO.File]::ReadAllText($installedHashPath).Trim()) {throw ('Current file changed after bridge install: '+$path)}
            $planned.Add(@{Path=$path;Backup=$backup;Restore=$true;Previous=[IO.File]::ReadAllBytes($path)}); continue
        }
        $text=[IO.File]::ReadAllText($path)
        if($text.Contains($marker)){Write-Output ('Already installed: '+$path);continue}
        if((Get-FileHash -LiteralPath $path).Hash -ne $rule.Hash){throw ('Unsupported or locally modified Lua source; no replacement made: '+$path)}
        if((Test-Path -LiteralPath $backup) -and (Get-FileHash -LiteralPath $backup).Hash -ne $rule.Hash){throw ('Backup already exists with different content: '+$backup)}
        if($rule.Kind -eq 'Mudraptor') {
            $pattern='(?s)Hook\.Patch\(\s*"Barotrauma\.Character",\s*"get_IsHuman",\s*\{\s*\},\s*function\(instance, ptable\)\s*return ptable\.OriginalReturnValue or instance\.Params\.UseHumanAI\s*end,\s*Hook\.HookMethodType\.After\s*\)'
            $matches=[regex]::Matches($text,$pattern)
            if($matches.Count -ne 1){throw 'Expected exactly one audited Mudraptor getter hook'}
            $replacement=[IO.File]::ReadAllText((Join-Path $root 'compat\Mudraptor-IsHuman.lua'))
            $match=$matches[0]; $text=$text.Substring(0,$match.Index)+$replacement+$text.Substring($match.Index+$match.Length)
        } else { $text=[IO.File]::ReadAllText((Join-Path $root 'compat\TSM-accessoriesbuff.lua')) }
        $planned.Add(@{Path=$path;Backup=$backup;Restore=$false;Text=$text;Expected=$rule.Hash;InstalledHash=$installedHashPath})
    }
}
if($planned.Count -eq 0){Write-Output 'No matching changes required.';return}
# Preflight every source before any mutation, and retain byte-exact originals.
$completed=[Collections.Generic.List[object]]::new()
try {
    foreach($change in $planned) {
        if($change.Restore) { $completed.Add($change); Copy-Item -LiteralPath $change.Backup -Destination $change.Path -Force }
        else {
            if((Get-FileHash -LiteralPath $change.Path).Hash -ne $change.Expected){throw 'Source changed during preflight'}
            if(-not(Test-Path -LiteralPath $change.Backup)){Copy-Item -LiteralPath $change.Path -Destination $change.Backup}
            $completed.Add($change)
            [IO.File]::WriteAllText($change.Path,$change.Text,[Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText($change.InstalledHash,(Get-FileHash -LiteralPath $change.Path).Hash)
        }
        Write-Output ('Applied: '+$change.Path)
    }
} catch {
    foreach($change in $completed){if($change.Restore){[IO.File]::WriteAllBytes($change.Path,$change.Previous)}else{Copy-Item -LiteralPath $change.Backup -Destination $change.Path -Force}}
    throw
}
Write-Output 'Known Lua hooks updated. Enable Rebound Guard 0.4.0 and fully restart every game/server process. Original files are retained beside the modified sources.'
