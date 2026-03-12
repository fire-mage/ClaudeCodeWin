using System.IO;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class WorkspaceService
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly object _lock = new();

    public WorkspaceService(SettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
        _settings = settings;
    }

    public IReadOnlyList<Workspace> GetAll()
    {
        lock (_lock) return _settings.Workspaces.ToList();
    }

    public Workspace? GetById(string id)
    {
        lock (_lock) return _settings.Workspaces.FirstOrDefault(w => w.Id == id)?.Clone();
    }

    public Workspace CreateWorkspace(string name, IEnumerable<string> projectPaths, string primaryPath)
    {
        var workspace = new Workspace
        {
            Name = name,
            PrimaryProjectPath = Path.GetFullPath(primaryPath),
            Projects = projectPaths.Select(p => new WorkspaceProject
            {
                Path = Path.GetFullPath(p)
            }).ToList()
        };

        lock (_lock)
        {
            _settings.Workspaces.Add(workspace);
            _settingsService.Save(_settings);
        }
        return workspace;
    }

    public void DeleteWorkspace(string id)
    {
        lock (_lock)
        {
            _settings.Workspaces.RemoveAll(w => w.Id == id);
            _settingsService.Save(_settings);
        }
    }

    public void AddProject(string workspaceId, string path, string? role = null)
    {
        lock (_lock)
        {
            var ws = _settings.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
            if (ws is null) return;

            var normalized = Path.GetFullPath(path);
            if (ws.Projects.Any(p => string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase)))
                return;

            ws.Projects.Add(new WorkspaceProject { Path = normalized, Role = role });
            _settingsService.Save(_settings);
        }
    }

    public void RemoveProject(string workspaceId, string path)
    {
        lock (_lock)
        {
            var ws = _settings.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
            if (ws is null) return;

            var normalized = Path.GetFullPath(path);
            ws.Projects.RemoveAll(p => string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase));

            if (string.Equals(ws.PrimaryProjectPath, normalized, StringComparison.OrdinalIgnoreCase))
                ws.PrimaryProjectPath = ws.Projects.FirstOrDefault()?.Path;

            _settingsService.Save(_settings);
        }
    }

    public void SetPrimary(string workspaceId, string path)
    {
        lock (_lock)
        {
            var ws = _settings.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
            if (ws is null) return;

            var normalized = Path.GetFullPath(path);
            if (!ws.Projects.Any(p => string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase)))
                return;

            ws.PrimaryProjectPath = normalized;
            _settingsService.Save(_settings);
        }
    }

    public void UpdateWorkspace(Workspace workspace)
    {
        lock (_lock)
        {
            var index = _settings.Workspaces.FindIndex(w => w.Id == workspace.Id);
            if (index < 0) return;
            _settings.Workspaces[index] = workspace;
            _settingsService.Save(_settings);
        }
    }

    public void TouchLastOpened(string workspaceId)
    {
        lock (_lock)
        {
            var ws = _settings.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
            if (ws is null) return;
            ws.LastOpenedAt = DateTime.Now;
            _settingsService.Save(_settings);
        }
    }
}
