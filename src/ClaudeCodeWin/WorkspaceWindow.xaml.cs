using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class WorkspaceWindow : Window
{
    private readonly WorkspaceService _workspaceService;
    private readonly ProjectRegistryService _projectRegistry;
    private readonly Workspace? _existingWorkspace;

    public ObservableCollection<WorkspaceProjectItem> Projects { get; } = [];

    /// <summary>The resulting workspace after Save (null if cancelled or deleted).</summary>
    public Workspace? ResultWorkspace { get; private set; }

    /// <summary>True if user deleted the workspace.</summary>
    public bool WasDeleted { get; private set; }

    public WorkspaceWindow(
        WorkspaceService workspaceService,
        ProjectRegistryService projectRegistry,
        Workspace? existingWorkspace = null,
        string? initialProjectPath = null)
    {
        _workspaceService = workspaceService;
        _projectRegistry = projectRegistry;
        _existingWorkspace = existingWorkspace;

        InitializeComponent();

        ProjectsList.ItemsSource = Projects;

        if (existingWorkspace != null)
        {
            // Edit mode
            Title = "Edit Workspace";
            NameTextBox.Text = existingWorkspace.Name;
            DeleteButton.Visibility = Visibility.Visible;

            foreach (var proj in existingWorkspace.Projects)
            {
                Projects.Add(new WorkspaceProjectItem
                {
                    Path = proj.Path,
                    Name = Path.GetFileName(proj.Path),
                    Role = proj.Role,
                    IsPrimary = string.Equals(proj.Path, existingWorkspace.PrimaryProjectPath,
                        StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        else if (!string.IsNullOrEmpty(initialProjectPath))
        {
            // Create mode with initial project
            NameTextBox.Text = Path.GetFileName(initialProjectPath) + " Workspace";
            Projects.Add(new WorkspaceProjectItem
            {
                Path = initialProjectPath,
                Name = Path.GetFileName(initialProjectPath),
                IsPrimary = true
            });
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Add Project Folder"
        };

        if (dialog.ShowDialog() != true) return;

        var path = dialog.FolderName;
        if (Projects.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("This project is already in the workspace.", "Duplicate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var item = new WorkspaceProjectItem
        {
            Path = path,
            Name = Path.GetFileName(path),
            IsPrimary = Projects.Count == 0
        };

        // Try to get role from registry
        var info = _projectRegistry.GetProject(path);
        if (!string.IsNullOrEmpty(info?.TechStack))
            item.Role = info.TechStack;

        Projects.Add(item);
    }

    private void AddFromRegistry_Click(object sender, RoutedEventArgs e)
    {
        var projects = _projectRegistry.GetFilteredProjects(50);
        if (projects.Count == 0)
        {
            MessageBox.Show("No projects in registry.", "Empty Registry",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Simple list picker dialog
        var picker = new Window
        {
            Title = "Select Projects",
            Width = 450, Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (System.Windows.Media.Brush)FindResource("BackgroundBrush"),
            ResizeMode = ResizeMode.CanResizeWithGrip
        };

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

        var listBox = new System.Windows.Controls.ListBox
        {
            Background = (System.Windows.Media.Brush)FindResource("InputBackgroundBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            SelectionMode = System.Windows.Controls.SelectionMode.Multiple
        };

        foreach (var proj in projects)
        {
            // Skip projects already in workspace
            if (Projects.Any(p => string.Equals(p.Path, proj.Path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var item = new System.Windows.Controls.ListBoxItem
            {
                Content = $"{proj.Name}  —  {proj.Path}",
                Tag = proj,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                FontSize = 12,
                Padding = new Thickness(4, 4, 4, 4)
            };
            listBox.Items.Add(item);
        }

        System.Windows.Controls.Grid.SetRow(listBox, 0);
        grid.Children.Add(listBox);

        var addBtn = new System.Windows.Controls.Button
        {
            Content = "Add Selected",
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Style = (Style)FindResource("PrimaryButton")
        };
        System.Windows.Controls.Grid.SetRow(addBtn, 1);
        grid.Children.Add(addBtn);

        addBtn.Click += (_, _) =>
        {
            foreach (System.Windows.Controls.ListBoxItem selected in listBox.SelectedItems)
            {
                if (selected.Tag is ProjectInfo info)
                {
                    Projects.Add(new WorkspaceProjectItem
                    {
                        Path = info.Path,
                        Name = info.Name,
                        Role = info.TechStack,
                        IsPrimary = Projects.Count == 0
                    });
                }
            }
            picker.Close();
        };

        picker.Content = grid;
        picker.ShowDialog();
    }

    private void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceProjectItem item })
        {
            var wasPrimary = item.IsPrimary;
            Projects.Remove(item);
            if (wasPrimary && Projects.Count > 0)
                Projects[0].IsPrimary = true;
        }
    }

    private void PrimaryRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceProjectItem selected })
        {
            foreach (var proj in Projects)
                proj.IsPrimary = proj == selected;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Please enter a workspace name.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Projects.Count == 0)
        {
            MessageBox.Show("Please add at least one project.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var primaryItem = Projects.FirstOrDefault(p => p.IsPrimary) ?? Projects[0];

        if (_existingWorkspace != null)
        {
            // Update existing
            _existingWorkspace.Name = name;
            _existingWorkspace.Projects = Projects.Select(p => new WorkspaceProject
            {
                Path = Path.GetFullPath(p.Path),
                Role = p.Role
            }).ToList();
            _existingWorkspace.PrimaryProjectPath = Path.GetFullPath(primaryItem.Path);
            _workspaceService.UpdateWorkspace(_existingWorkspace);
            ResultWorkspace = _existingWorkspace;
        }
        else
        {
            // Create new — set roles by path matching, only save again if roles exist
            var workspace = _workspaceService.CreateWorkspace(
                name,
                Projects.Select(p => p.Path),
                primaryItem.Path);

            var hasRoles = false;
            foreach (var uiProj in Projects)
            {
                if (string.IsNullOrEmpty(uiProj.Role)) continue;
                var wsProj = workspace.Projects.FirstOrDefault(p =>
                    string.Equals(Path.GetFullPath(p.Path), Path.GetFullPath(uiProj.Path),
                        StringComparison.OrdinalIgnoreCase));
                if (wsProj != null)
                {
                    wsProj.Role = uiProj.Role;
                    hasRoles = true;
                }
            }
            if (hasRoles)
                _workspaceService.UpdateWorkspace(workspace);
            ResultWorkspace = workspace;
        }

        DialogResult = true;
        Close();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_existingWorkspace == null) return;

        var result = MessageBox.Show(
            $"Delete workspace \"{_existingWorkspace.Name}\"?\nThis will not delete any project files.",
            "Delete Workspace",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _workspaceService.DeleteWorkspace(_existingWorkspace.Id);
        WasDeleted = true;
        DialogResult = true;
        Close();
    }
}

/// <summary>UI model for workspace project items in the list.</summary>
public class WorkspaceProjectItem : INotifyPropertyChanged
{
    private string _path = "";
    private string _name = "";
    private string? _role;
    private bool _isPrimary;

    public string Path
    {
        get => _path;
        set { _path = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path))); }
    }

    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
    }

    public string? Role
    {
        get => _role;
        set { _role = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Role))); }
    }

    public bool IsPrimary
    {
        get => _isPrimary;
        set { _isPrimary = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPrimary))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
