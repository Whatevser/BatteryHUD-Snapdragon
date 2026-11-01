<#
    BatteryHud build helper.

    Usage:
        .\build.ps1                    # framework-dependent win-arm64 (small, needs .NET 10 Desktop Runtime)
        .\build.ps1 -SelfContained     # standalone win-arm64 (no runtime install needed, much larger)
        .\build.ps1 -Rid win-x64       # only if you want to test on an x64 box

    Requires the .NET 10 SDK for ARM64:
        winget install Microsoft.DotNet.SDK.10
    Verify you got the native one (not the emulated x64 SDK):
        dotnet --info      ->  "Architecture: arm64"
#>

[CmdletBinding()]
param(
    [ValidateSet("win-arm64", "win-x64")]
    [string]$Rid = "win-arm64",
    [switch]$SelfContained,
    [switch]$SingleFile = $true,
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\BatteryHud.csproj"
$out = Join-Path $root "publish\$Rid"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet not found. Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10"
}

$sdk = (& dotnet --version).Trim()
if ([int]($sdk.Split('.')[0]) -lt 10) {
    throw "The .NET 10 SDK is required (found $sdk). Install it with: winget install Microsoft.DotNet.SDK.10"
}

Write-Host "SDK check:" -ForegroundColor Cyan
$info = & dotnet --info
$arch = ($info | Select-String -Pattern '^\s*Architecture:\s*(\S+)' | Select-Object -First 1).Matches.Groups[1].Value
Write-Host "  dotnet SDK $sdk, host architecture: $arch"
if ($Rid -eq "win-arm64" -and $arch -ne "arm64") {
    Write-Warning "Your dotnet host is '$arch'. The produced binary will still be ARM64, but you are building through emulation. Install the arm64 SDK for a fully native toolchain."
}

# Not "$args": that is a PowerShell automatic variable and must not be reassigned.
# DebugType is left to the csproj (portable): an embedded PDB would be carried inside the
# single-file exe and inflate it for no runtime benefit.
$publishArgs = @(
    "publish", $proj,
    "-c", $Configuration,
    "-r", $Rid,
    "-o", $out,
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),
    "-p:PublishSingleFile=$($SingleFile.ToString().ToLower())",
    "-p:PublishTrimmed=false",
    "-p:IncludeNativeLibrariesForSelfExtract=true"
)

Write-Host "`ndotnet $($publishArgs -join ' ')`n" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "`nOutput: $out" -ForegroundColor Green
Write-Host "Run BatteryHud.exe from there. Drop an empty 'portable.txt' next to it to keep config.json alongside the exe."
