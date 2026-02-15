using System.Windows;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class App : Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var dependencyService = new ClaudeCodeDependencyService();

        // Step 1: Check Git for Windows (required by Claude Code CLI)
        if (!dependencyService.IsGitInstalled())
        {
            MessageBox.Show(
                "Git for Windows is required for Claude Code CLI.\n\nPlease install Git from https://git-scm.com/downloads/win and restart the application.",
                "Git Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        // Step 2: Check that Claude Code CLI is installed
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

        // Step 3: Check authentication — if not logged in, launch interactive login
        if (!dependencyService.IsAuthenticated())
        {
            var claudeExe = depStatus.ExePath ?? "claude";
            var loginWindow = new LoginPromptWindow(dependencyService, claudeExe);
            loginWindow.ShowDialog();

            if (!loginWindow.Success)
            {
                MessageBox.Show(
                    "Anthropic account login is required to use Claude Code.\nThe application will now exit.",
                    "Login Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
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
        var projectRegistry = new ProjectRegistryService();
        projectRegistry.Load();

        // Apply settings
        var settings = settingsService.Load();
        if (!string.IsNullOrEmpty(settings.ClaudeExePath))
            cliService.ClaudeExePath = settings.ClaudeExePath;
        else if (depStatus.ExePath is not null)
            cliService.ClaudeExePath = depStatus.ExePath;
        if (!string.IsNullOrEmpty(settings.WorkingDirectory))
            cliService.WorkingDirectory = settings.WorkingDirectory;

        var usageService = new UsageService();

        var mainViewModel = new MainViewModel(cliService, notificationService, settingsService, settings, gitService, updateService, fileIndexService, chatHistoryService, projectRegistry);
        var mainWindow = new MainWindow(mainViewModel, notificationService, settingsService, settings, fileIndexService, chatHistoryService);

        // Wire up usage service → status bar
        usageService.OnUsageUpdated += () =>
        {
            if (!usageService.IsOnline)
            {
                mainViewModel.StatusText = "NO INTERNET";
                return;
            }

            // Restore status when back online
            if (mainViewModel.StatusText == "NO INTERNET")
                mainViewModel.StatusText = mainViewModel.IsProcessing ? "Processing..." : "Ready";

            if (!usageService.IsLoaded) return;
            var session = $"Session: {usageService.SessionUtilization:F0}%";
            var sessionCountdown = usageService.GetSessionCountdown();
            if (!string.IsNullOrEmpty(sessionCountdown))
                session += $" ({sessionCountdown})";

            var weekly = $"Week: {usageService.WeeklyUtilization:F0}%";
            mainViewModel.UsageText = $"{session} | {weekly}";
        };
        usageService.Start();

        // Setup scripts menu
        scriptService.PopulateMenu(mainWindow, mainViewModel, gitService);

        // Setup tasks menu
        taskRunnerService.PopulateMenu(mainWindow, mainViewModel);

        mainWindow.Show();
    }
}
