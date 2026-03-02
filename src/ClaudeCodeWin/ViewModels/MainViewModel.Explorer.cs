using System.Collections.ObjectModel;
using System.IO;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private SubTab? _activeSubTab;

    public ObservableCollection<SubTab> SubTabs { get; } = [];

    public ExplorerViewModel Explorer { get; } = new();

    public TeamViewModel Team { get; private set; } = null!;
    private PlannerService _plannerService = null!;
    private AnalyzerService _analyzerService = null!;
    private PlanReviewerService _planReviewerService = null!;
    private TeamOrchestratorService _orchestratorService = null!;

    public SubTab? ActiveSubTab
    {
        get => _activeSubTab;
        set
        {
            if (_activeSubTab == value) return;

            if (_activeSubTab != null)
                _activeSubTab.IsActive = false;

            SetProperty(ref _activeSubTab, value);

            if (_activeSubTab != null)
                _activeSubTab.IsActive = true;

            OnPropertyChanged(nameof(IsExplorerActive));
            OnPropertyChanged(nameof(IsChatActive));
            OnPropertyChanged(nameof(IsFileEditorActive));
            OnPropertyChanged(nameof(IsTeamActive));
            OnPropertyChanged(nameof(ActiveFileTab));

            if (IsTeamActive)
                Team.Refresh();
        }
    }

    public bool IsExplorerActive => _activeSubTab?.Type == SubTabType.Explorer;
    public bool IsChatActive => _activeSubTab?.Type == SubTabType.Chat;
    public bool IsFileEditorActive => _activeSubTab?.Type == SubTabType.FileEditor;
    public bool IsTeamActive => _activeSubTab?.Type == SubTabType.Team;
    public SubTab? ActiveFileTab => _activeSubTab?.Type == SubTabType.FileEditor ? _activeSubTab : null;

    /// <summary>
    /// Initializes the default sub-tabs (Explorer + Chat). Called once during construction.
    /// </summary>
    private void InitializeSubTabs()
    {
        var explorerTab = new SubTab(SubTabType.Explorer, "Explorer");
        var chatTab = new SubTab(SubTabType.Chat, "Chat");
        var teamTab = new SubTab(SubTabType.Team, "Team");

        SubTabs.Add(chatTab);
        SubTabs.Add(explorerTab);
        SubTabs.Add(teamTab);

        // Wire explorer file open event
        Explorer.OnOpenFile += OpenFileInEditor;

        // Initialize AnalyzerService + PlannerService + OrchestratorService + Team VM
        _analyzerService = new AnalyzerService();
        _analyzerService.TeamNotesService = _teamNotesService;
        var analyzerPrompt = TeamPrompts.BuildAnalyzerSystemPrompt(WorkingDirectory, _projectRegistry.Projects);
        _analyzerService.Configure(_cliService.ClaudeExePath, WorkingDirectory, analyzerPrompt);

        _plannerService = new PlannerService();
        _plannerService.TeamNotesService = _teamNotesService;
        _plannerService.Configure(_cliService.ClaudeExePath, WorkingDirectory);

        _orchestratorService = new TeamOrchestratorService(_backlogService, _gitService);
        _orchestratorService.TeamNotesService = _teamNotesService;
        _orchestratorService.Configure(_cliService.ClaudeExePath, WorkingDirectory, _settings);

        _planReviewerService = new PlanReviewerService();
        _planReviewerService.TeamNotesService = _teamNotesService;
        _planReviewerService.Configure(_cliService.ClaudeExePath, WorkingDirectory);

        Team = new TeamViewModel(_backlogService, _gitService,
            () => WorkingDirectory,
            _plannerService, _analyzerService, _planReviewerService,
            _orchestratorService,
            _notificationService,
            new IdeasStorageService(), _projectRegistry,
            _settingsService, _settings, _teamNotesService);
        Team.SetTeamTab(teamTab);

        // Wire "Ask in Chat" — populate input box and switch to Chat tab
        Team.OnAskInChat += text =>
        {
            InputText = text;
            ActiveSubTab = chatTab;
        };

        ActiveSubTab = chatTab; // Start with Chat active
    }

    /// <summary>
    /// Updates the explorer root when the working directory changes.
    /// </summary>
    private void UpdateExplorerRoot()
    {
        if (!string.IsNullOrEmpty(WorkingDirectory) && Directory.Exists(WorkingDirectory))
            Explorer.SetRoot(WorkingDirectory);

        if (_analyzerService is not null)
        {
            var prompt = TeamPrompts.BuildAnalyzerSystemPrompt(WorkingDirectory, _projectRegistry.Projects);
            _analyzerService.Configure(_cliService.ClaudeExePath, WorkingDirectory, prompt);
        }
        _plannerService?.Configure(_cliService.ClaudeExePath, WorkingDirectory);
        _planReviewerService?.Configure(_cliService.ClaudeExePath, WorkingDirectory);
        _orchestratorService?.Configure(_cliService.ClaudeExePath, WorkingDirectory);
    }

    /// <summary>
    /// Opens a file in a new editor sub-tab, or activates existing tab if already open.
    /// </summary>
    public void OpenFileInEditor(string filePath)
    {
        if (!File.Exists(filePath)) return;

        // Check if already open
        var existing = SubTabs.FirstOrDefault(t =>
            t.Type == SubTabType.FileEditor &&
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            ActiveSubTab = existing;
            return;
        }

        // Load file content
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            Messages.Add(new MessageViewModel(MessageRole.System,
                $"Failed to open file: {ex.Message}"));
            return;
        }

        var fileName = Path.GetFileName(filePath);
        var tab = new SubTab(SubTabType.FileEditor, fileName, filePath)
        {
            Content = content
        };
        tab.MarkSaved(); // initial content is "saved" state

        SubTabs.Add(tab);
        ActiveSubTab = tab;
    }

    /// <summary>
    /// Closes a file editor sub-tab. Prompts to save if unsaved changes.
    /// </summary>
    public bool CloseFileTab(SubTab tab)
    {
        if (tab.Type != SubTabType.FileEditor) return false;

        if (tab.HasUnsavedChanges)
        {
            var result = System.Windows.MessageBox.Show(
                $"Save changes to '{tab.Title}'?",
                "Unsaved Changes",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);

            switch (result)
            {
                case System.Windows.MessageBoxResult.Yes:
                    SaveFileTab(tab);
                    break;
                case System.Windows.MessageBoxResult.Cancel:
                    return false;
                    // No — just close without saving
            }
        }

        var index = SubTabs.IndexOf(tab);
        SubTabs.Remove(tab);

        // Switch to adjacent tab
        if (ActiveSubTab == tab || _activeSubTab == null)
        {
            if (SubTabs.Count > 0)
                ActiveSubTab = SubTabs[Math.Min(index, SubTabs.Count - 1)];
        }

        return true;
    }

    /// <summary>
    /// Saves the content of a file editor tab to disk.
    /// </summary>
    public void SaveFileTab(SubTab? tab = null)
    {
        tab ??= _activeSubTab;
        if (tab?.Type != SubTabType.FileEditor || tab.FilePath == null) return;

        try
        {
            File.WriteAllText(tab.FilePath, tab.Content);
            tab.MarkSaved();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to save: {ex.Message}",
                "Save Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Sends the active file to chat for review.
    /// </summary>
    public void SendFileToReview(SubTab? tab = null)
    {
        tab ??= _activeSubTab;
        if (tab?.Type != SubTabType.FileEditor || tab.FilePath == null) return;

        // Switch to Chat tab and set input text
        var chatTab = SubTabs.FirstOrDefault(t => t.Type == SubTabType.Chat);
        if (chatTab != null)
            ActiveSubTab = chatTab;

        var relPath = !string.IsNullOrEmpty(WorkingDirectory)
            ? Path.GetRelativePath(WorkingDirectory, tab.FilePath)
            : tab.FilePath;

        InputText = $"Please review this file `{relPath}`:\n\n```\n{tab.Content}\n```";
    }

    public RelayCommand CloseFileTabCommand { get; private set; } = null!;
    public RelayCommand SaveFileCommand { get; private set; } = null!;
    public RelayCommand SendFileToReviewCommand { get; private set; } = null!;
    public RelayCommand SwitchToSubTabCommand { get; private set; } = null!;

    /// <summary>
    /// Initializes sub-tab related commands. Called from the constructor.
    /// </summary>
    private void InitializeSubTabCommands()
    {
        CloseFileTabCommand = new RelayCommand(p =>
        {
            if (p is SubTab tab)
                CloseFileTab(tab);
        });

        SaveFileCommand = new RelayCommand(_ => SaveFileTab());

        SendFileToReviewCommand = new RelayCommand(_ => SendFileToReview());

        SwitchToSubTabCommand = new RelayCommand(p =>
        {
            if (p is SubTab tab)
                ActiveSubTab = tab;
        });
    }
}
