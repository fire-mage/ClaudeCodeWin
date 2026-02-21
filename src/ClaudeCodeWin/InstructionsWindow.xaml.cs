using System.Windows;
using System.Windows.Controls;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class InstructionsWindow : Window
{
    private readonly InstructionsService _instructions;
    private readonly string? _workingDir;
    private readonly string _systemInstruction;

    // Original content for dirty tracking
    private string? _globalOriginal;
    private string? _projectOriginal;
    private string? _memoryOriginal;

    // Paths
    private readonly string _globalPath;
    private readonly string? _projectPath;
    private readonly string? _memoryPath;

    private bool _suppressTextChanged;

    public InstructionsWindow(InstructionsService instructions, string? workingDir, string systemInstruction)
    {
        InitializeComponent();

        _instructions = instructions;
        _workingDir = workingDir;
        _systemInstruction = systemInstruction;

        _globalPath = instructions.GetGlobalClaudeMdPath();
        _projectPath = !string.IsNullOrEmpty(workingDir) ? instructions.GetProjectClaudeMdPath(workingDir) : null;
        _memoryPath = !string.IsNullOrEmpty(workingDir) ? instructions.GetMemoryPath(workingDir) : null;

        LoadAllTabs();
    }

    private void LoadAllTabs()
    {
        _suppressTextChanged = true;

        // Global CLAUDE.md
        _globalOriginal = _instructions.ReadFile(_globalPath);
        if (_globalOriginal is not null)
        {
            GlobalEditor.Text = _globalOriginal;
            GlobalEditor.Visibility = Visibility.Visible;
            GlobalPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            GlobalEditor.Visibility = Visibility.Collapsed;
            GlobalPlaceholder.Visibility = Visibility.Visible;
        }
        UpdateStatus(GlobalStatus, _globalPath);

        // Project CLAUDE.md
        if (_projectPath is not null)
        {
            _projectOriginal = _instructions.ReadFile(_projectPath);
            if (_projectOriginal is not null)
            {
                ProjectEditor.Text = _projectOriginal;
                ProjectEditor.Visibility = Visibility.Visible;
                ProjectPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                ProjectEditor.Visibility = Visibility.Collapsed;
                ProjectPlaceholder.Visibility = Visibility.Visible;
                ProjectPlaceholderText.Text = "This file does not exist yet.";
                ProjectCreateButtons.Visibility = Visibility.Visible;
            }
            UpdateStatus(ProjectStatus, _projectPath);
        }
        else
        {
            ProjectEditor.Visibility = Visibility.Collapsed;
            ProjectPlaceholder.Visibility = Visibility.Visible;
            ProjectPlaceholderText.Text = "No project folder is open. Open a project first.";
            ProjectCreateButtons.Visibility = Visibility.Collapsed;
            ProjectStatus.Text = "No project";
        }

        // CCW System Instruction (read-only)
        SystemEditor.Text = _systemInstruction;

        // Memory
        if (_memoryPath is not null)
        {
            _memoryOriginal = _instructions.ReadFile(_memoryPath);
            if (_memoryOriginal is not null)
            {
                MemoryEditor.Text = _memoryOriginal;
                MemoryEditor.Visibility = Visibility.Visible;
                MemoryPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                MemoryEditor.Visibility = Visibility.Collapsed;
                MemoryPlaceholder.Visibility = Visibility.Visible;
                MemoryPlaceholderText.Text = "No memory file exists for this project.";
            }
            UpdateStatus(MemoryStatus, _memoryPath);
        }
        else
        {
            MemoryEditor.Visibility = Visibility.Collapsed;
            MemoryPlaceholder.Visibility = Visibility.Visible;
            MemoryPlaceholderText.Text = "No project folder is open.";
            MemoryStatus.Text = "No project";
        }

        _suppressTextChanged = false;

        // Reset dirty state
        GlobalSaveBtn.IsEnabled = false;
        GlobalRevertBtn.IsEnabled = false;
        ProjectSaveBtn.IsEnabled = false;
        ProjectRevertBtn.IsEnabled = false;
        MemorySaveBtn.IsEnabled = false;
        MemoryRevertBtn.IsEnabled = false;
    }

    private void UpdateStatus(TextBlock statusBlock, string path)
    {
        if (_instructions.FileExists(path))
        {
            var size = _instructions.GetFileSize(path);
            var sizeText = size < 1024 ? $"{size} B" : $"{size / 1024.0:F1} KB";
            statusBlock.Text = $"{path}  ({sizeText})";
        }
        else
        {
            statusBlock.Text = $"{path}  (does not exist)";
        }
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;

        if (sender == GlobalEditor)
        {
            var dirty = GlobalEditor.Text != (_globalOriginal ?? "");
            GlobalSaveBtn.IsEnabled = dirty;
            GlobalRevertBtn.IsEnabled = dirty;
        }
        else if (sender == ProjectEditor)
        {
            var dirty = ProjectEditor.Text != (_projectOriginal ?? "");
            ProjectSaveBtn.IsEnabled = dirty;
            ProjectRevertBtn.IsEnabled = dirty;
        }
        else if (sender == MemoryEditor)
        {
            var dirty = MemoryEditor.Text != (_memoryOriginal ?? "");
            MemorySaveBtn.IsEnabled = dirty;
            MemoryRevertBtn.IsEnabled = dirty;
        }
    }

    private void TabsControl_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    // --- Global CLAUDE.md ---

    private void CreateGlobalDefault_Click(object sender, RoutedEventArgs e)
    {
        var content = InstructionsService.GetDefaultGlobalClaudeMd();
        _instructions.WriteFile(_globalPath, content);
        _globalOriginal = content;
        _suppressTextChanged = true;
        GlobalEditor.Text = content;
        GlobalEditor.Visibility = Visibility.Visible;
        GlobalPlaceholder.Visibility = Visibility.Collapsed;
        _suppressTextChanged = false;
        UpdateStatus(GlobalStatus, _globalPath);
    }

    private void CreateGlobalEmpty_Click(object sender, RoutedEventArgs e)
    {
        var content = "# CLAUDE.md (Global)\n";
        _instructions.WriteFile(_globalPath, content);
        _globalOriginal = content;
        _suppressTextChanged = true;
        GlobalEditor.Text = content;
        GlobalEditor.Visibility = Visibility.Visible;
        GlobalPlaceholder.Visibility = Visibility.Collapsed;
        _suppressTextChanged = false;
        UpdateStatus(GlobalStatus, _globalPath);
    }

    private void SaveGlobal_Click(object sender, RoutedEventArgs e)
    {
        if (_instructions.WriteFile(_globalPath, GlobalEditor.Text))
        {
            _globalOriginal = GlobalEditor.Text;
            GlobalSaveBtn.IsEnabled = false;
            GlobalRevertBtn.IsEnabled = false;
            UpdateStatus(GlobalStatus, _globalPath);
        }
        else
        {
            MessageBox.Show("Failed to save file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RevertGlobal_Click(object sender, RoutedEventArgs e)
    {
        _suppressTextChanged = true;
        GlobalEditor.Text = _globalOriginal ?? "";
        _suppressTextChanged = false;
        GlobalSaveBtn.IsEnabled = false;
        GlobalRevertBtn.IsEnabled = false;
    }

    // --- Project CLAUDE.md ---

    private void CreateProjectDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_projectPath is null) return;
        var content = InstructionsService.GetDefaultProjectClaudeMd();
        _instructions.WriteFile(_projectPath, content);
        _projectOriginal = content;
        _suppressTextChanged = true;
        ProjectEditor.Text = content;
        ProjectEditor.Visibility = Visibility.Visible;
        ProjectPlaceholder.Visibility = Visibility.Collapsed;
        _suppressTextChanged = false;
        UpdateStatus(ProjectStatus, _projectPath);
    }

    private void CreateProjectEmpty_Click(object sender, RoutedEventArgs e)
    {
        if (_projectPath is null) return;
        var content = "# CLAUDE.md\n";
        _instructions.WriteFile(_projectPath, content);
        _projectOriginal = content;
        _suppressTextChanged = true;
        ProjectEditor.Text = content;
        ProjectEditor.Visibility = Visibility.Visible;
        ProjectPlaceholder.Visibility = Visibility.Collapsed;
        _suppressTextChanged = false;
        UpdateStatus(ProjectStatus, _projectPath);
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_projectPath is null) return;
        if (_instructions.WriteFile(_projectPath, ProjectEditor.Text))
        {
            _projectOriginal = ProjectEditor.Text;
            ProjectSaveBtn.IsEnabled = false;
            ProjectRevertBtn.IsEnabled = false;
            UpdateStatus(ProjectStatus, _projectPath);
        }
        else
        {
            MessageBox.Show("Failed to save file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RevertProject_Click(object sender, RoutedEventArgs e)
    {
        _suppressTextChanged = true;
        ProjectEditor.Text = _projectOriginal ?? "";
        _suppressTextChanged = false;
        ProjectSaveBtn.IsEnabled = false;
        ProjectRevertBtn.IsEnabled = false;
    }

    // --- Memory ---

    private void SaveMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_memoryPath is null) return;
        if (_instructions.WriteFile(_memoryPath, MemoryEditor.Text))
        {
            _memoryOriginal = MemoryEditor.Text;
            MemorySaveBtn.IsEnabled = false;
            MemoryRevertBtn.IsEnabled = false;
            UpdateStatus(MemoryStatus, _memoryPath);
        }
        else
        {
            MessageBox.Show("Failed to save file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RevertMemory_Click(object sender, RoutedEventArgs e)
    {
        _suppressTextChanged = true;
        MemoryEditor.Text = _memoryOriginal ?? "";
        _suppressTextChanged = false;
        MemorySaveBtn.IsEnabled = false;
        MemoryRevertBtn.IsEnabled = false;
    }

    // --- Close ---

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (HasUnsavedChanges())
        {
            var result = MessageBox.Show(
                "You have unsaved changes. Close without saving?",
                "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }
        Close();
    }

    private bool HasUnsavedChanges()
    {
        if (GlobalSaveBtn.IsEnabled) return true;
        if (ProjectSaveBtn.IsEnabled) return true;
        if (MemorySaveBtn.IsEnabled) return true;
        return false;
    }

    /// <summary>
    /// Summary string for SettingsWindow: shows which instruction files exist.
    /// </summary>
    public static string BuildSummary(InstructionsService svc, string? workingDir)
    {
        var parts = new List<string>();

        var globalExists = svc.FileExists(svc.GetGlobalClaudeMdPath());
        parts.Add($"Global: {(globalExists ? "exists" : "none")}");

        if (!string.IsNullOrEmpty(workingDir))
        {
            var projectExists = svc.FileExists(svc.GetProjectClaudeMdPath(workingDir));
            parts.Add($"Project: {(projectExists ? "exists" : "none")}");

            var memoryExists = svc.FileExists(svc.GetMemoryPath(workingDir));
            parts.Add($"Memory: {(memoryExists ? "exists" : "none")}");
        }
        else
        {
            parts.Add("Project: no folder open");
        }

        return string.Join("  |  ", parts);
    }
}
