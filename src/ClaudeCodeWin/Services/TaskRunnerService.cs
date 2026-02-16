using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
            return JsonSerializer.Deserialize<List<TaskDefinition>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveTasks(List<TaskDefinition> tasks)
    {
        Directory.CreateDirectory(TasksDir);
        var json = JsonSerializer.Serialize(tasks, JsonOptions);
        File.WriteAllText(TasksPath, json);
    }

    public void PopulateMenu(MainWindow mainWindow, MainViewModel viewModel)
    {
        var tasks = LoadTasks();
        var tasksMenu = mainWindow.TasksMenu;
        tasksMenu.Items.Clear();

        foreach (var task in tasks)
        {
            var taskDef = task;
            var menuItem = new MenuItem
            {
                Header = task.Name,
                InputGestureText = task.HotKey ?? "",
                ToolTip = $"Runs shell command:\n{taskDef.Command}"
            };

            menuItem.Click += (_, _) => RunTask(taskDef, viewModel, mainWindow);
            tasksMenu.Items.Add(menuItem);
        }

        if (tasks.Count > 0)
            tasksMenu.Items.Add(new Separator());

        var openFolder = new MenuItem
        {
            Header = "Open Tasks Folder...",
            ToolTip = $"Open the folder with tasks.json to add or edit tasks.\n{TasksDir}"
        };
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(TasksDir);
            System.Diagnostics.Process.Start("explorer.exe", TasksDir);
        };
        tasksMenu.Items.Add(openFolder);

        var reload = new MenuItem
        {
            Header = "Reload Tasks",
            ToolTip = "Re-read tasks.json and refresh this menu after manual edits."
        };
        reload.Click += (_, _) => PopulateMenu(mainWindow, viewModel);
        tasksMenu.Items.Add(reload);

        tasksMenu.Items.Add(new Separator());

        var editTasks = new MenuItem
        {
            Header = "Edit Tasks...",
            ToolTip = "Open the built-in editor to modify tasks.json."
        };
        editTasks.Click += (_, _) => EditTasksJson(mainWindow, viewModel);
        tasksMenu.Items.Add(editTasks);

        var howTo = new MenuItem
        {
            Header = "How to add a task",
            ToolTip = "Show instructions for adding custom tasks."
        };
        howTo.Click += (_, _) =>
        {
            MessageBox.Show(
                "Tasks are stored in tasks.json as a JSON array.\n\n" +
                "Each task has these fields:\n" +
                "  \u2022 name \u2014 display name in the menu\n" +
                "  \u2022 command \u2014 shell command to run\n" +
                "  \u2022 hotKey \u2014 (optional) keyboard shortcut hint\n" +
                "  \u2022 confirmBeforeRun \u2014 (optional) ask before running\n\n" +
                "Example:\n" +
                "[\n" +
                "  {\n" +
                "    \"name\": \"npm install\",\n" +
                "    \"command\": \"npm install\",\n" +
                "    \"confirmBeforeRun\": false\n" +
                "  }\n" +
                "]\n\n" +
                "Use Edit Tasks... to modify, or open the folder manually.",
                "How to Add a Task",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        };
        tasksMenu.Items.Add(howTo);
    }

    private void EditTasksJson(MainWindow mainWindow, MainViewModel viewModel)
    {
        Directory.CreateDirectory(TasksDir);

        if (!File.Exists(TasksPath))
            SaveTasks(GetDefaultTasks());

        var json = File.ReadAllText(TasksPath);

        var editorWindow = new Window
        {
            Title = "Edit Tasks \u2014 tasks.json",
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
                var parsed = JsonSerializer.Deserialize<List<TaskDefinition>>(textBox.Text, JsonOptions);
                if (parsed is null)
                {
                    errorText.Text = "JSON parsed as null. Expected a JSON array.";
                    errorText.Visibility = Visibility.Visible;
                    return;
                }

                File.WriteAllText(TasksPath, textBox.Text);
                PopulateMenu(mainWindow, viewModel);
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

    private static void RunTask(TaskDefinition task, MainViewModel viewModel, Window owner)
    {
        if (task.ConfirmBeforeRun)
        {
            var result = MessageBox.Show(
                $"Run task \"{task.Name}\"?\n\nCommand: {task.Command}",
                "Confirm Task",
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
                Name = "Echo Test",
                Command = "echo Hello from ClaudeCodeWin!",
                ConfirmBeforeRun = false
            },
            new TaskDefinition
            {
                Name = "Git Status",
                Command = "git status",
                ConfirmBeforeRun = false
            },
            new TaskDefinition
            {
                Name = "Git Log (last 10)",
                Command = "git log --oneline -10",
                ConfirmBeforeRun = false
            }
        ];
    }
}
