using System.IO;
using System.Text.Json;
using System.Windows;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    // ─── Project-level utilities ───

    internal void TryRegisterProjectFromToolUse(string toolName, string inputJson)
    {
        string? filePath = null;
        try
        {
            if (string.IsNullOrEmpty(inputJson) || !inputJson.StartsWith('{'))
                return;

            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;

            filePath = toolName switch
            {
                "Read" or "Write" or "Edit" =>
                    root.TryGetProperty("file_path", out var fp) ? fp.GetString() : null,
                "NotebookEdit" =>
                    root.TryGetProperty("notebook_path", out var np) ? np.GetString() : null,
                "Glob" or "Grep" =>
                    root.TryGetProperty("path", out var p) ? p.GetString() : null,
                _ => null
            };
        }
        catch { return; }

        if (string.IsNullOrEmpty(filePath) || !Path.IsPathRooted(filePath))
            return;

        var projectRoot = ProjectRegistryService.DetectProjectRoot(filePath);
        if (projectRoot is null || !_registeredProjectRoots.TryAdd(projectRoot, 0))
            return;

        if (!string.IsNullOrEmpty(WorkingDirectory))
        {
            var currentDir = Path.GetFullPath(WorkingDirectory);
            var detectedDir = Path.GetFullPath(projectRoot);
            if (currentDir.IsSubPathOf(detectedDir))
                return;
        }

        _ = Task.Run(() => _projectRegistry.RegisterProject(projectRoot, _gitService));
    }

    /// <summary>Recall the last sent message (delegate to ActiveChatSession).</summary>
    public bool RecallLastMessage() => ActiveChatSession?.RecallLastMessage() ?? false;

    /// <summary>Handle Escape key (delegate to ActiveChatSession).</summary>
    public bool HandleEscape()
    {
        var result = ActiveChatSession?.HandleEscape() ?? false;
        if (result && _teamPausedForConflict)
            ResumeTeamAfterConflict();
        return result;
    }

    public void AddTaskOutput(string taskName, string output)
    {
        ActiveChatSession?.AddTaskOutput(taskName, output);
    }

    private void ShowFileDiff(string filePath)
    {
        var cli = ActiveChatSession?.CliService ?? _cliService;
        var oldContent = cli.GetFileSnapshot(filePath);

        string? newContent;
        try
        {
            newContent = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }
        catch
        {
            newContent = null;
        }

        if (oldContent is null && newContent is null)
        {
            MessageBox.Show($"Cannot read file:\n{filePath}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var diff = DiffService.ComputeDiff(oldContent, newContent);

        var viewer = new DiffViewerWindow(filePath, diff);
        if (Application.Current?.MainWindow is { } mainWin)
            viewer.Owner = mainWin;
        viewer.Show();
    }

    private static void ShowImagePreview(FileAttachment att)
    {
        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow is not null)
            Infrastructure.ImagePreviewHelper.ShowPreviewWindow(mainWindow, att.FilePath, att.FileName);
    }
}
