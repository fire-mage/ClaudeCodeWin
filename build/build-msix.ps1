# build-msix.ps1 — Build MSIX package for Microsoft Store
# Usage: .\build\build-msix.ps1 [-Runtime win-x64] [-Version 1.0.0]
# Output: dist\ClaudeCodeWin-<version>-<arch>.msix

param(
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $root "src\ClaudeCodeWin\ClaudeCodeWin.csproj"
$appxDir = Join-Path $root "build\appx"

# Determine architecture from runtime
$arch = if ($Runtime -match "arm64") { "arm64" } else { "x64" }

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ClaudeCodeWin MSIX Builder"           -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Runtime:       $Runtime"
Write-Host "Architecture:  $arch"
Write-Host "Version:       $Version"
Write-Host ""

# Step 1: Publish
Write-Host "[1/4] Publishing application..." -ForegroundColor Yellow
$publishDir = Join-Path $root "build\msix-publish-$arch"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish FAILED" -ForegroundColor Red
    exit 1
}

# Step 2: Create MSIX layout
Write-Host "[2/4] Creating MSIX layout..." -ForegroundColor Yellow
$layoutDir = Join-Path $root "build\msix-layout-$arch"
if (Test-Path $layoutDir) { Remove-Item $layoutDir -Recurse -Force }
New-Item -ItemType Directory -Path $layoutDir | Out-Null

# Copy published files
Copy-Item "$publishDir\*" $layoutDir -Recurse

# Copy assets
$assetsDir = Join-Path $layoutDir "Assets"
New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
Copy-Item (Join-Path $appxDir "Assets\*") $assetsDir

# Copy and patch manifest
$manifestTemplate = Get-Content (Join-Path $appxDir "AppxManifest.xml") -Raw
$manifestTemplate = $manifestTemplate -replace 'Version="[^"]*"', "Version=`"$Version`""
$manifestTemplate = $manifestTemplate -replace 'ProcessorArchitecture="[^"]*"', "ProcessorArchitecture=`"$arch`""
Set-Content (Join-Path $layoutDir "AppxManifest.xml") $manifestTemplate -Encoding UTF8

# Step 3: Find makeappx.exe
Write-Host "[3/4] Finding makeappx.exe..." -ForegroundColor Yellow
$sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
$makeappx = Get-ChildItem $sdkRoot -Recurse -Filter "makeappx.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -match "\\x64$" } |
    Sort-Object { [version]($_.DirectoryName -replace '.*\\(\d+\.\d+\.\d+\.\d+)\\.*', '$1') } -Descending |
    Select-Object -First 1

if (-not $makeappx) {
    Write-Host "makeappx.exe not found! Install Windows SDK." -ForegroundColor Red
    exit 1
}
Write-Host "Using: $($makeappx.FullName)"

# Step 4: Create MSIX
Write-Host "[4/4] Packing MSIX..." -ForegroundColor Yellow
$distDir = Join-Path $root "dist"
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

$msixPath = Join-Path $distDir "ClaudeCodeWin-$Version-$arch.msix"
& $makeappx.FullName pack /d $layoutDir /p $msixPath /o

if ($LASTEXITCODE -ne 0) {
    Write-Host "MSIX creation FAILED" -ForegroundColor Red
    exit 1
}

# Cleanup
Remove-Item $publishDir -Recurse -Force
Remove-Item $layoutDir -Recurse -Force

$sizeMB = [math]::Round((Get-Item $msixPath).Length / 1MB, 1)
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  MSIX ready: ClaudeCodeWin-$Version-$arch.msix ($sizeMB MB)" -ForegroundColor Green
Write-Host "  Location: $msixPath" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
