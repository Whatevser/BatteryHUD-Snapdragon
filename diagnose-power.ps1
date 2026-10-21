# verify-powermode.ps1 - confirms the by-reference fix on this machine.
#
# Run in either Windows PowerShell or pwsh:
#     powershell -ExecutionPolicy Bypass -File .\diagnose-power.ps1
#
# This version NEVER passes the GUID by value. Doing so on ARM64 puts the GUID's first
# 8 bytes where powrprof expects a pointer, and the process dies with 0xC0000005 - which
# is exactly what happened when the previous version of this script tried it.
#
# No admin needed. Whatever mode is active when it starts is restored at the end.

$ErrorActionPreference = 'Continue'

function Section($t) {
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor DarkGray
    Write-Host $t -ForegroundColor Cyan
    Write-Host ('=' * 70) -ForegroundColor DarkGray
}

$SchemesKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes'

$Names = @('Best power efficiency', 'Balanced', 'Best performance')
$Guids = @('961cc777-2547-4f9d-8174-7d86181b8a7a',
           '00000000-0000-0000-0000-000000000000',
           'ded574b5-45a0-4f42-8737-46345c09c238')

# powrprof, declared the way the ARM64 ABI actually needs it.
$src = @(
    'using System;',
    'using System.Runtime.InteropServices;',
    'public static class PwrRef',
    '{',
    '    [DllImport("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]',
    '    public static extern uint GetEffective(out Guid g);',
    '    [DllImport("powrprof.dll", EntryPoint = "PowerGetActualOverlayScheme")]',
    '    public static extern uint GetActual(out Guid g);',
    '    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]',
    '    public static extern uint SetByRef(ref Guid g);',
    '}'
) -join "`r`n"

try { Add-Type -TypeDefinition $src -ErrorAction Stop }
catch { Write-Host "Add-Type failed: $_" -ForegroundColor Red; exit 1 }

Section '1. Starting state'
$before = Get-ItemProperty $SchemesKey -ErrorAction SilentlyContinue
Write-Host ('ActiveOverlayAcPowerScheme : {0}' -f $before.ActiveOverlayAcPowerScheme)
Write-Host ('ActiveOverlayDcPowerScheme : {0}' -f $before.ActiveOverlayDcPowerScheme)

$cur = [Guid]::Empty
if ([PwrRef]::GetEffective([ref]$cur) -eq 0) {
    Write-Host ('Effective overlay          : {0}' -f $cur)
} else {
    Write-Host 'Effective overlay          : could not read' -ForegroundColor Yellow
}
$original = $cur

Section '2. Setting each mode, GUID passed BY REFERENCE'
Write-Host '0 = success. 87 = ERROR_INVALID_PARAMETER.' -ForegroundColor DarkGray
Write-Host ''

for ($i = 0; $i -lt $Names.Count; $i++) {
    $g = [Guid]$Guids[$i]
    Write-Host ('--- {0}  ({1})' -f $Names[$i], $g) -ForegroundColor White
    try {
        $rc = [PwrRef]::SetByRef([ref]$g)
        $col = if ($rc -eq 0) { 'Green' } else { 'Red' }
        Write-Host ('    rc = {0}' -f $rc) -ForegroundColor $col
    } catch {
        Write-Host ('    threw {0}' -f $_.Exception.Message) -ForegroundColor Red
        continue
    }

    Start-Sleep -Milliseconds 250
    $now = Get-ItemProperty $SchemesKey -ErrorAction SilentlyContinue
    Write-Host ('    registry: ac={0} dc={1}' -f $now.ActiveOverlayAcPowerScheme, $now.ActiveOverlayDcPowerScheme) -ForegroundColor DarkGray

    $back = [Guid]::Empty
    if ([PwrRef]::GetActual([ref]$back) -eq 0) {
        $match = if ($back -eq $g) { 'MATCHES' } else { 'does NOT match' }
        $col = if ($back -eq $g) { 'Green' } else { 'Yellow' }
        Write-Host ('    actual  : {0}  ({1} what we asked for)' -f $back, $match) -ForegroundColor $col
    }
}

Section '3. Restoring'
try {
    $r = $original
    $rc = [PwrRef]::SetByRef([ref]$r)
    Write-Host ('restore to {0}: rc={1}' -f $original, $rc)
} catch {
    Write-Host "restore failed: $_" -ForegroundColor Yellow
}

$final = Get-ItemProperty $SchemesKey -ErrorAction SilentlyContinue
Write-Host ('ac: {0} -> {1}' -f $before.ActiveOverlayAcPowerScheme, $final.ActiveOverlayAcPowerScheme)
Write-Host ('dc: {0} -> {1}' -f $before.ActiveOverlayDcPowerScheme, $final.ActiveOverlayDcPowerScheme)

Write-Host ''
Write-Host 'If every rc above is 0, the by-reference declaration is correct and BatteryHud' -ForegroundColor Cyan
Write-Host 'will now be able to change the power mode.' -ForegroundColor Cyan
