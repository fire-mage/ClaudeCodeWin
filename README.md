# ClaudeCodeWin

A native Windows GUI for [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) — the AI coding assistant by Anthropic.

Helps you transition from Cursor to Claude Code without the terminal stress. All the power of Claude Code, wrapped in a familiar desktop app.

## Features

- Chat interface with streaming responses and thinking indicators
- Session persistence — resume where you left off
- Project folder management with recent projects
- File attachments via drag & drop or clipboard (screenshots)
- Git status in the status bar (branch + dirty count)
- Token usage tracking per session
- Prompt template scripts with variables (`{clipboard}`, `{git-status}`, `{git-diff}`, `{snapshot}`, `{file:path}`)
- Auto-injection of `CONTEXT_SNAPSHOT.md` on first message
- Dark theme (Catppuccin-inspired)
- Auto-updates

## Requirements

- Windows 10/11 (x64 or ARM64)
- [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) installed and authenticated

## Installation

Download the latest installer from [Releases](https://github.com/fire-mage/ClaudeCodeWin/releases).

## Building from Source

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
# Build
dotnet build src/ClaudeCodeWin

# Run
dotnet run --project src/ClaudeCodeWin

# Publish (single-file, self-contained)
dotnet publish src/ClaudeCodeWin -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist
```

## Creating an Installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
.\build\build-installer.ps1
```

## License

[MIT](LICENSE)
