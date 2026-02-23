using System.IO;
using System.Windows;
using ClaudeCodeWin.ContextSnapshot;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin", "crash.log");

    private static void WriteCrashLog(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\n{ex}\n\n";
            File.AppendAllText(CrashLogPath, entry);
        }
        catch { /* last resort — can't log the log failure */ }
    }

    private void SetupCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("UnhandledException", (Exception)args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            args.Handled = false;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteCrashLog("UnobservedTaskException", args.Exception);
    }

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        SetupCrashHandlers();

        try
        {
            // Create all services
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

            // Create and show main window
            var mainViewModel = new MainViewModel(cliService, notificationService, settingsService, settings, gitService, updateService, fileIndexService, chatHistoryService, projectRegistry, contextSnapshotService, usageService);
            var mainWindow = new MainWindow(mainViewModel, notificationService, settingsService, settings, fileIndexService, chatHistoryService, projectRegistry);
            mainWindow.Show();

            // Dependency checks and installation
            if (!await EnsureDependencies(mainViewModel, mainWindow))
                return;

            // Apply CLI path
            var depService = new ClaudeCodeDependencyService();
            var depStatus = await depService.CheckAsync();
            if (!string.IsNullOrEmpty(settings.ClaudeExePath))
                cliService.ClaudeExePath = settings.ClaudeExePath;
            else if (depStatus.ExePath is not null)
                cliService.ClaudeExePath = depStatus.ExePath;

            // Authentication
            if (!EnsureAuthentication(depService, depStatus))
                return;

            // Instruction deduplication
            CheckInstructionDeduplication(settings.WorkingDirectory);

            // Usage service wiring
            ConfigureUsageService(mainViewModel, usageService);
            usageService.Start();

            // Menus
            scriptService.PopulateMenu(mainWindow, mainViewModel, gitService, settings, projectRegistry);
            taskRunnerService.PopulateMenu(mainWindow, mainViewModel);
            mainViewModel.SetTaskRunner(taskRunnerService, mainWindow);

            // Update check first, then welcome flow (to avoid overlapping popups)
            var hasUpdate = await mainViewModel.Update.CheckOnStartupAsync();
            if (hasUpdate)
            {
                // Update overlay is showing — defer welcome flow until user dismisses it
                mainViewModel.Update.OnUpdateDismissed += () =>
                    RunWelcomeFlow(mainViewModel, mainWindow, chatHistoryService, projectRegistry, settings);
            }
            else
            {
                RunWelcomeFlow(mainViewModel, mainWindow, chatHistoryService, projectRegistry, settings);
            }

            // Start periodic background checks (every 4 hours)
            mainViewModel.Update.StartPeriodicCheck();
        }
        catch (Exception ex)
        {
            WriteCrashLog("Application_Startup", ex);
            MessageBox.Show(
                $"Startup failed:\n{ex.Message}\n\nSee {CrashLogPath} for details.",
                "ClaudeCodeWin Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task<bool> EnsureDependencies(MainViewModel vm, MainWindow window)
    {
        var depService = new ClaudeCodeDependencyService();
        var needsGit = !depService.IsGitInstalled();
        var needsCli = !(await depService.CheckAsync()).IsInstalled;
        var needsGh = !depService.IsGhInstalled();

        // Git requires admin
        if (needsGit && !ClaudeCodeDependencyService.IsAdministrator())
        {
            var result = MessageBox.Show(
                "Git for Windows needs to be installed, which requires administrator privileges.\n\n" +
                "The application will restart with elevated permissions.\nClick OK to continue.",
                "Administrator Required", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (result == MessageBoxResult.OK && ClaudeCodeDependencyService.RequestElevation())
            {
                Shutdown();
                return false;
            }

            MessageBox.Show(
                "Git for Windows is required. Please install it manually from:\nhttps://git-scm.com/downloads/win\n\nThen restart the application.",
                "Git Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return false;
        }

        if (needsGit || needsCli || needsGh)
        {
            var success = await RunDependencySetup(vm, window, depService, needsGit, needsCli, needsGh);
            if (!success)
                return false;
        }

        return true;
    }

    private bool EnsureAuthentication(ClaudeCodeDependencyService depService, DependencyStatus depStatus)
    {
        if (depService.IsAuthenticated())
            return true;

        var claudeExe = depStatus.ExePath ?? "claude";
        var loginWindow = new LoginPromptWindow(depService, claudeExe);
        loginWindow.ShowDialog();

        if (loginWindow.Success)
            return true;

        MessageBox.Show(
            "Anthropic account login is required to use Claude Code.\nThe application will now exit.",
            "Login Required", MessageBoxButton.OK, MessageBoxImage.Warning);
        Shutdown();
        return false;
    }

    private static void RunWelcomeFlow(
        MainViewModel vm, MainWindow window,
        ChatHistoryService chatHistory, ProjectRegistryService projectRegistry,
        AppSettings settings)
    {
        var history = chatHistory.ListAll();
        var projectCount = projectRegistry.GetMostRecentProjects(2).Count;
        if (history.Count == 0 && projectCount < 2)
            return;

        var welcomeDialog = new WelcomeDialog(chatHistory, projectRegistry, settings.WorkingDirectory)
        {
            Owner = window
        };

        if (welcomeDialog.ShowDialog() != true)
            return;

        switch (welcomeDialog.ChosenAction)
        {
            case WelcomeDialogResult.NewChat:
                vm.NewSessionCommand.Execute(null);
                break;
            case WelcomeDialogResult.SwitchProject:
                vm.SetWorkingDirectory(welcomeDialog.SelectedProjectPath!);
                break;
            case WelcomeDialogResult.ContinueChat:
                vm.LoadChatFromHistory(welcomeDialog.SelectedChatEntry!);
                break;
            case WelcomeDialogResult.GeneralChat:
                vm.StartGeneralChat();
                break;
        }
    }

    private static void ConfigureUsageService(MainViewModel vm, UsageService usageService)
    {
        usageService.OnUsageUpdated += () =>
        {
            if (!usageService.IsOnline)
            {
                vm.StatusText = "NO INTERNET";
                return;
            }

            if (vm.StatusText == "NO INTERNET")
                vm.StatusText = vm.IsProcessing ? "Processing..." : "Ready";

            if (!usageService.IsLoaded) return;
            var sessionPct = $"{usageService.SessionUtilization:F0}%";
            var sessionCountdown = usageService.GetSessionCountdown();
            var sessionExtra = string.IsNullOrEmpty(sessionCountdown)
                ? " | " : $" ({sessionCountdown}) | ";
            var weekPct = $"{usageService.WeeklyUtilization:F0}%";

            vm.SessionPctText = sessionPct;
            vm.SessionExtraText = sessionExtra;
            vm.WeekPctText = weekPct;
            vm.UsageText = $"Session: {sessionPct}{sessionExtra}Week: {weekPct}";
        };
    }

    private static async Task<bool> RunDependencySetup(
        MainViewModel vm, MainWindow window, ClaudeCodeDependencyService depService,
        bool needGit, bool needCli, bool needGh)
    {
        vm.DependencySetup.ShowDependencyOverlay = true;

        void UpdateProgress(string message)
        {
            window.Dispatcher.InvokeAsync(() =>
            {
                vm.DependencySetup.DependencyStatus = message;
                if (vm.DependencySetup.DependencyLog.Length > 0)
                    vm.DependencySetup.DependencyLog += "\n";
                vm.DependencySetup.DependencyLog += message;
                window.ScrollDependencyLog();
            });
        }

        var totalSteps = (needGit ? 1 : 0) + (needCli ? 1 : 0) + (needGh ? 1 : 0);
        var currentStep = 0;

        if (needGit)
        {
            currentStep++;
            vm.DependencySetup.DependencyStep = $"Step {currentStep} of {totalSteps} — First-time setup";
            vm.DependencySetup.DependencyTitle = "Installing Git for Windows";
            vm.DependencySetup.DependencySubtitle = "Git is required for version control and is used by Claude Code internally. The installer will run silently in the background.";
            vm.DependencySetup.DependencyStatus = "Connecting to GitHub...";
            vm.DependencySetup.DependencyLog = "";

            var gitOk = await depService.InstallGitAsync(UpdateProgress);
            if (!gitOk)
            {
                vm.DependencySetup.DependencyStatus = "Git installation failed. Check your internet connection and try again.";
                vm.DependencySetup.DependencyFailed = true;
                return false;
            }
        }

        if (needCli)
        {
            currentStep++;
            vm.DependencySetup.DependencyStep = $"Step {currentStep} of {totalSteps} — First-time setup";
            vm.DependencySetup.DependencyTitle = "Installing Claude Code CLI";
            vm.DependencySetup.DependencySubtitle = "Claude Code CLI is the core engine that powers this application.\nDownloading ~222MB — this will take a few minutes. Please wait.";
            vm.DependencySetup.DependencyStatus = "Fetching latest version...";
            vm.DependencySetup.DependencyLog = "";

            var cliOk = await depService.InstallAsync(UpdateProgress);
            if (!cliOk)
            {
                vm.DependencySetup.DependencyStatus = "Claude Code CLI installation failed. Check your internet connection and try again.";
                vm.DependencySetup.DependencyFailed = true;
                return false;
            }
        }

        if (needGh)
        {
            currentStep++;
            vm.DependencySetup.DependencyStep = $"Step {currentStep} of {totalSteps} — First-time setup";
            vm.DependencySetup.DependencyTitle = "Installing GitHub CLI";
            vm.DependencySetup.DependencySubtitle = "GitHub CLI enables pull requests, issue management, and repository operations directly from the app.";
            vm.DependencySetup.DependencyStatus = "Connecting to GitHub...";
            vm.DependencySetup.DependencyLog = "";

            var ghOk = await depService.InstallGhAsync(UpdateProgress);
            if (!ghOk)
            {
                UpdateProgress("GitHub CLI installation failed (non-critical). You can install it later.");
            }
        }

        vm.DependencySetup.ShowDependencyOverlay = false;
        return true;
    }

    private static void CheckInstructionDeduplication(string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir)) return;

        try
        {
            var svc = new InstructionsService();
            var globalContent = svc.ReadFile(svc.GetGlobalClaudeMdPath());
            var projectContent = svc.ReadFile(svc.GetProjectClaudeMdPath(workingDir));

            if (globalContent is null || projectContent is null) return;

            var duplicates = svc.FindDuplicateBlocks(globalContent, projectContent);
            if (duplicates.Count == 0) return;

            var headers = string.Join("\n", duplicates.Select(h => $"  - {h.TrimStart('#').Trim()}"));
            var result = MessageBox.Show(
                $"Your project CLAUDE.md contains {duplicates.Count} section(s) that duplicate your global instructions:\n\n{headers}\n\nRemove these duplicates from the project file?",
                "Duplicate Instructions", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            var cleaned = svc.RemoveDuplicateBlocks(projectContent, duplicates);
            var projectPath = svc.GetProjectClaudeMdPath(workingDir);
            if (cleaned is null)
                File.Delete(projectPath);
            else
                svc.WriteFile(projectPath, cleaned);
        }
        catch
        {
            // Non-critical — don't crash the app over dedup
        }
    }
}
