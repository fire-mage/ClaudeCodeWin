using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin.Services;

public partial class ScriptService
{
    private static readonly string ScriptsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin");

    private static readonly string ScriptsPath = Path.Combine(ScriptsDir, "scripts.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [GeneratedRegex(@"\{file:(.+?)\}")]
    private static partial Regex FileVariableRegex();

    public List<ScriptDefinition> LoadScripts()
    {
        if (!File.Exists(ScriptsPath))
        {
            var defaults = GetDefaultScripts();
            SaveScripts(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(ScriptsPath);
            return JsonSerializer.Deserialize<List<ScriptDefinition>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveScripts(List<ScriptDefinition> scripts)
    {
        Directory.CreateDirectory(ScriptsDir);
        var json = JsonSerializer.Serialize(scripts, JsonOptions);
        File.WriteAllText(ScriptsPath, json);
    }

    public void PopulateMenu(MainWindow mainWindow, MainViewModel viewModel, GitService gitService)
    {
        var scripts = LoadScripts();
        var scriptsMenu = mainWindow.ScriptsMenu;
        scriptsMenu.Items.Clear();

        foreach (var script in scripts)
        {
            var prompt = script.Prompt;
            var menuItem = new MenuItem
            {
                Header = script.Name,
                InputGestureText = script.HotKey ?? "",
                ToolTip = $"Sends prompt to Claude:\n\"{(prompt.Length > 120 ? prompt[..120] + "..." : prompt)}\""
            };

            menuItem.Click += (_, _) =>
            {
                var resolved = ResolveVariables(prompt, viewModel.WorkingDirectory, gitService);
                viewModel.InputText = resolved;

                if (viewModel.SendCommand.CanExecute(null))
                    viewModel.SendCommand.Execute(null);
            };

            scriptsMenu.Items.Add(menuItem);
        }

        if (scripts.Count > 0)
            scriptsMenu.Items.Add(new Separator());

        var openFolder = new MenuItem
        {
            Header = "Open Scripts Folder...",
            ToolTip = $"Open the folder with scripts.json to add or edit scripts.\n{ScriptsDir}"
        };
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(ScriptsDir);
            System.Diagnostics.Process.Start("explorer.exe", ScriptsDir);
        };
        scriptsMenu.Items.Add(openFolder);

        var reload = new MenuItem
        {
            Header = "Reload Scripts",
            ToolTip = "Re-read scripts.json and refresh this menu after manual edits."
        };
        reload.Click += (_, _) => PopulateMenu(mainWindow, viewModel, gitService);
        scriptsMenu.Items.Add(reload);

        // Register hotkeys
        RegisterHotkeys(mainWindow, scripts, viewModel, gitService);
    }

    private string ResolveVariables(string prompt, string? workingDir, GitService gitService)
    {
        var result = prompt;

        // {clipboard}
        result = result.Replace("{clipboard}", GetClipboardText());

        // {git-status}
        if (result.Contains("{git-status}"))
        {
            var output = gitService.RunGit("status", workingDir) ?? "";
            result = result.Replace("{git-status}", output.Trim());
        }

        // {git-diff}
        if (result.Contains("{git-diff}"))
        {
            var output = gitService.RunGit("diff --cached", workingDir) ?? "";
            result = result.Replace("{git-diff}", output.Trim());
        }

        // {snapshot}
        if (result.Contains("{snapshot}") && !string.IsNullOrEmpty(workingDir))
        {
            var snapshotPath = Path.Combine(workingDir, "CONTEXT_SNAPSHOT.md");
            var content = File.Exists(snapshotPath) ? File.ReadAllText(snapshotPath) : "";
            result = result.Replace("{snapshot}", content.Trim());
        }

        // {file:path}
        result = FileVariableRegex().Replace(result, match =>
        {
            var relativePath = match.Groups[1].Value;
            if (string.IsNullOrEmpty(workingDir))
                return "";
            var fullPath = Path.Combine(workingDir, relativePath);
            try
            {
                return File.Exists(fullPath) ? File.ReadAllText(fullPath).Trim() : "";
            }
            catch
            {
                return "";
            }
        });

        return result;
    }

    private void RegisterHotkeys(Window window, List<ScriptDefinition> scripts,
        MainViewModel viewModel, GitService gitService)
    {
        foreach (var script in scripts.Where(s => !string.IsNullOrEmpty(s.HotKey)))
        {
            try
            {
                var parts = script.HotKey!.Split('+');
                var modifiers = ModifierKeys.None;
                var key = Key.None;

                foreach (var part in parts)
                {
                    var p = part.Trim();
                    if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
                        modifiers |= ModifierKeys.Control;
                    else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                        modifiers |= ModifierKeys.Alt;
                    else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                        modifiers |= ModifierKeys.Shift;
                    else if (Enum.TryParse<Key>(p, true, out var k))
                        key = k;
                }

                if (key == Key.None) continue;

                var prompt = script.Prompt;
                var binding = new KeyBinding(
                    new Infrastructure.RelayCommand(() =>
                    {
                        var resolved = ResolveVariables(prompt, viewModel.WorkingDirectory, gitService);
                        viewModel.InputText = resolved;

                        if (viewModel.SendCommand.CanExecute(null))
                            viewModel.SendCommand.Execute(null);
                    }),
                    key,
                    modifiers);

                window.InputBindings.Add(binding);
            }
            catch
            {
                // Invalid hotkey format — skip
            }
        }
    }

    private static string GetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch
        {
            return "";
        }
    }

    private static List<ScriptDefinition> GetDefaultScripts()
    {
        return
        [
            new ScriptDefinition
            {
                Name = "Explain Code",
                Prompt = "Explain the following code:\n\n{clipboard}",
                HotKey = "Ctrl+Shift+E"
            },
            new ScriptDefinition
            {
                Name = "Review Code",
                Prompt = "Review the following code for bugs and improvements:\n\n{clipboard}",
                HotKey = "Ctrl+Shift+R"
            },
            new ScriptDefinition
            {
                Name = "Review Staged Changes",
                Prompt = "Review my staged changes for bugs and improvements:\n\n{git-diff}",
                HotKey = "Ctrl+Shift+G"
            },
            new ScriptDefinition
            {
                Name = "Fix Error",
                Prompt = "Fix the following error:\n\n{clipboard}",
                HotKey = null
            }
        ];
    }
}
