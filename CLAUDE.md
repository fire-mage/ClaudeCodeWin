# CLAUDE.md

## Project Overview

ClaudeCodeWin — native WPF desktop client for Claude Code CLI. Provides a rich GUI with chat interface, file explorer with code editor, team task management, notepad, knowledge base, and extensible scripts/tasks system.

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
├── Infrastructure/       # ViewModelBase, RelayCommand, Win32Interop, PathHelper, JsonDefaults
├── Models/               # 35+ models (ChatMessage, BacklogFeature, TeamState, FileNode, etc.)
├── ViewModels/           # MainViewModel (partial: Messaging, Session, Controls, Explorer,
│                         #   Review, BackgroundTasks, Nudge, Utilities), MessageViewModel,
│                         #   TeamViewModel, ExplorerViewModel, NotepadViewModel, etc.
├── Services/             # 36+ services (see Key Components below)
├── Services/Highlighting/# Syntax tokenizers + completion providers for 13 languages
├── Views/                # ChatControl, FileExplorerControl, CodeEditorControl,
│                         #   MessageComposerControl, NotepadControl, TeamPanelControl, etc.
├── Converters/           # BoolToVisibility, RoleToAlignment, NullToCollapsed, ExpandArrow
├── Resources/Themes/     # Dark.xaml (Catppuccin-inspired dark theme)
├── MainWindow.xaml/.cs   # Main UI layout with tab system
├── *Window.xaml          # 12+ dialog windows (Settings, About, DiffViewer, HealthCheck, etc.)
└── App.xaml/.cs          # Entry point, manual DI
```

## Key Components

### Core
- **ClaudeCliService** — launches `claude -p --output-format stream-json --verbose`, parses JSON stream, fires events (OnTextDelta, OnToolUseStarted, OnCompleted, OnError). Uses `--resume <session_id>` for follow-up messages.
- **StreamJsonParser** — parses newline-delimited JSON from CLI stdout.
- **ChatHistoryService** — saves/restores chat sessions per project folder.
- **SettingsService** — reads/writes `%AppData%/ClaudeCodeWin/settings.json`.
- **UsageService** — tracks API usage with periodic refresh.

### UI Features
- **NotificationService** — FlashWindowEx Win32 API + TaskbarItemInfo overlay + SystemSounds when window is inactive.
- **ScriptService** — loads prompt scripts from `%AppData%/ClaudeCodeWin/scripts.json` with variable substitution and hotkey bindings.
- **TaskRunnerService** — runs shell tasks from `%AppData%/ClaudeCodeWin/tasks.json`, grouped by project.
- **DiffService** — renders file diffs in a dedicated viewer window.
- **NotepadStorageService** — per-project notepad with persistent storage.

### File Explorer & Code Editor
- **FileExplorerControl** — TreeView-based file browser with colored names by file type.
- **CodeEditorControl** — custom syntax highlighting (no AvalonEdit), bracket matching, IntelliSense.
- **Services/Highlighting/** — tokenizers + completion providers for 13 languages: C#, HTML, CSS, Python, JS, TS, Java, C/C++, Go, Rust, PHP, Swift, Kotlin, SQL.
- **FileIndexService** — indexes project files for search and navigation.

### Team Management System
- **TeamOrchestratorService** — autonomous pipeline: Planning → Plan Review → Backlog → Queue → Completed.
- **PlannerService** — generates implementation plans from task descriptions.
- **PlanReviewerService** — reviews and refines plans before execution.
- **BacklogService** — manages feature backlog with priorities and statuses.
- **ReviewService** — code review loop with retry detection (max 11 retries).
- **GitService** — git operations for the team pipeline (branch, commit, diff).
- **TeamNotesService** + **TeamNotesDetector** — team communication notes.

### Knowledge & Updates
- **KnowledgeBaseService** — local KB articles in `memory/knowledge-base/`.
- **DevKbService** — syncs developer articles from remote S3 manifest.
- **MarketplaceService** — built-in + custom skill plugins.
- **McpRegistryService** — MCP server registry management.
- **UpdateService** + **CliUpdateService** — app and CLI update checks.
- **HealthCheckService** + **ClaudeCodeDependencyService** — system health and dependency checks.

### Context Injection
- **InstructionsService** — manages system instructions injected into Claude's context on each session start (project registry, SSH access, developer KB articles, team state variables).
- **ProjectRegistryService** — tracks all known local projects with git remotes and tech stacks.

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
- **Delegate complex tasks to Team** — for multi-file features, refactoring, or any task that can be described as a self-contained unit of work, prefer delegating to the Team pipeline via `team-task` code blocks rather than doing everything in the main chat session. This keeps the main conversation focused and leverages parallel execution.
