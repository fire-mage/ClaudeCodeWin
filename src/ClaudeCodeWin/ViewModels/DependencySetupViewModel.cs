using ClaudeCodeWin.Infrastructure;

namespace ClaudeCodeWin.ViewModels;

public class DependencySetupViewModel : ViewModelBase
{
    private bool _showDependencyOverlay;
    private string _dependencyTitle = "";
    private string _dependencySubtitle = "";
    private string _dependencyStep = "";
    private string _dependencyStatus = "Preparing...";
    private string _dependencyLog = "";
    private bool _dependencyFailed;

    public bool ShowDependencyOverlay
    {
        get => _showDependencyOverlay;
        set => SetProperty(ref _showDependencyOverlay, value);
    }

    public string DependencyTitle
    {
        get => _dependencyTitle;
        set => SetProperty(ref _dependencyTitle, value);
    }

    public string DependencySubtitle
    {
        get => _dependencySubtitle;
        set => SetProperty(ref _dependencySubtitle, value);
    }

    public string DependencyStep
    {
        get => _dependencyStep;
        set => SetProperty(ref _dependencyStep, value);
    }

    public string DependencyStatus
    {
        get => _dependencyStatus;
        set => SetProperty(ref _dependencyStatus, value);
    }

    public string DependencyLog
    {
        get => _dependencyLog;
        set => SetProperty(ref _dependencyLog, value);
    }

    public bool DependencyFailed
    {
        get => _dependencyFailed;
        set => SetProperty(ref _dependencyFailed, value);
    }
}
