using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class ExplorerViewModel : ViewModelBase, IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<string> _rootPaths = [];
    private string _rootPath = "";
    private FileNode? _selectedNode;
    // FIX: debounce timer must be a field with lock to avoid race condition from concurrent FSW events
    private System.Timers.Timer? _debounceTimer;
    private readonly object _debounceLock = new();
    private readonly HashSet<string> _pendingRefreshDirs = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<FileNode> RootNodes { get; } = [];

    public FileNode? SelectedNode
    {
        get => _selectedNode;
        set => SetProperty(ref _selectedNode, value);
    }

    public string RootPath
    {
        get => _rootPath;
        private set => SetProperty(ref _rootPath, value);
    }

    /// <summary>Fired when user wants to open a file in the editor.</summary>
    public event Action<string>? OnOpenFile;

    // Commands
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand NewFileCommand { get; }
    public RelayCommand NewFolderCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand RevealInExplorerCommand { get; }

    public ExplorerViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        CollapseAllCommand = new RelayCommand(_ => CollapseAll());
        NewFileCommand = new RelayCommand(_ => CreateNewFile());
        NewFolderCommand = new RelayCommand(_ => CreateNewFolder());
        OpenFileCommand = new RelayCommand(p =>
        {
            var node = p as FileNode ?? SelectedNode;
            if (node is { IsDirectory: false, IsPlaceholder: false })
                OnOpenFile?.Invoke(node.FullPath);
        });
        DeleteCommand = new RelayCommand(_ => DeleteSelected());
        RenameCommand = new RelayCommand(_ => RenameSelected());
        CopyPathCommand = new RelayCommand(_ =>
        {
            if (SelectedNode != null)
                Clipboard.SetText(SelectedNode.FullPath);
        });
        RevealInExplorerCommand = new RelayCommand(_ =>
        {
            if (SelectedNode != null)
            {
                var path = SelectedNode.IsDirectory ? SelectedNode.FullPath : Path.GetDirectoryName(SelectedNode.FullPath);
                if (path != null)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedNode.FullPath}\"") { UseShellExecute = true });
            }
        });
    }

    public void SetRoot(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        SetRoots([path]);
    }

    public void SetRoots(IEnumerable<string> paths)
    {
        var validPaths = paths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).ToList();
        if (validPaths.Count == 0) return;

        // Check if roots are unchanged (set equality + same primary/first path)
        if (_rootPaths.Count == validPaths.Count &&
            string.Equals(_rootPaths.FirstOrDefault(), validPaths[0], StringComparison.OrdinalIgnoreCase) &&
            new HashSet<string>(_rootPaths, StringComparer.OrdinalIgnoreCase).SetEquals(validPaths))
            return;

        _rootPaths.Clear();
        _rootPaths.AddRange(validPaths);
        RootPath = validPaths[0];

        LoadTree();
        SetupWatchers();
    }

    private void LoadTree()
    {
        RunOnUI(() =>
        {
            RootNodes.Clear();
            foreach (var path in _rootPaths)
            {
                if (!Directory.Exists(path)) continue;
                var root = new FileNode(Path.GetFileName(path), path, true);
                root.LoadChildren();
                root.IsExpanded = true;
                RootNodes.Add(root);
            }
        });
    }

    private void SetupWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();

        foreach (var path in _rootPaths)
        {
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                // FIX: synchronized debounce to prevent race condition from concurrent threadpool callbacks.
                // Accumulates all changed directories and refreshes them together on timer expiry.
                void OnChanged(object s, FileSystemEventArgs e)
                {
                    lock (_debounceLock)
                    {
                        var dir = Path.GetDirectoryName(e.FullPath);
                        if (dir != null) _pendingRefreshDirs.Add(dir);

                        _debounceTimer?.Stop();
                        _debounceTimer?.Dispose();
                        _debounceTimer = new System.Timers.Timer(500) { AutoReset = false };
                        _debounceTimer.Elapsed += (_, _) =>
                        {
                            string[] dirs;
                            lock (_debounceLock)
                            {
                                _debounceTimer?.Dispose();
                                _debounceTimer = null;
                                dirs = [.. _pendingRefreshDirs];
                                _pendingRefreshDirs.Clear();
                            }
                            RunOnUI(() =>
                            {
                                foreach (var d in dirs) RefreshNode(d);
                            });
                        };
                        _debounceTimer.Start();
                    }
                }

                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += (s, e) => OnChanged(s, e);
                _watchers.Add(watcher);
            }
            catch
            {
                // FileSystemWatcher can fail on some paths — not critical
            }
        }
    }

    private void RefreshNode(string directoryPath)
    {
        if (RootNodes.Count == 0) return;

        foreach (var rootNode in RootNodes)
        {
            var node = FindNode(rootNode, directoryPath);
            if (node != null)
            {
                node.Invalidate();
                return;
            }
        }
    }

    private static FileNode? FindNode(FileNode root, string path)
    {
        if (string.Equals(root.FullPath, path, StringComparison.OrdinalIgnoreCase))
            return root;

        if (!root.IsDirectory) return null;

        foreach (var child in root.Children)
        {
            var found = FindNode(child, path);
            if (found != null) return found;
        }

        return null;
    }

    public void Refresh()
    {
        LoadTree();
    }

    private void CollapseAll()
    {
        foreach (var node in RootNodes)
            CollapseRecursive(node);
    }

    private static void CollapseRecursive(FileNode node)
    {
        if (!node.IsDirectory) return;
        foreach (var child in node.Children)
            CollapseRecursive(child);
        node.IsExpanded = false;
    }

    // FIX: validate that user-provided name doesn't escape the parent directory (path traversal)
    private static bool IsValidFileName(string name, string parentPath)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        var combined = Path.GetFullPath(Path.Combine(parentPath, name));
        return combined.StartsWith(Path.GetFullPath(parentPath) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private void CreateNewFile()
    {
        var parentPath = GetSelectedDirectory();
        if (parentPath == null) return;

        var name = PromptForName("New File", "Enter file name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        // FIX: prevent path traversal via names like "..\secret.txt"
        if (!IsValidFileName(name, parentPath))
        {
            MessageBox.Show("Invalid file name.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var filePath = Path.Combine(parentPath, name);
        if (File.Exists(filePath))
        {
            MessageBox.Show($"File '{name}' already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            File.WriteAllText(filePath, "");
            RefreshNode(parentPath);
            OnOpenFile?.Invoke(filePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CreateNewFolder()
    {
        var parentPath = GetSelectedDirectory();
        if (parentPath == null) return;

        var name = PromptForName("New Folder", "Enter folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        // FIX: prevent path traversal
        if (!IsValidFileName(name, parentPath))
        {
            MessageBox.Show("Invalid folder name.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folderPath = Path.Combine(parentPath, name);
        if (Directory.Exists(folderPath))
        {
            MessageBox.Show($"Folder '{name}' already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            RefreshNode(parentPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteSelected()
    {
        if (SelectedNode is null or { IsPlaceholder: true }) return;

        var msg = SelectedNode.IsDirectory
            ? $"Delete folder '{SelectedNode.Name}' and all its contents?"
            : $"Delete file '{SelectedNode.Name}'?";

        if (MessageBox.Show(msg, "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            if (SelectedNode.IsDirectory)
                Directory.Delete(SelectedNode.FullPath, true);
            else
                File.Delete(SelectedNode.FullPath);

            var parent = Path.GetDirectoryName(SelectedNode.FullPath);
            if (parent != null)
                RefreshNode(parent);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenameSelected()
    {
        if (SelectedNode is null or { IsPlaceholder: true }) return;

        var newName = PromptForName("Rename", "Enter new name:", SelectedNode.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == SelectedNode.Name) return;

        var parentDir = Path.GetDirectoryName(SelectedNode.FullPath);
        if (parentDir == null) return;

        // FIX: prevent path traversal via rename
        if (!IsValidFileName(newName, parentDir))
        {
            MessageBox.Show("Invalid name.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newPath = Path.Combine(parentDir, newName);

        try
        {
            if (SelectedNode.IsDirectory)
                Directory.Move(SelectedNode.FullPath, newPath);
            else
                File.Move(SelectedNode.FullPath, newPath);

            RefreshNode(parentDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to rename: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? GetSelectedDirectory()
    {
        if (SelectedNode == null)
            return RootPath;

        return SelectedNode.IsDirectory
            ? SelectedNode.FullPath
            : Path.GetDirectoryName(SelectedNode.FullPath);
    }

    private static string? PromptForName(string title, string prompt, string defaultValue = "")
    {
        // Simple input dialog using WPF Window
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#161b22")),
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow
        };

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

        var label = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e6edf3")),
            Margin = new Thickness(0, 0, 0, 8)
        };
        System.Windows.Controls.Grid.SetRow(label, 0);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0d1117")),
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e6edf3")),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#30363d")),
            Padding = new Thickness(8, 4, 8, 4),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code,Consolas,Courier New"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Controls.Grid.SetRow(textBox, 1);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        System.Windows.Controls.Grid.SetRow(buttonPanel, 2);

        string? result = null;

        var okButton = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 80,
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        okButton.Click += (_, _) => { result = textBox.Text; dialog.Close(); };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 80,
            Padding = new Thickness(0, 4, 0, 4),
            IsCancel = true
        };

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);

        grid.Children.Add(label);
        grid.Children.Add(textBox);
        grid.Children.Add(buttonPanel);

        dialog.Content = grid;
        dialog.ShowDialog();

        return result;
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }
}
