using System.Windows;
using ClaudeCodeWin.ContextSnapshot;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class App : Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        // Create all services upfront — they don't depend on Git/Claude being installed
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

        var settings = settingsService.Load();
        if (!string.IsNullOrEmpty(settings.WorkingDirectory))
            cliService.WorkingDirectory = settings.WorkingDirectory;

        var usageService = new UsageService();
        var contextSnapshotService = new ContextSnapshotService();

        var mainViewModel = new MainViewModel(cliService, notificationService, settingsService, settings, gitService, updateService, fileIndexService, chatHistoryService, projectRegistry, contextSnapshotService);
        var mainWindow = new MainWindow(mainViewModel, notificationService, settingsService, settings, fileIndexService, chatHistoryService, projectRegistry);

        // Show MainWindow immediately so the user sees the app
        mainWindow.Show();

        // Run dependency checks in the overlay
        var dependencyService = new ClaudeCodeDependencyService();
        var needsSetup = !dependencyService.IsGitInstalled()
                         || !(await dependencyService.CheckAsync()).IsInstalled;

        if (needsSetup)
        {
            var success = await RunDependencySetup(mainViewModel, mainWindow, dependencyService);
            if (!success)
            {
                Shutdown();
                return;
            }
        }

        // Apply Claude CLI path
        var depStatus = await dependencyService.CheckAsync();
        if (!string.IsNullOrEmpty(settings.ClaudeExePath))
            cliService.ClaudeExePath = settings.ClaudeExePath;
        else if (depStatus.ExePath is not null)
            cliService.ClaudeExePath = depStatus.ExePath;

        // Check authentication — requires interactive terminal, so use separate window
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

        // Wire up usage service → status bar
        usageService.OnUsageUpdated += () =>
        {
            if (!usageService.IsOnline)
            {
                mainViewModel.StatusText = "NO INTERNET";
                return;
            }

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

        // Setup menus
        scriptService.PopulateMenu(mainWindow, mainViewModel, gitService, settings, projectRegistry);
        taskRunnerService.PopulateMenu(mainWindow, mainViewModel);
    }

    private static async Task<bool> RunDependencySetup(
        MainViewModel vm, MainWindow window, ClaudeCodeDependencyService depService)
    {
        vm.ShowDependencyOverlay = true;

        void UpdateProgress(string message)
        {
            window.Dispatcher.InvokeAsync(() =>
            {
                vm.DependencyStatus = message;
                if (vm.DependencyLog.Length > 0)
                    vm.DependencyLog += "\n";
                vm.DependencyLog += message;
                window.ScrollDependencyLog();
            });
        }

        // Step 1: Git
        if (!depService.IsGitInstalled())
        {
            vm.DependencyTitle = "Installing Git for Windows...";
            vm.DependencyStatus = "Preparing...";
            vm.DependencyLog = "";

            var gitOk = await depService.InstallGitAsync(UpdateProgress);
            if (!gitOk)
            {
                vm.DependencyStatus = "Git installation failed.";
                vm.DependencyFailed = true;
                return false;
            }
        }

        // Step 2: Claude Code CLI
        var status = await depService.CheckAsync();
        if (!status.IsInstalled)
        {
            vm.DependencyTitle = "Installing Claude Code CLI...";
            vm.DependencyStatus = "Preparing...";
            vm.DependencyLog = "";

            var cliOk = await depService.InstallAsync(UpdateProgress);
            if (!cliOk)
            {
                vm.DependencyStatus = "Claude Code CLI installation failed.";
                vm.DependencyFailed = true;
                return false;
            }
        }

        vm.ShowDependencyOverlay = false;
        return true;
    }
}
