# diagnose-nightlight.ps1 - captures the real night light blob layout on THIS machine.
#
#     powershell -ExecutionPolicy Bypass -File .\diagnose-nightlight.ps1
#
# Reads only. Nothing is written, nothing is changed. It shows the blob with night light
# off, then with it on, and diffs them - which pins down exactly which bytes encode the
# state on your build instead of assuming the layout from someone else's machine.

$ErrorActionPreference = 'Continue'

function Section($t) {
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor DarkGray
    Write-Host $t -ForegroundColor Cyan
    Write-Host ('=' * 70) -ForegroundColor DarkGray
}

$Paths = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Cloud\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\Cache\DefaultAccount\$$windows.data.bluelightreduction.bluelightreductionstate\Current'
)

function Get-Blobs {
    $out = @{}
    foreach ($p in $Paths) {
        try {
            $v = (Get-ItemProperty -Path $p -Name Data -ErrorAction Stop).Data
            if ($v) { $out[$p] = $v }
        } catch { }
    }
    return $out
}

function Show-Blob($bytes) {
    $hex = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
    Write-Host ("  length : {0} bytes" -f $bytes.Count)
    Write-Host ("  hex    : {0}" -f $hex)
}

Section '1. Where the blob lives'
$found = Get-Blobs
if ($found.Count -eq 0) {
    Write-Host 'No night light blob found anywhere.' -ForegroundColor Yellow
    Write-Host 'That means night light has never been switched on. Turn it on once in'
    Write-Host 'Settings > System > Display > Night light, then run this again.'
    exit
}
foreach ($k in $found.Keys) {
    $tag = if ($k -like '*Cache*') { 'Cache' } elseif ($k -like '*Cloud*') { 'Cloud' } else { 'Current' }
    Write-Host ("[{0}]" -f $tag) -ForegroundColor Green
    Write-Host ("  {0}" -f $k) -ForegroundColor DarkGray
    Show-Blob $found[$k]
}

Section '2. Turn night light OFF'
Write-Host 'Open Settings > System > Display and make sure night light is OFF.'
Read-Host 'Press Enter once it is OFF'
$off = Get-Blobs
foreach ($k in $off.Keys) {
    $tag = if ($k -like '*Cache*') { 'Cache' } elseif ($k -like '*Cloud*') { 'Cloud' } else { 'Current' }
    Write-Host ("[{0}] OFF" -f $tag) -ForegroundColor Yellow
    Show-Blob $off[$k]
}

Section '3. Turn night light ON'
Write-Host 'Now switch night light ON in that same Settings page.'
Read-Host 'Press Enter once it is ON'
$on = Get-Blobs
foreach ($k in $on.Keys) {
    $tag = if ($k -like '*Cache*') { 'Cache' } elseif ($k -like '*Cloud*') { 'Cloud' } else { 'Current' }
    Write-Host ("[{0}] ON" -f $tag) -ForegroundColor Green
    Show-Blob $on[$k]
}

Section '4. What changed'
foreach ($k in $on.Keys) {
    if (-not $off.ContainsKey($k)) { continue }
    $a = $off[$k]; $b = $on[$k]
    $tag = if ($k -like '*Cache*') { 'Cache' } elseif ($k -like '*Cloud*') { 'Cloud' } else { 'Current' }
    Write-Host ("[{0}] {1} bytes OFF -> {2} bytes ON" -f $tag, $a.Count, $b.Count) -ForegroundColor White

    $min = [Math]::Min($a.Count, $b.Count)
    $diff = @()
    for ($i = 0; $i -lt $min; $i++) {
        if ($a[$i] -ne $b[$i]) { $diff += ('{0}: {1:x2} -> {2:x2}' -f $i, $a[$i], $b[$i]) }
    }
    if ($diff.Count -eq 0) {
        Write-Host '  no differing bytes in the overlapping region' -ForegroundColor DarkGray
    } else {
        Write-Host '  differing byte offsets:'
        $diff | ForEach-Object { Write-Host ("    {0}" -f $_) }
    }

    # Where does the 43 42 01 00 marker sit, and what follows it?
    foreach ($pair in @(@('OFF', $a), @('ON', $b))) {
        $label = $pair[0]; $bytes = $pair[1]
        $idx = -1
        for ($i = 0; $i -lt ($bytes.Count - 3); $i++) {
            if ($bytes[$i] -eq 0x43 -and $bytes[$i+1] -eq 0x42 -and $bytes[$i+2] -eq 0x01 -and $bytes[$i+3] -eq 0x00) { $idx = $i; break }
        }
        if ($idx -lt 0) {
            Write-Host ("  {0}: marker 43 42 01 00 NOT FOUND" -f $label) -ForegroundColor Red
        } else {
            $after = ''
            for ($j = $idx + 4; $j -lt [Math]::Min($idx + 10, $bytes.Count); $j++) { $after += ('{0:x2} ' -f $bytes[$j]) }
            Write-Host ("  {0}: marker at offset {1}, next bytes: {2}" -f $label, $idx, $after)
        }
    }
}

Write-Host ''
Write-Host 'Done. Copy everything above.' -ForegroundColor Cyan
