using System.IO;
using System.Windows;
using ClaudeCodeWin.ContextSnapshot;
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

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        // Global crash handler — writes to %LocalAppData%\ClaudeCodeWin\crash.log
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("UnhandledException", (Exception)args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            args.Handled = false; // let it crash, but at least we logged it
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteCrashLog("UnobservedTaskException", args.Exception);

        try
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

        var mainViewModel = new MainViewModel(cliService, notificationService, settingsService, settings, gitService, updateService, fileIndexService, chatHistoryService, projectRegistry, contextSnapshotService, usageService);
        var mainWindow = new MainWindow(mainViewModel, notificationService, settingsService, settings, fileIndexService, chatHistoryService, projectRegistry);

        // Show MainWindow immediately so the user sees the app
        mainWindow.Show();

        // Run dependency checks in the overlay
        var dependencyService = new ClaudeCodeDependencyService();
        var needsGit = !dependencyService.IsGitInstalled();
        var needsCli = !(await dependencyService.CheckAsync()).IsInstalled;
        var needsGh = !dependencyService.IsGhInstalled();

        // Git installer requires admin. If Git is missing and we're not admin, request elevation.
        if (needsGit && !ClaudeCodeDependencyService.IsAdministrator())
        {
            var result = MessageBox.Show(
                "Git for Windows needs to be installed, which requires administrator privileges.\n\n" +
                "The application will restart with elevated permissions.\nClick OK to continue.",
                "Administrator Required", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (result == MessageBoxResult.OK && ClaudeCodeDependencyService.RequestElevation())
            {
                Shutdown();
                return;
            }
            else
            {
                MessageBox.Show(
                    "Git for Windows is required. Please install it manually from:\nhttps://git-scm.com/downloads/win\n\nThen restart the application.",
                    "Git Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
        }

        if (needsGit || needsCli || needsGh)
        {
            var success = await RunDependencySetup(mainViewModel, mainWindow, dependencyService, needsGit, needsCli, needsGh);
            if (!success)
            {
                // Don't Shutdown() — let the overlay stay visible with the error and Close button.
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

        // Welcome dialog: unified startup flow (new chat, switch project, continue chat, general chat)
        var history = chatHistoryService.ListAll();
        var projectCount = projectRegistry.GetMostRecentProjects(2).Count;
        if (history.Count > 0 || projectCount >= 2)
        {
            var welcomeDialog = new WelcomeDialog(chatHistoryService, projectRegistry, settings.WorkingDirectory)
            {
                Owner = mainWindow
            };

            if (welcomeDialog.ShowDialog() == true)
            {
                switch (welcomeDialog.ChosenAction)
                {
                    case Models.WelcomeDialogResult.NewChat:
                        mainViewModel.NewSessionCommand.Execute(null);
                        break;
                    case Models.WelcomeDialogResult.SwitchProject:
                        mainViewModel.SetWorkingDirectory(welcomeDialog.SelectedProjectPath!);
                        break;
                    case Models.WelcomeDialogResult.ContinueChat:
                        mainViewModel.LoadChatFromHistory(welcomeDialog.SelectedChatEntry!);
                        break;
                    case Models.WelcomeDialogResult.GeneralChat:
                        mainViewModel.StartGeneralChat();
                        break;
                }
            }
        }

        // Deduplication check: compare project CLAUDE.md with global CLAUDE.md
        CheckInstructionDeduplication(settings.WorkingDirectory);

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
            var sessionPct = $"{usageService.SessionUtilization:F0}%";
            var sessionCountdown = usageService.GetSessionCountdown();
            var sessionExtra = string.IsNullOrEmpty(sessionCountdown)
                ? " | " : $" ({sessionCountdown}) | ";
            var weekPct = $"{usageService.WeeklyUtilization:F0}%";

            mainViewModel.SessionPctText = sessionPct;
            mainViewModel.SessionExtraText = sessionExtra;
            mainViewModel.WeekPctText = weekPct;
            mainViewModel.UsageText = $"Session: {sessionPct}{sessionExtra}Week: {weekPct}";
        };
        usageService.Start();

        // Setup menus
        scriptService.PopulateMenu(mainWindow, mainViewModel, gitService, settings, projectRegistry);
        taskRunnerService.PopulateMenu(mainWindow, mainViewModel);
        mainViewModel.SetTaskRunner(taskRunnerService, mainWindow);

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

    private static async Task<bool> RunDependencySetup(
        MainViewModel vm, MainWindow window, ClaudeCodeDependencyService depService,
        bool needGit, bool needCli, bool needGh)
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

        var totalSteps = (needGit ? 1 : 0) + (needCli ? 1 : 0) + (needGh ? 1 : 0);
        var currentStep = 0;

        // Step: Git for Windows (full installer, requires admin)
        if (needGit)
        {
            currentStep++;
            vm.DependencyStep = $"Step {currentStep} of {totalSteps} — First-time setup";
            vm.DependencyTitle = "Installing Git for Windows";
            vm.DependencySubtitle = "Git is required for version control and is used by Claude Code internally. The installer will run silently in the background.";
            vm.DependencyStatus = "Connecting to GitHub...";
            vm.DependencyLog = "";

            var gitOk = await depService.InstallGitAsync(UpdateProgress);
            if (!gitOk)
            {
                vm.DependencyStatus = "Git installation failed. Check your internet connection and try again.";
                vm.DependencyFailed = true;
                return false;
            }
        }

        // Step: Claude Code CLI
        if (needCli)
        {
            currentStep++;
            vm.DependencyStep = $"Step {currentStep} of {totalSteps} — First-time setup";
            vm.DependencyTitle = "Installing Claude Code CLI";
            vm.DependencySubtitle = "Claude Code CLI is the core engine that powers this application.\nDownloading ~222MB — this will take a few minutes. Please wait.";
            vm.DependencyStatus = "Fetching latest version...";
            vm.DependencyLog = "";

            var cliOk = await depService.InstallAsync(UpdateProgress);
            if (!cliOk)
            {
                vm.DependencyStatus = "Claude Code CLI installation failed. Check your internet connection and try again.";
                vm.DependencyFailed = true;
                return false;
            }
        }

        // Step: GitHub CLI (non-critical — failure doesn't block the app)
        if (needGh)
        {
            currentStep++;
            vm.DependencyStep = $"Step {currentStep} of {totalSteps} — First-time setup";
            vm.DependencyTitle = "Installing GitHub CLI";
            vm.DependencySubtitle = "GitHub CLI enables pull requests, issue management, and repository operations directly from the app.";
            vm.DependencyStatus = "Connecting to GitHub...";
            vm.DependencyLog = "";

            var ghOk = await depService.InstallGhAsync(UpdateProgress);
            if (!ghOk)
            {
                UpdateProgress("GitHub CLI installation failed (non-critical). You can install it later.");
                // Don't return false — gh is optional, Git + Claude CLI are sufficient
            }
        }

        vm.ShowDependencyOverlay = false;
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
