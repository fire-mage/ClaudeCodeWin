# publish.ps1 — Build self-contained single-file executable
# Usage: .\build\publish.ps1
# Output: build\publish\ClaudeCodeWin.exe (+ runtime files)

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $root "src\ClaudeCodeWin\ClaudeCodeWin.csproj"
$publishDir = Join-Path $root "build\publish"

Write-Host "=== ClaudeCodeWin Publish ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host "Runtime:       $Runtime"
Write-Host "Output:        $publishDir"
Write-Host ""

# Clean previous publish
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

# Publish
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish FAILED" -ForegroundColor Red
    exit 1
}

# Show result
$exe = Get-Item (Join-Path $publishDir "ClaudeCodeWin.exe")
$sizeMB = [math]::Round($exe.Length / 1MB, 1)
Write-Host ""
Write-Host "Published: $($exe.FullName) ($sizeMB MB)" -ForegroundColor Green
