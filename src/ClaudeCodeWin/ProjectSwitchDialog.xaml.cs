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

        ProjectList.ItemsSource = projectRegistry.GetFilteredProjects(50, currentWorkingDirectory);
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
