using System.IO;
using System.Windows;
using ClaudeCodeWin.ContextSnapshot;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class App : Application
{
    public App()
    {
        // Apply dark title bar to all windows automatically
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w)
                    Win32Interop.EnableDarkTitleBar(w);
            }));
    }

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
            // Check if a previous update was rolled back
            var rolledBackVersion = CheckUpdateRollback();

            // Create shared services
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

            // Persist rolled-back version to blacklist and apply to update service
            if (rolledBackVersion != null && !settings.FailedUpdateVersions.Contains(rolledBackVersion))
            {
                settings.FailedUpdateVersions.Add(rolledBackVersion);
                settingsService.Save(settings);
            }
            updateService.BlacklistedVersions = new HashSet<string>(settings.FailedUpdateVersions);

            var cliUpdateService = new CliUpdateService();

            // CLI rollback check
            var cliRolledBack = CliUpdateService.CheckCliRollbackMarker();
            if (cliRolledBack != null && !settings.FailedCliVersions.Contains(cliRolledBack))
            {
                settings.FailedCliVersions.Add(cliRolledBack);
                settingsService.Save(settings);
                MessageBox.Show(
                    $"Claude Code CLI update to v{cliRolledBack} failed — previous version restored.\n\n" +
                    "This version has been blacklisted and will not be offered again.",
                    "CLI Update Rolled Back", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            cliUpdateService.BlacklistedVersions = new HashSet<string>(settings.FailedCliVersions);
            var usageService = new UsageService();
            var contextSnapshotService = new ContextSnapshotService();
            var backlogService = new BacklogService();
            backlogService.Load();

            // Create TabHostViewModel (manages multiple tabs)
            var tabHost = new TabHostViewModel(
                notificationService, settingsService, settings, gitService,
                updateService, fileIndexService, chatHistoryService,
                projectRegistry, contextSnapshotService, usageService,
                backlogService);

            // Determine which project paths to restore (new multi-tab or legacy single)
            var tabPaths = (settings.OpenTabPaths is { Count: > 0 }
                ? settings.OpenTabPaths
                : string.IsNullOrEmpty(settings.WorkingDirectory)
                    ? new List<string> { "" }
                    : new List<string> { settings.WorkingDirectory })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Create initial tab (always created before mainWindow)
            var initialTab = tabHost.CreateTab();
            initialTab.ShowWelcome = true;
            if (!string.IsNullOrEmpty(tabPaths[0]))
                initialTab.SetWorkingDirectoryOnStartup(tabPaths[0]);

            var mainWindow = new MainWindow(tabHost, notificationService, settingsService, settings, fileIndexService, chatHistoryService, projectRegistry);
            mainWindow.Show();

            // Signal to update.cmd that the new version started successfully
            if (e.Args.Contains("--after-update"))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(UpdateService.SuccessMarkerPath)!);
                    File.WriteAllText(UpdateService.SuccessMarkerPath, "ok");
                }
                catch { /* non-critical */ }
            }

            // Dependency checks and installation
            if (!await EnsureDependencies(initialTab, mainWindow))
                return;

            // Apply CLI path (shared for all tabs)
            var depService = new ClaudeCodeDependencyService();
            var depStatus = await depService.CheckAsync();
            string claudeExePath = "claude";
            if (!string.IsNullOrEmpty(settings.ClaudeExePath))
                claudeExePath = settings.ClaudeExePath;
            else if (depStatus.ExePath is not null)
                claudeExePath = depStatus.ExePath;
            tabHost.ClaudeExePath = claudeExePath;

            // CLI update service: set exe path and get current version
            cliUpdateService.ExePath = claudeExePath;
            _ = cliUpdateService.GetCurrentVersionAsync();

            // Authentication
            if (!EnsureAuthentication(depService, depStatus))
                return;

            // Instruction deduplication
            CheckInstructionDeduplication(settings.WorkingDirectory);

            // Usage service wiring (global — same for all tabs)
            ConfigureUsageService(tabHost, usageService);
            usageService.Start();

            // Menus (resolve active tab at click time)
            scriptService.PopulateMenu(mainWindow, () => tabHost.ActiveTab!, gitService, settings, projectRegistry, backlogService);
            taskRunnerService.PopulateMenu(mainWindow, () => tabHost.ActiveTab!);

            // Set task runner for all current and future tabs via CollectionChanged.
            // This ensures every new tab (startup restore, "+" button, Ctrl+T) gets it immediately.
            tabHost.Tabs.CollectionChanged += (_, args) =>
            {
                if (args.NewItems is null) return;
                foreach (MainViewModel tab in args.NewItems)
                    tab.SetTaskRunner(taskRunnerService, mainWindow);
            };

            // Set task runner for tabs already created before this subscription (initial tab)
            foreach (var tab in tabHost.Tabs)
                tab.SetTaskRunner(taskRunnerService, mainWindow);

            // Restore additional saved tabs (after hooks are wired)
            foreach (var path in tabPaths.Skip(1))
            {
                var tab = tabHost.CreateTab();
                if (!string.IsNullOrEmpty(path))
                    tab.SetWorkingDirectoryOnStartup(path);
            }

            // Restore the previously active tab
            if (!string.IsNullOrEmpty(settings.ActiveTabPath))
            {
                var target = tabHost.Tabs.FirstOrDefault(t =>
                    string.Equals(t.WorkingDirectory, settings.ActiveTabPath,
                        StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                    tabHost.ActiveTab = target;
            }

            // Knowledge Base
            var knowledgeBaseService = new KnowledgeBaseService();
            mainWindow.SetKnowledgeBaseService(knowledgeBaseService);

            // Update check first, then welcome flow (to avoid overlapping popups)
            var hasUpdate = await tabHost.Update.CheckOnStartupAsync();
            if (hasUpdate)
            {
                // Update overlay is showing — defer welcome flow until user dismisses it
                tabHost.Update.OnUpdateDismissed += () =>
                    RunWelcomeFlow(initialTab, mainWindow, chatHistoryService, projectRegistry, settings);
            }
            else
            {
                RunWelcomeFlow(initialTab, mainWindow, chatHistoryService, projectRegistry, settings);
            }

            // Start periodic background checks (every 4 hours)
            tabHost.Update.StartPeriodicCheck();

            // CLI update checks
            tabHost.Update.InitCliUpdate(cliUpdateService);
            tabHost.Update.StartCliPeriodicCheck();
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
        if (history.Count == 0)
            return;

        window.ShowWelcomeScreen();
    }

    private static void ConfigureUsageService(TabHostViewModel tabHost, UsageService usageService)
    {
        usageService.OnUsageUpdated += () =>
        {
            var activeTab = tabHost.ActiveTab;

            if (!usageService.IsOnline)
            {
                if (activeTab is not null)
                    activeTab.StatusText = "NO INTERNET";
                return;
            }

            if (activeTab is not null && activeTab.StatusText == "NO INTERNET")
                activeTab.StatusText = activeTab.IsProcessing ? "Processing..." : "Ready";

            if (!usageService.IsLoaded) return;
            var sessionPct = $"{usageService.SessionUtilization:F0}%";
            var sessionCountdown = usageService.GetSessionCountdown();
            var sessionExtra = string.IsNullOrEmpty(sessionCountdown)
                ? " | " : $" ({sessionCountdown}) | ";
            var weekPct = $"{usageService.WeeklyUtilization:F0}%";

            // Usage is global (API-level), update TabHost-level properties
            tabHost.SessionPctText = sessionPct;
            tabHost.SessionExtraText = sessionExtra;
            tabHost.WeekPctText = weekPct;
            tabHost.UsageText = $"Session: {sessionPct}{sessionExtra}Week: {weekPct}";
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

    /// <summary>
    /// Check if a previous update was rolled back. Returns the failed version string, or null.
    /// </summary>
    private static string? CheckUpdateRollback()
    {
        try
        {
            var rollbackPath = UpdateService.RollbackMarkerPath;
            if (!File.Exists(rollbackPath)) return null;

            var failedVersion = File.ReadAllText(rollbackPath).Trim();
            File.Delete(rollbackPath);

            MessageBox.Show(
                $"Update to v{failedVersion} failed — the previous version has been restored.\n\n" +
                "This version has been blacklisted and will not be offered again.",
                "Update Rolled Back", MessageBoxButton.OK, MessageBoxImage.Warning);

            return failedVersion;
        }
        catch { return null; }
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
