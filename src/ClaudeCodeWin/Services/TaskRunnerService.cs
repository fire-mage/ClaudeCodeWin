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

        var howTo = new MenuItem
        {
            Header = "How to add a task",
            IsEnabled = false,
            ToolTip = "Open Tasks Folder, edit tasks.json, then Reload Tasks.\n\n" +
                      "Example tasks.json:\n" +
                      "[\n" +
                      "  {\n" +
                      "    \"name\": \"npm install\",\n" +
                      "    \"command\": \"npm install\",\n" +
                      "    \"confirmBeforeRun\": false\n" +
                      "  },\n" +
                      "  {\n" +
                      "    \"name\": \"Run Tests\",\n" +
                      "    \"command\": \"dotnet test\",\n" +
                      "    \"confirmBeforeRun\": true\n" +
                      "  }\n" +
                      "]"
        };
        tasksMenu.Items.Add(howTo);
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
