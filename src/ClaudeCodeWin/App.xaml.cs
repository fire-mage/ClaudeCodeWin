using System.Windows;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Manual DI — no NuGet containers
        var cliService = new ClaudeCliService();
        var notificationService = new NotificationService();
        var scriptService = new ScriptService();
        var settingsService = new SettingsService();
        var taskRunnerService = new TaskRunnerService();
        var gitService = new GitService();
        var updateService = new UpdateService();
        var fileIndexService = new FileIndexService();

        // Apply settings
        var settings = settingsService.Load();
        if (!string.IsNullOrEmpty(settings.ClaudeExePath))
            cliService.ClaudeExePath = settings.ClaudeExePath;
        if (!string.IsNullOrEmpty(settings.WorkingDirectory))
            cliService.WorkingDirectory = settings.WorkingDirectory;

        var mainViewModel = new MainViewModel(cliService, notificationService, settingsService, settings, gitService, updateService, fileIndexService);
        var mainWindow = new MainWindow(mainViewModel, notificationService, settingsService, settings, fileIndexService);

        // Setup scripts menu
        scriptService.PopulateMenu(mainWindow, mainViewModel, gitService);

        // Setup tasks menu
        taskRunnerService.PopulateMenu(mainWindow, mainViewModel);

        mainWindow.Show();
    }
}
