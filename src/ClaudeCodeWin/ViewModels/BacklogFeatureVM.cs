using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class BacklogFeatureVM : ViewModelBase
{
    private bool _isExpanded;
    private string _answerText = "";

    public BacklogFeature Feature { get; }

    public BacklogFeatureVM(BacklogFeature feature)
    {
        Feature = feature;
    }

    public string AnswerText
    {
        get => _answerText;
        set => SetProperty(ref _answerText, value);
    }

    public string Id => Feature.Id;

    public string DisplayTitle
    {
        get
        {
            var text = Feature.Title ?? Feature.RawIdea;
            return text.Length > 80 ? text[..77] + "..." : text;
        }
    }

    public FeatureStatus Status => Feature.Status;
    public bool HasPhases => Feature.Phases.Count > 0;
    public bool NeedsUserInput => Feature.NeedsUserInput;
    public string? PlannerQuestion => Feature.PlannerQuestion;

    public string PhaseProgressText
    {
        get
        {
            if (Feature.Phases.Count == 0) return "";
            var done = Feature.Phases.Count(p => p.Status == PhaseStatus.Done);
            return $"[{done}/{Feature.Phases.Count} phases]";
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private List<PhaseDisplayItem>? _phaseItems;

    public List<PhaseDisplayItem> PhaseItems =>
        _phaseItems ??= Feature.Phases
            .OrderBy(p => p.Order)
            .Select(p => new PhaseDisplayItem(p))
            .ToList();
}

public class PhaseDisplayItem
{
    public BacklogPhase Phase { get; }

    public PhaseDisplayItem(BacklogPhase phase)
    {
        Phase = phase;
    }

    public string StatusIcon => Phase.Status switch
    {
        PhaseStatus.Done => "[v]",
        PhaseStatus.InProgress => "[>]",
        PhaseStatus.InReview => "[R]",
        PhaseStatus.Pending => "[-]",
        PhaseStatus.Failed => "[!]",
        _ => "[-]"
    };

    public string Title => Phase.Title;

    public string TimeText
    {
        get
        {
            if (Phase.CompletedAt.HasValue)
                return Phase.CompletedAt.Value.ToString("HH:mm");
            if (Phase.StartedAt.HasValue)
                return Phase.StartedAt.Value.ToString("HH:mm");
            return "";
        }
    }

    public string StatusText => Phase.Status switch
    {
        PhaseStatus.Done => "done",
        PhaseStatus.InProgress => "in progress",
        PhaseStatus.InReview => "in review",
        PhaseStatus.Pending => "pending",
        PhaseStatus.Failed => "failed",
        _ => ""
    };
}
