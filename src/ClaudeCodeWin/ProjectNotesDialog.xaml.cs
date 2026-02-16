using System.IO;
using System.Windows;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class ProjectNotesDialog : Window
{
    private readonly ProjectRegistryService _projectRegistry;
    private readonly string _projectPath;

    public ProjectNotesDialog(ProjectRegistryService projectRegistry, string projectPath)
    {
        InitializeComponent();
        _projectRegistry = projectRegistry;
        _projectPath = projectPath;

        ProjectNameText.Text = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        NotesBox.Text = projectRegistry.GetNotes(projectPath) ?? "";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _projectRegistry.UpdateNotes(_projectPath, NotesBox.Text);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
