using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.ViewModels;
using ClaudeCodeWin.Views;

namespace ClaudeCodeWin.Services;

public class TaskRunnerService
{
    private static readonly string TasksDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin");

    private static readonly string TasksPath = Path.Combine(TasksDir, "tasks.json");

    // Stored references for SetTaskRunner wiring
    private Func<MainViewModel>? _getActiveTab;
    private MainWindow? _mainWindow;
    private System.Windows.RoutedEventHandler? _submenuRefreshHandler;
    private bool _isPopulating;
    private DateTime _lastTasksWriteTime;

    private static string ScriptHelpText { get; } = string.Join("\n", new[]
    {
        "Scripts are stored in tasks.json as a JSON array.",
        "",
        "Each script has these fields:",
        "  \u2022 name \u2014 display name in the menu",
        "  \u2022 command \u2014 shell command to run",
        "  \u2022 project \u2014 (optional) project name for grouping in submenu",
        "  \u2022 hotKey \u2014 (optional) keyboard shortcut hint",
        "  \u2022 confirmBeforeRun \u2014 (optional) ask before running",
        "  \u2022 category \u2014 (optional) set to \"deploy\" for Deploy Scripts menu",
        "",
        "Example:",
        "[",
        "  {",
        "    \"name\": \"Deploy API\",",
        "    \"command\": \"powershell ./deploy-api.ps1\",",
        "    \"project\": \"MyProject\",",
        "    \"category\": \"deploy\"",
        "  }",
        "]",
        "",
        "Scripts with a 'project' field appear in a submenu.",
        "Scripts with category \"deploy\" appear in Deploy Scripts menu.",
        "",
        "Use Edit Scripts... to modify, or ask Claude to add a script for you."
    });

    public List<TaskDefinition> LoadTasks()
    {
        if (!File.Exists(TasksPath))
        {
            var defaults = GetDefaultTasks();
            SaveTasks(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(TasksPath);
            return JsonSerializer.Deserialize<List<TaskDefinition>>(json, JsonDefaults.Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveTasks(List<TaskDefinition> tasks)
    {
        Directory.CreateDirectory(TasksDir);
        var json = JsonSerializer.Serialize(tasks, JsonDefaults.Options);
        File.WriteAllText(TasksPath, json);
    }

    public List<TaskDefinition> GetTasksForProject(string? workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory))
            return [];

        var normalized = workingDirectory.NormalizePath();
        var projectName = Path.GetFileName(normalized);
        var tasks = LoadTasks();

        return tasks.Where(t =>
        {
            // Match by project name (folder name)
            if (!string.IsNullOrEmpty(t.Project) &&
                string.Equals(t.Project, projectName, StringComparison.OrdinalIgnoreCase))
                return true;

            // Match by workingDirectory: task belongs to this folder or a parent
            if (!string.IsNullOrEmpty(t.WorkingDirectory))
            {
                if (t.WorkingDirectory.PathEquals(normalized) ||
                    normalized.IsSubPathOf(t.WorkingDirectory))
                    return true;
            }

            return false;
        }).ToList();
    }

    public void PopulateMenu(MainWindow mainWindow, Func<MainViewModel> getActiveTab)
    {
        if (_isPopulating) return;

        // Skip repopulation if tasks.json hasn't changed since last load
        var currentWriteTime = File.Exists(TasksPath)
            ? File.GetLastWriteTimeUtc(TasksPath) : DateTime.MinValue;
        if (_getActiveTab is not null && currentWriteTime == _lastTasksWriteTime)
            return;

        _isPopulating = true;
        try
        {
            _lastTasksWriteTime = currentWriteTime;
            _getActiveTab = getActiveTab;
            _mainWindow = mainWindow;

            var tasks = LoadTasks();

            // Split tasks by category
            var deployTasks = tasks.Where(t =>
                string.Equals(t.Category, "deploy", StringComparison.OrdinalIgnoreCase)).ToList();
            var regularTasks = tasks.Where(t =>
                !string.Equals(t.Category, "deploy", StringComparison.OrdinalIgnoreCase)).ToList();

            PopulateMenuInternal(mainWindow.TasksMenu, regularTasks, mainWindow, getActiveTab);

            // Refresh menus when opened (picks up tasks.json changes made by Claude or manual edits)
            if (_submenuRefreshHandler is not null)
            {
                mainWindow.TasksMenu.SubmenuOpened -= _submenuRefreshHandler;
                mainWindow.DeployScriptsMenu.SubmenuOpened -= _submenuRefreshHandler;
            }
            _submenuRefreshHandler = (_, _) => PopulateMenu(mainWindow, getActiveTab);
            mainWindow.TasksMenu.SubmenuOpened += _submenuRefreshHandler;
            mainWindow.DeployScriptsMenu.SubmenuOpened += _submenuRefreshHandler;

            if (deployTasks.Count > 0)
            {
                PopulateMenuInternal(mainWindow.DeployScriptsMenu, deployTasks, mainWindow, getActiveTab);
            }
            else
            {
                mainWindow.DeployScriptsMenu.Items.Clear();
                var emptyHint = new MenuItem
                {
                    Header = "No deploy scripts yet",
                    IsEnabled = false,
                    IsHitTestVisible = false,
                    Focusable = false,
                    FontStyle = System.Windows.FontStyles.Italic
                };
                mainWindow.DeployScriptsMenu.Items.Add(emptyHint);

                var createHint = new MenuItem
                {
                    Header = "Use Claude > Create Deploy Scripts...",
                    ToolTip = "Go to Claude menu to let Claude create deploy scripts for your project.",
                    FontStyle = System.Windows.FontStyles.Italic,
                    IsEnabled = false,
                    IsHitTestVisible = false,
                    Focusable = false
                };
                mainWindow.DeployScriptsMenu.Items.Add(createHint);

                mainWindow.DeployScriptsMenu.Items.Add(new Separator());

                var editTasks = new MenuItem
                {
                    Header = "Edit Scripts...",
                    ToolTip = "Open the built-in editor to modify tasks.json."
                };
                editTasks.Click += (_, _) => EditTasksJson(mainWindow, getActiveTab);
                mainWindow.DeployScriptsMenu.Items.Add(editTasks);

                var howTo = new MenuItem
                {
                    Header = "How to add a script",
                    ToolTip = "Show instructions for adding custom scripts."
                };
                howTo.Click += (_, _) =>
                {
                    MessageBox.Show(ScriptHelpText, "How to Add a Script",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                };
                mainWindow.DeployScriptsMenu.Items.Add(howTo);
            }
        }
        finally { _isPopulating = false; }
    }

    private void PopulateMenuInternal(MenuItem targetMenu, List<TaskDefinition> tasks,
        MainWindow mainWindow, Func<MainViewModel> getActiveTab)
    {
        targetMenu.Items.Clear();

        // Group tasks: those with Project go into submenus, others stay at top level
        var ungrouped = tasks.Where(t => string.IsNullOrEmpty(t.Project)).ToList();
        var grouped = tasks
            .Where(t => !string.IsNullOrEmpty(t.Project))
            .GroupBy(t => t.Project!)
            .OrderBy(g => g.Key);

        if (grouped.Any())
        {
            var projectsHeader = new MenuItem
            {
                Header = "PROJECTS",
                IsEnabled = false,
                IsHitTestVisible = false,
                Focusable = false,
                FontSize = 10,
                Padding = new Thickness(0, 2, 0, 2)
            };
            targetMenu.Items.Add(projectsHeader);
        }

        foreach (var group in grouped)
        {
            var projectMenu = new MenuItem
            {
                Header = group.Key,
                ToolTip = $"Scripts for project: {group.Key}"
            };

            foreach (var task in group)
            {
                var taskDef = task;
                var menuItem = new MenuItem
                {
                    Header = task.Name,
                    InputGestureText = task.HotKey ?? "",
                    ToolTip = $"Runs shell command:\n{taskDef.Command}"
                };
                menuItem.Click += (_, _) => RunTask(taskDef, getActiveTab(), mainWindow);
                projectMenu.Items.Add(menuItem);
            }

            targetMenu.Items.Add(projectMenu);
        }

        if (grouped.Any() && ungrouped.Count > 0)
            targetMenu.Items.Add(new Separator());

        foreach (var task in ungrouped)
        {
            var taskDef = task;
            var menuItem = new MenuItem
            {
                Header = task.Name,
                InputGestureText = task.HotKey ?? "",
                ToolTip = $"Runs shell command:\n{taskDef.Command}"
            };

            menuItem.Click += (_, _) => RunTask(taskDef, getActiveTab(), mainWindow);
            targetMenu.Items.Add(menuItem);
        }

        if (tasks.Count > 0)
            targetMenu.Items.Add(new Separator());

        var editTasks = new MenuItem
        {
            Header = "Edit Scripts...",
            ToolTip = "Open the built-in editor to modify tasks.json."
        };
        editTasks.Click += (_, _) => EditTasksJson(mainWindow, getActiveTab);
        targetMenu.Items.Add(editTasks);

        var howTo = new MenuItem
        {
            Header = "How to add a script",
            ToolTip = "Show instructions for adding custom scripts."
        };
        howTo.Click += (_, _) =>
        {
            MessageBox.Show(ScriptHelpText, "How to Add a Script",
                MessageBoxButton.OK, MessageBoxImage.Information);
        };
        targetMenu.Items.Add(howTo);
    }

    private void EditTasksJson(MainWindow mainWindow, Func<MainViewModel> getActiveTab)
    {
        Directory.CreateDirectory(TasksDir);

        if (!File.Exists(TasksPath))
            SaveTasks(GetDefaultTasks());

        var json = File.ReadAllText(TasksPath);

        var editorWindow = new Window
        {
            Title = "Edit Scripts \u2014 tasks.json",
            Width = 600,
            Height = 500,
            MinWidth = 400,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = mainWindow,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush")
        };

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = json,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code,Consolas,Courier New"),
            FontSize = 13,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceBrush"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush"),
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            SelectionBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SelectionBrush"),
            CaretBrush = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush")
        };
        Grid.SetRow(textBox, 0);
        grid.Children.Add(textBox);

        var errorText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("ErrorBrush"),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(errorText, 1);
        grid.Children.Add(errorText);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(20, 6, 20, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.FindResource("CancelButton")
        };
        cancelButton.Click += (_, _) => editorWindow.Close();

        var saveButton = new Button
        {
            Content = "Save",
            Padding = new Thickness(20, 6, 20, 6),
            Style = (Style)Application.Current.FindResource("PrimaryButton")
        };
        saveButton.Click += (_, _) =>
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<TaskDefinition>>(textBox.Text, JsonDefaults.Options);
                if (parsed is null)
                {
                    errorText.Text = "JSON parsed as null. Expected a JSON array.";
                    errorText.Visibility = Visibility.Visible;
                    return;
                }

                File.WriteAllText(TasksPath, textBox.Text);
                PopulateMenu(mainWindow, getActiveTab);
                editorWindow.Close();
            }
            catch (JsonException ex)
            {
                errorText.Text = $"JSON Error: {ex.Message}";
                errorText.Visibility = Visibility.Visible;
            }
        };

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(saveButton);
        Grid.SetRow(buttonPanel, 2);
        grid.Children.Add(buttonPanel);

        editorWindow.Content = grid;
        editorWindow.ShowDialog();
    }

    public static void RunTaskPublic(TaskDefinition task, MainViewModel viewModel, Window owner)
        => RunTask(task, viewModel, owner);

    private static void RunTask(TaskDefinition task, MainViewModel viewModel, Window owner)
    {
        if (task.ConfirmBeforeRun)
        {
            var result = MessageBox.Show(
                $"Run script \"{task.Name}\"?\n\nCommand: {task.Command}",
                "Confirm Script",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;
        }

        var outputWindow = new TaskOutputWindow { Owner = owner };
        outputWindow.OnTaskCompleted += (taskName, output) =>
        {
            viewModel.AddTaskOutput(taskName, output);
        };
        outputWindow.Show();

        _ = outputWindow.RunTaskAsync(task, viewModel.WorkingDirectory);
    }

    private static List<TaskDefinition> GetDefaultTasks()
    {
        return
        [
            new TaskDefinition
            {
                Name = "Hello Script",
                Command = "echo Scripts are working! You can add your own scripts such as deploy commands, build scripts, or git workflows. Go to My Scripts > Edit Scripts... or ask Claude to add a script for you.",
                ConfirmBeforeRun = false
            }
        ];
    }
}
