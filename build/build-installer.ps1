# build-installer.ps1 — Full pipeline: publish + create installer
# Usage: .\build\build-installer.ps1
# Output: dist\ClaudeCodeWin-Setup-1.0.0.exe

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ClaudeCodeWin Installer Builder"       -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Publish
Write-Host "[1/2] Publishing application..." -ForegroundColor Yellow
& "$PSScriptRoot\publish.ps1"
if ($LASTEXITCODE -ne 0) { exit 1 }

# Step 2: Create installer with Inno Setup
Write-Host ""
Write-Host "[2/2] Creating installer..." -ForegroundColor Yellow

# Find ISCC.exe
$isccPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
)

$iscc = $isccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup not found!" -ForegroundColor Red
    Write-Host "Install from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "After installing, run:" -ForegroundColor Gray
    Write-Host '  & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" build\installer.iss' -ForegroundColor White
    Write-Host ""
    Write-Host "Or open build\installer.iss in Inno Setup GUI and press Ctrl+F9" -ForegroundColor Gray
    exit 1
}

Write-Host "Using: $iscc"

# Create dist directory
$distDir = Join-Path $root "dist"
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

& $iscc "$PSScriptRoot\installer.iss"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installer creation FAILED" -ForegroundColor Red
    exit 1
}

$installer = Get-ChildItem (Join-Path $distDir "ClaudeCodeWin-Setup-*.exe") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$sizeMB = [math]::Round($installer.Length / 1MB, 1)

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Installer ready: $($installer.Name) ($sizeMB MB)" -ForegroundColor Green
Write-Host "  Location: $($installer.FullName)" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
