# Plan: SSH Key + Server Registry for ClaudeCodeWin

## Motivation

User generates an SSH key specifically for Claude's use. Currently, Claude has to search for the key every time and doesn't know which key is "his". We need:

1. **SSH Key setting** — path to Claude's private SSH key, stored in AppSettings
2. **Server registry** — list of servers where this key works, with associated projects
3. **System prompt injection** — so Claude knows the key path and server list without asking

## Architecture

### 1. Data Model

**AppSettings.cs** — add:
```csharp
public string? SshKeyPath { get; set; }  // Path to Claude's private SSH key
public List<ServerInfo> Servers { get; set; } = [];
```

**New file: Models/ServerInfo.cs**
```csharp
public class ServerInfo
{
    public string Name { get; set; } = "";           // e.g. "test-admin", "prod-admin"
    public string Host { get; set; } = "";            // e.g. "192.0.2.1"
    public int Port { get; set; } = 22;
    public string User { get; set; } = "root";        // SSH user
    public string? Description { get; set; }           // e.g. "Test server for app.example.com"
    public List<string> Projects { get; set; } = [];   // Project names deployed here
}
```

### 2. Storage

Everything goes into existing `%APPDATA%\ClaudeCodeWin\settings.json` via existing `SettingsService`. No new files needed — just new properties in AppSettings.

### 3. System Prompt Injection

In `MainViewModel.SystemInstruction`, add a new section describing SSH capabilities:

```
## SSH Access
- Claude's SSH private key: `{path}`
- When deploying or connecting via SSH, use this key with `-i "{path}"` flag
- Known servers:
  - **test-server** — root@192.0.2.1:22 (Test server: my-project)
  - **prod-server** — root@203.0.113.42:22 (Production: my-project)
```

BUT: SystemInstruction is a `const string`. To make it dynamic, change it to a method or build it at runtime in the preamble construction code (line ~527).

**Approach**: Keep `SystemInstruction` as the static base. Add SSH/server info dynamically in the preamble construction block (where context-snapshot and project-registry are already appended).

Add after project-registry injection:
```csharp
var sshInfo = BuildSshInfo();
if (!string.IsNullOrEmpty(sshInfo))
    preamble += $"\n\n<ssh-access>\n{sshInfo}\n</ssh-access>";
```

### 4. GUI — Menu Entry for Managing SSH & Servers

Add a new **"Servers"** menu item to MainWindow menu bar (between Scripts and Tasks).

**Option A (simpler)**: Just a menu with sub-items:
- "Set SSH Key..." — file dialog to pick the key file
- Separator
- "Add Server..." — dialog to add server
- List of existing servers with right-click to edit/remove

**Option B**: A dedicated `ServerRegistryWindow.xaml` — a small window with:
- SSH key path field with browse button
- DataGrid of servers (Name, Host, Port, User, Projects)
- Add/Edit/Remove buttons

**Recommendation: Option B** — it's cleaner for managing a list of servers with projects.

### 5. Files to Create/Modify

| File | Action |
|------|--------|
| `Models/AppSettings.cs` | Add `SshKeyPath` and `Servers` properties |
| `Models/ServerInfo.cs` | **New** — server model |
| `ViewModels/MainViewModel.cs` | Add `BuildSshInfo()` method, inject into preamble, add `ManageServersCommand` |
| `ServerRegistryWindow.xaml` | **New** — server management dialog |
| `ServerRegistryWindow.xaml.cs` | **New** — code-behind for dialog |
| `MainWindow.xaml` | Add "Servers" menu item |
| `App.xaml.cs` | No changes needed (settings already flow through) |

### 6. Implementation Steps

1. Create `ServerInfo.cs` model
2. Add properties to `AppSettings.cs`
3. Create `ServerRegistryWindow.xaml` + code-behind
4. Add "Servers" menu item to `MainWindow.xaml`
5. Wire up menu click to open ServerRegistryWindow
6. Add `BuildSshInfo()` to MainViewModel and inject into preamble
7. Build and test
