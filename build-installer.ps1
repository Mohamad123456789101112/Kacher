<#
.SYNOPSIS
    Builds the customer-facing Windows installer for Kacher POS.

.DESCRIPTION
    Publishes the app (self-contained win-x64) and wraps it in a single setup executable with
    Inno Setup 6. Output: .\dist\KacherPOS-<version>-setup.exe

    Inno Setup is free and tiny; if it is missing the script tells you exactly what to install
    instead of failing with a cryptic "iscc not found".

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist\KacherPos'
$iss = Join-Path $root 'installer\KacherPos.iss'

Write-Host ''
Write-Host "Kacher POS - installer build (v$Version)" -ForegroundColor Cyan
Write-Host '---------------------------------------' -ForegroundColor DarkGray

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'The .NET 8 SDK was not found: https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Red
    exit 1
}

# ── 1. Publish ───────────────────────────────────────────────────────────────────────────────────
if (-not $SkipPublish) {
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

    & dotnet publish (Join-Path $root 'src\Kacher.Pos\Kacher.Pos.csproj') `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $dist `
        -p:Version=$Version `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=embedded

    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Publish failed.' -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path (Join-Path $dist 'Kacher.Pos.exe'))) {
    Write-Host "No published app in $dist - run build-exe.ps1 first, or drop -SkipPublish." -ForegroundColor Red
    exit 1
}

# ── 2. Locate Inno Setup ─────────────────────────────────────────────────────────────────────────
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue)?.Source

if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ }

    $iscc = $candidates | Select-Object -First 1
}

if (-not $iscc) {
    Write-Host ''
    Write-Host 'Inno Setup 6 was not found.' -ForegroundColor Yellow
    Write-Host 'Install it (free):  winget install JRSoftware.InnoSetup' -ForegroundColor Yellow
    Write-Host 'Then run this script again.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "The published app is already ready at: $dist" -ForegroundColor Cyan
    exit 1
}

Write-Host "Compiler : $iscc"

# ── 3. Compile the installer ─────────────────────────────────────────────────────────────────────
$env:KACHER_VERSION = $Version

& $iscc $iss

if ($LASTEXITCODE -ne 0) {
    Write-Host 'Inno Setup reported an error (see the log above).' -ForegroundColor Red
    exit $LASTEXITCODE
}

$setup = Join-Path $root "dist\KacherPOS-$Version-setup.exe"

if (-not (Test-Path $setup)) {
    Write-Host "Setup was not produced at $setup" -ForegroundColor Red
    exit 1
}

$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)

Write-Host ''
Write-Host "Installer ready: $setup" -ForegroundColor Green
Write-Host "Size           : $size MB"
Write-Host ''
Write-Host 'Hand this single file to the customer.' -ForegroundColor Cyan
Write-Host 'The database lives in %LOCALAPPDATA%\Kacher and survives upgrades and uninstalls.' -ForegroundColor DarkGray
