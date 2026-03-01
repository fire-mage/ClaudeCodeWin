using System.IO;
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

    // Analysis display properties
    public bool IsAnalyzing => Feature.Status == FeatureStatus.Analyzing;
    public bool IsAnalysisDone => Feature.Status == FeatureStatus.AnalysisDone;
    public bool IsAnalysisRejected => Feature.Status == FeatureStatus.AnalysisRejected;
    public bool ShowAnalysisActions => Feature.Status == FeatureStatus.Analyzing
        || (Feature.Status == FeatureStatus.AwaitingUser
            && Feature.AwaitingReason == AwaitingUserReason.AnalysisQuestion);
    public string? AnalysisResult => Feature.AnalysisResult;
    public string? RejectionReason => Feature.RejectionReason;
    public bool HasAnalysisResult => !string.IsNullOrEmpty(Feature.AnalysisResult);
    public bool HasCrossProjectHint => Feature.AffectedProjects.Count > 0;
    public string CrossProjectText => Feature.AffectedProjects.Count > 0
        ? $"Affects: {string.Join(", ", Feature.AffectedProjects)}"
        : "";

    // Planning section properties
    public bool IsPlanning => Feature.Status == FeatureStatus.Planning;
    public bool IsPlanningFailed => Feature.Status == FeatureStatus.PlanningFailed;
    public bool IsPlanningQuestion => Feature.Status == FeatureStatus.AwaitingUser
        && Feature.AwaitingReason == AwaitingUserReason.PlanningQuestion;

    // Plan approval section properties
    public bool IsPlanReady => Feature.Status == FeatureStatus.PlanReady;
    public bool HasPlanReview => !string.IsNullOrEmpty(Feature.PlanReviewVerdict);
    public string? PlanReviewVerdict => Feature.PlanReviewVerdict;
    public string? PlanReviewComments => Feature.PlanReviewComments;
    public bool HasPlanReviewSuggestions => Feature.PlanReviewSuggestions.Count > 0;
    public string PlanReviewSuggestionsText => Feature.PlanReviewSuggestions.Count > 0
        ? string.Join("\n", Feature.PlanReviewSuggestions.Select((s, i) => $"{i + 1}. {s}"))
        : "";
    public bool IsPlanReviewApproved => string.Equals(Feature.PlanReviewVerdict, "approve",
        StringComparison.OrdinalIgnoreCase);
    public bool IsPlanReviewRejected => string.Equals(Feature.PlanReviewVerdict, "reject",
        StringComparison.OrdinalIgnoreCase);
    public bool IsPlanReviewError => string.Equals(Feature.PlanReviewVerdict, "error",
        StringComparison.OrdinalIgnoreCase);

    private bool _isReviewInProgress;
    public bool IsReviewInProgress
    {
        get => _isReviewInProgress;
        set => SetProperty(ref _isReviewInProgress, value);
    }

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

    // Backlog section properties
    public int PhaseCount => Feature.Phases.Count;
    public string PriorityText => $"P{Feature.Priority}";
    public bool HasError => !string.IsNullOrEmpty(Feature.ErrorSummary);
    public string? ErrorSummary => Feature.ErrorSummary;
    public string? ErrorDetails => Feature.ErrorDetails;
    public bool HasSessionHistory => Feature.SessionHistoryPaths.Count > 0;
    public bool ReviewDismissed => Feature.ReviewDismissed;

    private bool _isErrorExpanded;
    public bool IsErrorExpanded
    {
        get => _isErrorExpanded;
        set => SetProperty(ref _isErrorExpanded, value);
    }

    // Queue section properties
    public bool IsQueued => Feature.Status == FeatureStatus.Queued;
    public bool IsInProgress => Feature.Status == FeatureStatus.InProgress;
    public bool IsQueuedOrInProgress => Feature.Status is FeatureStatus.Queued or FeatureStatus.InProgress;

    public string ActivePhaseText
    {
        get
        {
            if (Feature.Phases.Count == 0) return "";
            var active = Feature.Phases
                .Where(p => p.Status is PhaseStatus.InProgress or PhaseStatus.InReview)
                .OrderBy(p => p.Order)
                .FirstOrDefault();
            if (active == null) return "";
            return active.Status switch
            {
                PhaseStatus.InProgress => $"Dev: {active.Title}",
                PhaseStatus.InReview => $"Review: {active.Title}",
                _ => ""
            };
        }
    }

    // Completed section properties
    public bool IsDone => Feature.Status == FeatureStatus.Done;
    public bool IsCancelled => Feature.Status == FeatureStatus.Cancelled;
    public bool HasChangedFiles => IsDone && AllChangedFiles.Count > 0;

    public string CompletedPhasesText
    {
        get
        {
            if (Feature.Phases.Count == 0) return "No phases";
            var done = Feature.Phases.Count(p => p.Status == PhaseStatus.Done);
            return $"{done}/{Feature.Phases.Count} phases completed";
        }
    }

    public string ChangedFilesSummary
    {
        get
        {
            var files = Feature.Phases
                .SelectMany(p => p.ChangedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0) return "No changed files";
            if (files.Count <= 3) return string.Join(", ", files.Select(Path.GetFileName));
            return $"{files.Count} files changed";
        }
    }

    public bool HasUserActions => Feature.Phases
        .Any(p => p.UserActions is { Count: > 0 });

    public string UserActionsText
    {
        get
        {
            var actions = Feature.Phases
                .Where(p => p.UserActions is { Count: > 0 })
                .SelectMany(p => p.UserActions!)
                .ToList();
            return string.Join("\n", actions.Select(a => $"\u2022 {a}"));
        }
    }

    private List<string>? _allChangedFiles;

    public List<string> AllChangedFiles =>
        _allChangedFiles ??= Feature.Phases
            .SelectMany(p => p.ChangedFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
