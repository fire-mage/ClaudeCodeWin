# Plan: Add MSIX packaging to ClaudeCodeWin

## Goal
Add optional MSIX package generation alongside existing EXE/Setup builds.
MSIX will be used for Microsoft Store submission (Store signs it automatically).

## Files to create

### 1. `build/appx/AppxManifest.xml`
Package manifest for MSIX. Key values:
- `Identity Name="ClaudeCodeWin"`
- `Identity Publisher="CN=F17AD68F-9073-4654-A0A0-86377E85A593"`
- `Identity Version` — injected at build time (e.g. `1.0.71.0`)
- `Identity ProcessorArchitecture` — `x64` or `arm64` (injected at build time)
- `EntryPoint="Windows.FullTrustApplication"` — required for WPF desktop app
- `Capability: runFullTrust` — needed to launch claude CLI and access filesystem
- Visual elements: DisplayName, Description, BackgroundColor, logos
- MinVersion: `10.0.17763.0` (Windows 10 1809)

### 2. `build/appx/Assets/` — logo images
Generate from existing `src/ClaudeCodeWin/app.ico` using PowerShell + System.Drawing:
- `Square44x44Logo.png` (44x44)
- `Square150x150Logo.png` (150x150)
- `StoreLogo.png` (200x200)

3 images is the minimum required. Will use a PowerShell script to resize.

### 3. `build/build-msix.ps1` — local MSIX build script
Steps:
1. Run `dotnet publish` (same as existing publish.ps1)
2. Copy published files to temp layout directory
3. Copy AppxManifest.xml, inject version + architecture
4. Copy Assets/
5. Find `makeappx.exe` in Windows SDK
6. Run `makeappx.exe pack /d layout /p output.msix`

### 4. Update `.github/workflows/build.yml`
After existing x64/arm64 build steps, add MSIX creation:
- Find makeappx.exe path (glob Windows Kits directory)
- Create layout dir with publish output + manifest + assets
- Inject version and arch into manifest
- `makeappx.exe pack` for each architecture
- Upload .msix files alongside .exe in GitHub Release

## What does NOT change
- Existing EXE + Inno Setup pipeline — untouched
- Application code — no changes needed
- S3 upload / version.json — unchanged (MSIX only goes to GitHub Release + Store)

## Architecture decision
Using `makeappx.exe` directly (not WAP project or GenerateAppxPackageOnBuild) because:
- Simpler, no Visual Studio dependency
- Full control over manifest
- Works on any windows runner with Windows SDK
- Easy to maintain alongside existing Inno Setup build

## Sequence of implementation
1. Create asset images (resize app.ico → 3 PNGs)
2. Create AppxManifest.xml template
3. Create build-msix.ps1 local script
4. Update GitHub Actions workflow
5. Test build locally
