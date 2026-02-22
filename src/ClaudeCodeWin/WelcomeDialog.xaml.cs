using System.IO;
using System.Windows;
using System.Windows.Input;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class WelcomeDialog : Window
{
    private readonly ChatHistoryService _historyService;
    private readonly ProjectRegistryService _projectRegistry;
    private readonly string? _currentWorkingDirectory;

    public WelcomeDialogResult ChosenAction { get; private set; }
    public string? SelectedProjectPath { get; private set; }
    public ChatHistoryEntry? SelectedChatEntry { get; private set; }

    public WelcomeDialog(
        ChatHistoryService historyService,
        ProjectRegistryService projectRegistry,
        string? currentWorkingDirectory)
    {
        InitializeComponent();
        _historyService = historyService;
        _projectRegistry = projectRegistry;
        _currentWorkingDirectory = currentWorkingDirectory;

        // Section 1: Update subtitle with current project name
        var projectName = ExtractProjectName(currentWorkingDirectory);
        NewChatSubtitle.Text = string.IsNullOrEmpty(projectName)
            ? "Start a fresh conversation"
            : $"Start a fresh conversation in {projectName}";

        // Section 2: Load projects
        var projects = LoadFilteredProjects(50);
        if (projects.Count < 2)
        {
            SwitchProjectSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            SwitchProjectHeader.Text = $"Switch Project ({projects.Count})";
            ProjectList.ItemsSource = projects;
        }

        // Section 3: Load recent chats
        var summaries = historyService.ListAll();
        var recentChats = summaries
            .Take(5)
            .Select(s => new SessionDisplayItem
            {
                Id = s.Id,
                Title = s.Title,
                ProjectPath = s.ProjectPath,
                ProjectName = ExtractProjectName(s.ProjectPath),
                UpdatedAt = s.UpdatedAt,
                MessageCount = s.MessageCount
            })
            .ToList();

        if (recentChats.Count == 0)
            ContinueChatSection.Visibility = Visibility.Collapsed;
        else
            RecentChatsList.ItemsSource = recentChats;
    }

    private List<ProjectInfo> LoadFilteredProjects(int maxCount)
    {
        var projects = _projectRegistry.GetMostRecentProjects(maxCount);

        // Filter out nested sub-projects (keep topmost roots)
        var sorted = projects.OrderBy(p => p.Path.Length).ToList();
        var roots = new List<ProjectInfo>();
        foreach (var p in sorted)
        {
            var normalizedPath = p.Path.TrimEnd('\\', '/') + "\\";
            var isNested = roots.Any(r =>
                normalizedPath.StartsWith(r.Path.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase));
            if (!isNested)
                roots.Add(p);
        }

        var filtered = roots.OrderByDescending(p => p.LastOpened).ToList();

        // Mark the current project
        var currentDir = _currentWorkingDirectory?.TrimEnd('\\', '/');
        foreach (var p in filtered)
            p.IsCurrent = !string.IsNullOrEmpty(currentDir)
                && string.Equals(p.Path.TrimEnd('\\', '/'), currentDir, StringComparison.OrdinalIgnoreCase);

        return filtered;
    }

    private static string ExtractProjectName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "No project";
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
               ?? path;
    }

    // --- Keyboard ---

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ProjectList.SelectedItem is null && RecentChatsList.SelectedItem is null)
        {
            // Enter with nothing selected = New Chat
            ChosenAction = WelcomeDialogResult.NewChat;
            DialogResult = true;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    // --- Section 1: New Chat ---

    private void NewChat_Click(object sender, MouseButtonEventArgs e)
    {
        ChosenAction = WelcomeDialogResult.NewChat;
        DialogResult = true;
        Close();
    }

    // --- Section 2: Switch Project ---

    private void ProjectList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        StartWithProjectBtn.IsEnabled = ProjectList.SelectedItem is ProjectInfo;
        // Deselect chat when project is selected
        if (ProjectList.SelectedItem is not null)
            RecentChatsList.SelectedItem = null;
    }

    private void ProjectList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectInfo project)
        {
            ChosenAction = WelcomeDialogResult.SwitchProject;
            SelectedProjectPath = project.Path;
            DialogResult = true;
            Close();
        }
    }

    private void StartWithProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectInfo project)
        {
            ChosenAction = WelcomeDialogResult.SwitchProject;
            SelectedProjectPath = project.Path;
            DialogResult = true;
            Close();
        }
    }

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Project Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            ChosenAction = WelcomeDialogResult.SwitchProject;
            SelectedProjectPath = dialog.FolderName;
            DialogResult = true;
            Close();
        }
    }

    // --- Section 3: Continue Previous Chat ---

    private void RecentChats_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ContinueChatBtn.IsEnabled = RecentChatsList.SelectedItem is SessionDisplayItem;
        // Deselect project when chat is selected
        if (RecentChatsList.SelectedItem is not null)
            ProjectList.SelectedItem = null;
    }

    private void RecentChats_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentChatsList.SelectedItem is SessionDisplayItem item)
            AcceptChat(item);
    }

    private void ContinueChat_Click(object sender, RoutedEventArgs e)
    {
        if (RecentChatsList.SelectedItem is SessionDisplayItem item)
            AcceptChat(item);
    }

    private void AcceptChat(SessionDisplayItem item)
    {
        var entry = _historyService.Load(item.Id);
        if (entry is null) return;

        ChosenAction = WelcomeDialogResult.ContinueChat;
        SelectedChatEntry = entry;
        DialogResult = true;
        Close();
    }

    // --- Section 4: General Chat ---

    private void GeneralChat_Click(object sender, MouseButtonEventArgs e)
    {
        ChosenAction = WelcomeDialogResult.GeneralChat;
        DialogResult = true;
        Close();
    }
}
