# Remove native Claude Code CLI binary so CCW falls back to npm version.
# Run this script AFTER closing CCW (the binary is locked while CCW runs).

$nativePath = Join-Path $env:USERPROFILE ".local\bin\claude.exe"
$backupPath = "$nativePath.v2.1.56.bak"

if (-not (Test-Path $nativePath)) {
    Write-Host "Native binary not found at $nativePath — nothing to do." -ForegroundColor Yellow
    exit 0
}

# Rename instead of delete (safer — keeps a backup)
try {
    Rename-Item -Path $nativePath -NewName (Split-Path $backupPath -Leaf) -Force
    Write-Host "Native CLI renamed to $backupPath" -ForegroundColor Green
} catch {
    Write-Host "ERROR: Cannot rename $nativePath — is CCW still running?" -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
}

# Verify npm version is available
$npmClaude = Join-Path $env:APPDATA "npm\claude.cmd"
if (Test-Path $npmClaude) {
    $version = & cmd.exe /c "`"$npmClaude`" --version" 2>&1
    Write-Host "npm CLI found: $npmClaude → $version" -ForegroundColor Green
} else {
    Write-Host "WARNING: npm CLI not found at $npmClaude" -ForegroundColor Yellow
    Write-Host "CCW will not be able to find Claude Code CLI." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done. Start CCW — it will now use npm v2.1.48." -ForegroundColor Cyan
