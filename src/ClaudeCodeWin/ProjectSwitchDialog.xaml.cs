using System.Windows;
using System.Windows.Input;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class ProjectSwitchDialog : Window
{
    public string? SelectedProjectPath { get; private set; }

    public ProjectSwitchDialog(ProjectRegistryService projectRegistry, string? currentWorkingDirectory)
    {
        InitializeComponent();

        var projects = LoadFilteredProjects(projectRegistry, currentWorkingDirectory, 50);
        ProjectList.ItemsSource = projects;
    }

    private static List<ProjectInfo> LoadFilteredProjects(
        ProjectRegistryService registry, string? currentDir, int maxCount)
    {
        var projects = registry.GetMostRecentProjects(maxCount);

        // Filter out nested sub-projects (keep topmost roots)
        var sorted = projects.OrderBy(p => p.Path.Length).ToList();
        var roots = new List<ProjectInfo>();
        foreach (var p in sorted)
        {
            var normalizedPath = p.Path.TrimEnd('\\', '/') + "\\";
            var isNested = roots.Any(r =>
                normalizedPath.StartsWith(r.Path.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase));
            if (!isNested)
                roots.Add(p);
        }

        var filtered = roots.OrderByDescending(p => p.LastOpened).ToList();

        // Mark the current project
        var current = currentDir?.TrimEnd('\\', '/');
        foreach (var p in filtered)
            p.IsCurrent = !string.IsNullOrEmpty(current)
                && string.Equals(p.Path.TrimEnd('\\', '/'), current, StringComparison.OrdinalIgnoreCase);

        return filtered;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ProjectList.SelectedItem is ProjectInfo project)
        {
            AcceptProject(project.Path);
            e.Handled = true;
        }
    }

    private void ProjectList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SwitchBtn.IsEnabled = ProjectList.SelectedItem is ProjectInfo;
    }

    private void ProjectList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectInfo project)
            AcceptProject(project.Path);
    }

    private void SwitchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectInfo project)
            AcceptProject(project.Path);
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Project Folder"
        };

        if (dialog.ShowDialog() == true)
            AcceptProject(dialog.FolderName);
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void AcceptProject(string path)
    {
        SelectedProjectPath = path;
        DialogResult = true;
        Close();
    }
}
