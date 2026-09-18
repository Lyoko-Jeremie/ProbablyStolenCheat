# Build and deploy the Probably Stolen Cheat plugin.
# Usage (close the game first - its dll is locked while the game runs):
#   powershell -ExecutionPolicy Bypass -File .\build-deploy.ps1
#
# NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 reads .ps1
# files as ANSI/GBK unless they carry a UTF-8 BOM, so non-ASCII text here would
# break parsing ('The string is missing the terminator').
$ErrorActionPreference = 'Stop'

$srcMod  = Join-Path $PSScriptRoot 'ProbablyStolenCheat'
$game    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$plugins = Join-Path $game 'BepInEx\plugins'
$srcDll  = Join-Path $srcMod 'bin\Release\net6.0\ProbablyStolenCheat.dll'
$dstDll  = Join-Path $plugins 'ProbablyStolenCheat.dll'
$srcUi   = Join-Path $srcMod 'ps-ui'
$dstUi   = Join-Path $plugins 'ps-cheat-web'

Write-Host '== Building ProbablyStolenCheat ==' -ForegroundColor Cyan
dotnet build $srcMod -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Build FAILED - nothing deployed.' -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $srcDll)) {
    Write-Host "Build output not found: $srcDll" -ForegroundColor Red
    exit 1
}

try {
    Copy-Item $srcDll $dstDll -Force
} catch {
    Write-Host 'Deploy FAILED - is the game still running? (the plugin dll is locked)' -ForegroundColor Yellow
    Write-Host $_.Exception.Message -ForegroundColor DarkGray
    exit 2
}

Write-Host "Deployed: $dstDll ($((Get-Item $dstDll).Length) bytes)" -ForegroundColor Green

# External UI page(s) served by the plugin at http://127.0.0.1:8787/
if (Test-Path $srcUi) {
    if (-not (Test-Path $dstUi)) {
        New-Item -ItemType Directory -Force -Path $dstUi | Out-Null
    }
    Get-ChildItem $srcUi -File | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $dstUi $_.Name) -Force
        Write-Host ("Deployed UI: " + (Join-Path $dstUi $_.Name)) -ForegroundColor Green
    }
} else {
    Write-Host "UI folder not found, skipped: $srcUi" -ForegroundColor DarkGray
}

Write-Host 'In game: F9 = cheat panel, F5 = +1,000,000, F8 = item spawn menu, F10 = edit right-clicked item' -ForegroundColor Green
Write-Host 'External UI: http://127.0.0.1:8787/' -ForegroundColor Green
