# diagnose-trayicon.ps1 (v2) - wide net.
#
#     powershell -ExecutionPolicy Bypass -File .\diagnose-trayicon.ps1
#
# v1 watched seven hand-picked keys and found nothing, so this stops guessing: it walks
# entire subtrees of HKCU plus ExplorerPatcher's files, and diffs everything.
#
# Read-only, no admin. Each snapshot takes 20-60 seconds. It also takes a snapshot AFTER
# restarting Explorer, because Explorer keeps some tray state in memory and only flushes
# it on shutdown - which would make an immediate diff look empty even when it worked.

$ErrorActionPreference = 'Continue'

function Section($t) {
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor DarkGray
    Write-Host $t -ForegroundColor Cyan
    Write-Host ('=' * 70) -ForegroundColor DarkGray
}

$Roots = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\StartupNotify',
    'HKCU:\Control Panel',
    'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion',
    'HKCU:\Software\ExplorerPatcher',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'
)

# Churns constantly and would bury the signal. If the answer turns out to hide in one of
# these, drop it from the list and run again.
$Noise = @(
    'RecentDocs', 'ComDlg32', 'FeatureUsage', 'UserAssist', 'MuiCache', 'BagMRU',
    '\Bags\', 'TypedPaths', 'RunMRU', 'SearchPlatform', 'Notifications',
    'AppBadgeUpdated', 'VisualElements', 'StartPage', 'Streams\Desktop',
    'TaskbarWinXP', 'JumpListData', 'Shell Extensions\Cached'
)

$FileTargets = @(
    (Join-Path $env:APPDATA 'ExplorerPatcher'),
    (Join-Path $env:LOCALAPPDATA 'ExplorerPatcher')
)

function Is-Noise($path) {
    foreach ($n in $Noise) { if ($path -like ('*' + $n + '*')) { return $true } }
    return $false
}

function Format-Value($v) {
    if ($null -eq $v) { return '<null>' }
    if ($v -is [byte[]]) {
        $hex = ($v | ForEach-Object { $_.ToString('x2') }) -join ''
        if ($hex.Length -gt 200) {
            $md5 = [System.Security.Cryptography.MD5]::Create()
            $hash = ($md5.ComputeHash($v) | ForEach-Object { $_.ToString('x2') }) -join ''
            return ('BINARY len={0} md5={1} head={2}' -f $v.Count, $hash, $hex.Substring(0, 120))
        }
        return ('BINARY len={0} {1}' -f $v.Count, $hex)
    }
    if ($v -is [System.Array]) { return ($v -join '|') }
    return [string]$v
}

function Take-Snapshot {
    $snap = @{}
    foreach ($root in $Roots) {
        if (-not (Test-Path $root)) { continue }
        $keys = New-Object System.Collections.ArrayList
        [void]$keys.Add($root)
        try {
            Get-ChildItem -Path $root -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
                $p = $_.PSPath -replace '^Microsoft\.PowerShell\.Core\\Registry::', ''
                if (-not (Is-Noise $p)) { [void]$keys.Add($_.PSPath) }
            }
        } catch { }

        foreach ($k in $keys) {
            try { $item = Get-Item -LiteralPath $k -ErrorAction Stop } catch { continue }
            $clean = $item.Name
            foreach ($name in $item.GetValueNames()) {
                try {
                    $display = $name
                    if ($display -eq '') { $display = '(default)' }
                    $snap["$clean :: $display"] = Format-Value $item.GetValue($name)
                } catch { }
            }
        }
    }

    foreach ($dir in $FileTargets) {
        if (-not (Test-Path $dir)) { continue }
        try {
            Get-ChildItem -Path $dir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
                $snap["FILE :: $($_.FullName)"] = ('{0} bytes, {1:yyyy-MM-dd HH:mm:ss}' -f $_.Length, $_.LastWriteTime)
            }
        } catch { }
    }

    return $snap
}

function Compare-Snapshots($before, $after) {
    $changed = 0
    foreach ($k in $after.Keys) {
        if (-not $before.ContainsKey($k)) {
            Write-Host ("  ADDED   {0}" -f $k) -ForegroundColor Green
            Write-Host ("          = {0}" -f $after[$k])
            $changed++
        } elseif ($before[$k] -ne $after[$k]) {
            Write-Host ("  CHANGED {0}" -f $k) -ForegroundColor Yellow
            Write-Host ("          was {0}" -f $before[$k]) -ForegroundColor DarkGray
            Write-Host ("          now {0}" -f $after[$k])
            $changed++
        }
    }
    foreach ($k in $before.Keys) {
        if (-not $after.ContainsKey($k)) {
            Write-Host ("  REMOVED {0}" -f $k) -ForegroundColor Red
            Write-Host ("          was {0}" -f $before[$k]) -ForegroundColor DarkGray
            $changed++
        }
    }
    if ($changed -eq 0) { Write-Host '  nothing changed' -ForegroundColor DarkGray }
    Write-Host ("  -- {0} difference(s)" -f $changed) -ForegroundColor White
}

Section '0. Setup'
Write-Host 'Watching:'
foreach ($r in $Roots) {
    $mark = 'missing'
    if (Test-Path $r) { $mark = 'exists ' }
    Write-Host ("  [{0}] {1}" -f $mark, $r)
}
foreach ($d in $FileTargets) { if (Test-Path $d) { Write-Host ("  [exists ] {0}" -f $d) } }

$open = Read-Host 'Open the system icons page for you? (y/n)'
if ($open -eq 'y') {
    Start-Process explorer.exe 'shell:::{05d7b0f4-2121-4eff-bf6b-ed3f69b894d9}'
    Start-Sleep -Seconds 2
}

Section '1. Baseline - battery icon currently ON'
Read-Host 'Make sure the battery icon is ON (visible), then press Enter'
Write-Host 'Scanning...' -ForegroundColor DarkGray
$before = Take-Snapshot
Write-Host ("  {0} values captured" -f $before.Count) -ForegroundColor DarkGray

Section '2. Turn the battery icon OFF'
Read-Host 'Turn Alimentation / Power OFF, wait for the icon to vanish, then press Enter'
Write-Host 'Scanning...' -ForegroundColor DarkGray
$afterOff = Take-Snapshot
Write-Host ("  {0} values captured" -f $afterOff.Count) -ForegroundColor DarkGray
Write-Host ''
Write-Host 'ON -> OFF (immediately):' -ForegroundColor White
Compare-Snapshots $before $afterOff

Section '3. Same again, after an Explorer restart'
Write-Host 'Explorer caches some tray state and only writes it out on shutdown, so this'
Write-Host 'catches anything that had not been flushed yet.'
$doRestart = Read-Host 'Restart Explorer now? (y/n)'
if ($doRestart -eq 'y') {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 4
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe
        Start-Sleep -Seconds 3
    }
    Write-Host 'Scanning...' -ForegroundColor DarkGray
    $afterRestart = Take-Snapshot
    Write-Host ''
    Write-Host 'ON -> OFF (after Explorer restart):' -ForegroundColor White
    Compare-Snapshots $before $afterRestart
}

Section '4. Turn it back ON'
Read-Host 'Turn the battery icon back ON, then press Enter'
Write-Host 'Scanning...' -ForegroundColor DarkGray
$afterOn = Take-Snapshot
Write-Host ''
Write-Host 'OFF -> ON:' -ForegroundColor White
Compare-Snapshots $afterOff $afterOn

Write-Host ''
Write-Host 'Done. Copy everything above.' -ForegroundColor Cyan
Write-Host 'If every section still says "nothing changed", say so - it means the state is'
Write-Host 'not in HKCU at all and I will look somewhere else entirely.' -ForegroundColor DarkGray
