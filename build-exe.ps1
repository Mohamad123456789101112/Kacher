<#
.SYNOPSIS
    Builds Kacher POS into a runnable Windows application folder (and optionally a zip).

.DESCRIPTION
    Produces a self-contained win-x64 publish of the WPF app in .\dist\KacherPos.
    "Self-contained" means the target machine does not need the .NET 8 Desktop Runtime installed —
    the folder carries everything, which is what you want for a till that must survive a clean PC.

    Requires the .NET 8 SDK *on Windows*. WPF cannot be published from Linux/macOS: the XAML
    markup compiler and the Windows Desktop targeting pack are Windows-only.

.PARAMETER FrameworkDependent
    Publish without bundling the runtime (smaller output, requires the .NET 8 Desktop Runtime).

.PARAMETER Zip
    Also produce .\dist\KacherPos-win-x64.zip for copying to the terminal.

.EXAMPLE
    .\build-exe.ps1
    .\build-exe.ps1 -Zip
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\Kacher.Pos\Kacher.Pos.csproj'
$output = Join-Path $root 'dist\KacherPos'

Write-Host ''
Write-Host 'Kacher POS — Windows build' -ForegroundColor Cyan
Write-Host '--------------------------' -ForegroundColor DarkGray

# ── Toolchain ────────────────────────────────────────────────────────────────────────────────────
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue

if (-not $sdk) {
    Write-Host 'The .NET 8 SDK was not found.' -ForegroundColor Red
    Write-Host 'Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this script again.'
    exit 1
}

$version = (& dotnet --version).Trim()
Write-Host "SDK          : $version"

if ($version -notlike '8.*') {
    Write-Host "Warning: this project targets .NET 8; SDK $version may still work but is untested." -ForegroundColor Yellow
}

if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) {
    Write-Host 'This script must run on Windows: WPF output cannot be produced elsewhere.' -ForegroundColor Red
    exit 1
}

# ── Publish ──────────────────────────────────────────────────────────────────────────────────────
if (Test-Path $output) {
    Write-Host 'Cleaning previous output…' -ForegroundColor DarkGray
    Remove-Item $output -Recurse -Force
}

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

$publishArgs = @(
    'publish', $project,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    "--self-contained", $selfContained,
    '--output', $output,
    # Trimming and single-file are deliberately off: both break WPF (reflection over XAML types
    # and native imaging libraries). A folder publish is boring, but it starts every time.
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=embedded'
)

Write-Host ''
Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
Write-Host ''

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'Build failed. The compiler output above lists the errors.' -ForegroundColor Red
    exit $LASTEXITCODE
}

# ── Ship it ─────────────────────────────────────────────────────────────────────────────────────
$exe = Join-Path $output 'Kacher.Pos.exe'

if (-not (Test-Path $exe)) {
    Write-Host 'Publish reported success but Kacher.Pos.exe is missing.' -ForegroundColor Red
    exit 1
}

$size = [math]::Round((Get-ChildItem $output -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ''
Write-Host "Ready: $exe" -ForegroundColor Green
Write-Host "Size : $size MB"

if ($Zip) {
    $zipPath = Join-Path $root 'dist\KacherPos-win-x64.zip'

    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Zip  : $zipPath" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Run it now with:' -ForegroundColor Cyan
Write-Host "  $exe"
Write-Host ''
Write-Host 'First launch creates the database in %LOCALAPPDATA%\Kacher\kacher.db and signs in with PIN 1234 (admin).' -ForegroundColor DarkGray
