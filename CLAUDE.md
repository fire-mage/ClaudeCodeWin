# CLAUDE.md

## Project Overview

ClaudeCodeWin — native WPF wrapper for Claude Code CLI. Provides GUI with multi-line input, notifications, drag & drop files, and custom scripts.

## Tech Stack

- .NET 9.0, WPF
- **Zero NuGet dependencies** — everything uses built-in .NET 9 APIs
- MVVM with custom ViewModelBase + RelayCommand

## Build & Run

```powershell
dotnet build                    # Build
dotnet run --project src/ClaudeCodeWin  # Run
dotnet publish -c Release       # Publish
```

## Architecture

```
src/ClaudeCodeWin/
├── Infrastructure/       # ViewModelBase, RelayCommand, Win32Interop (P/Invoke)
├── Models/               # ChatMessage, SessionState, ScriptDefinition, FileAttachment, AppSettings
├── ViewModels/           # MainViewModel, MessageViewModel, ToolUseViewModel
├── Services/             # ClaudeCliService, NotificationService, ScriptService, SettingsService
├── Views/                # (reserved for future UserControls)
├── Converters/           # BoolToVisibility, RoleToAlignment, RoleToBubbleBrush
├── Resources/Themes/     # Dark.xaml (Catppuccin-inspired dark theme)
├── MainWindow.xaml/.cs   # Main UI layout
└── App.xaml/.cs          # Entry point, manual DI
```

## Key Components

- **ClaudeCliService** — launches `claude -p --output-format stream-json --verbose`, parses JSON stream, fires events (OnTextDelta, OnToolUseStarted, OnCompleted, OnError). Uses `--resume <session_id>` for follow-up messages.
- **NotificationService** — FlashWindowEx Win32 API + TaskbarItemInfo overlay + SystemSounds when window is inactive.
- **ScriptService** — loads scripts from `%AppData%/ClaudeCodeWin/scripts.json`, creates dynamic menu items with hotkey bindings.
- **SettingsService** — reads/writes `%AppData%/ClaudeCodeWin/settings.json`.

## Keyboard Shortcuts

- **Ctrl+Enter** — send message
- **Escape** — cancel current processing
- **Ctrl+N** — new session
- **Ctrl+V** — paste screenshot from clipboard
- **Drag & drop** — attach files

## Key Rules

- **Minimize dependencies** — prefer built-in .NET 9 APIs; confirm any new NuGet dependency with the user before adding
- **All UI text in English** (labels, buttons, messages in the app interface)
- **Dark theme only** (defined in Resources/Themes/Dark.xaml)
