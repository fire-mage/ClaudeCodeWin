using System.Windows;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class App : Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        // Check that Claude Code CLI is installed
        var dependencyService = new ClaudeCodeDependencyService();
        var depStatus = await dependencyService.CheckAsync();

        if (!depStatus.IsInstalled)
        {
            var installWindow = new DependencyInstallWindow(dependencyService);
            installWindow.ShowDialog();

            if (!installWindow.Success)
            {
                MessageBox.Show(
                    "Claude Code CLI is required but could not be installed.\nThe application will now exit.",
                    "Dependency Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            // Re-check after install
            depStatus = await dependencyService.CheckAsync();
        }

        // Manual DI — no NuGet containers
        var cliService = new ClaudeCliService();
        var notificationService = new NotificationService();
        var scriptService = new ScriptService();
        var settingsService = new SettingsService();
        var taskRunnerService = new TaskRunnerService();
        var gitService = new GitService();
        var updateService = new UpdateService();
        var fileIndexService = new FileIndexService();
        var chatHistoryService = new ChatHistoryService();

        // Apply settings
        var settings = settingsService.Load();
        if (!string.IsNullOrEmpty(settings.ClaudeExePath))
            cliService.ClaudeExePath = settings.ClaudeExePath;
        else if (depStatus.ExePath is not null)
            cliService.ClaudeExePath = depStatus.ExePath;
        if (!string.IsNullOrEmpty(settings.WorkingDirectory))
            cliService.WorkingDirectory = settings.WorkingDirectory;

        var mainViewModel = new MainViewModel(cliService, notificationService, settingsService, settings, gitService, updateService, fileIndexService, chatHistoryService);
        var mainWindow = new MainWindow(mainViewModel, notificationService, settingsService, settings, fileIndexService, chatHistoryService);

        // Setup scripts menu
        scriptService.PopulateMenu(mainWindow, mainViewModel, gitService);

        // Setup tasks menu
        taskRunnerService.PopulateMenu(mainWindow, mainViewModel);

        mainWindow.Show();
    }
}
