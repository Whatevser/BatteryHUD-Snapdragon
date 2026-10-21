<#
    BatteryHud build helper.

    Usage:
        .\build.ps1                    # framework-dependent win-arm64 (small, needs .NET 8 Desktop Runtime)
        .\build.ps1 -SelfContained     # standalone win-arm64 (~90 MB, no runtime install needed)
        .\build.ps1 -Rid win-x64       # only if you want to test on an x64 box

    Requires the .NET 8 SDK for ARM64:
        winget install Microsoft.DotNet.SDK.8
    Verify you got the native one (not the emulated x64 SDK):
        dotnet --info      ->  "Architecture: arm64"
#>

[CmdletBinding()]
param(
    [string]$Rid = "win-arm64",
    [switch]$SelfContained,
    [switch]$SingleFile = $true,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\BatteryHud.csproj"
$out = Join-Path $root "publish\$Rid"

Write-Host "SDK architecture check:" -ForegroundColor Cyan
$info = & dotnet --info
$arch = ($info | Select-String -Pattern '^\s*Architecture:\s*(\S+)').Matches.Groups[1].Value
Write-Host "  dotnet host architecture: $arch"
if ($Rid -eq "win-arm64" -and $arch -ne "arm64") {
    Write-Warning "Your dotnet host is '$arch'. The produced binary will still be ARM64, but you are building through emulation. Install the arm64 SDK for a fully native toolchain."
}

$args = @(
    "publish", $proj,
    "-c", $Configuration,
    "-r", $Rid,
    "-o", $out,
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),
    "-p:PublishSingleFile=$($SingleFile.ToString().ToLower())",
    "-p:PublishTrimmed=false",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=embedded"
)

Write-Host "`ndotnet $($args -join ' ')`n" -ForegroundColor DarkGray
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "`nOutput: $out" -ForegroundColor Green
Write-Host "Run BatteryHud.exe from there. Drop an empty 'portable.txt' next to it to keep config.json alongside the exe."
