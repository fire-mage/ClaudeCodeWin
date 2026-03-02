using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class BacklogService
{
    private static readonly string BacklogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin");

    private static readonly string BacklogPath = Path.Combine(BacklogDir, "backlog.json");
    private static readonly string ArchivePath = Path.Combine(BacklogDir, "archive.json");

    private readonly object _lock = new();
    private List<BacklogFeature> _features = [];
    private FileSystemWatcher? _watcher;
    private bool _isSaving;
    private DateTime _lastExternalReload = DateTime.MinValue;

    /// <summary>
    /// Fired when backlog.json is modified externally (e.g. by Claude writing to the file).
    /// Subscribers should refresh their UI. Raised on a background thread.
    /// </summary>
    public event Action? OnExternalChange;

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(BacklogPath))
            {
                _features = [];
                StartFileWatcher();
                return;
            }

            try
            {
                var json = File.ReadAllText(BacklogPath);
                var migrated = MigrateJson(json);
                _features = JsonSerializer.Deserialize<List<BacklogFeature>>(migrated, JsonDefaults.ReadOptions) ?? [];
                var postFixed = PostMigrationFixup();
                if (migrated != json || postFixed)
                    SaveLocked();
            }
            catch
            {
                _features = [];
            }
        }

        StartFileWatcher();
    }

    private void StartFileWatcher()
    {
        _watcher?.Dispose();
        Directory.CreateDirectory(BacklogDir);

        _watcher = new FileSystemWatcher(BacklogDir, "backlog.json")
        {
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_isSaving) return;

        // Debounce: ignore rapid successive changes
        var now = DateTime.Now;
        if ((now - _lastExternalReload).TotalMilliseconds < 500) return;
        _lastExternalReload = now;

        lock (_lock)
        {
            try
            {
                var json = File.ReadAllText(BacklogPath);
                var migrated = MigrateJson(json);
                _features = JsonSerializer.Deserialize<List<BacklogFeature>>(migrated, JsonDefaults.ReadOptions) ?? [];
            }
            catch
            {
                return; // File might be mid-write
            }
        }

        OnExternalChange?.Invoke();
    }

    /// <summary>
    /// Migrate old enum values in JSON to current FeatureStatus values.
    /// Remove after a few versions.
    /// </summary>
    private static string MigrateJson(string json)
    {
        json = Regex.Replace(json, @"""[Ss]tatus""\s*:\s*""[Rr]aw""", @"""status"": ""planning""");
        json = Regex.Replace(json, @"""[Ss]tatus""\s*:\s*""[Pp]lanned""", @"""status"": ""planApproved""");
        json = Regex.Replace(json, @"""[Ss]tatus""\s*:\s*""[Aa]nalyzing""", @"""status"": ""planning""");
        json = Regex.Replace(json, @"""[Ss]tatus""\s*:\s*""[Aa]nalysisDone""", @"""status"": ""planning""");
        json = Regex.Replace(json, @"""[Ss]tatus""\s*:\s*""[Aa]nalysisRejected""", @"""status"": ""cancelled""");
        json = Regex.Replace(json, @"""[Aa]waitingReason""\s*:\s*""[Aa]nalysisQuestion""", @"""awaitingReason"": ""planningQuestion""");
        return json;
    }

    private void SaveLocked()
    {
        _isSaving = true;
        try
        {
            Directory.CreateDirectory(BacklogDir);
            var json = JsonSerializer.Serialize(_features, JsonDefaults.Options);
            File.WriteAllText(BacklogPath, json);
        }
        finally
        {
            _isSaving = false;
        }
    }

    public List<BacklogFeature> GetFeatures(string? projectPath = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(projectPath))
                return _features.ToList();

            return _features
                .Where(f => f.ProjectPath.PathEquals(projectPath))
                .ToList();
        }
    }

    public List<BacklogFeature> GetFeaturesByStatus(string projectPath, FeatureStatus status)
    {
        lock (_lock)
        {
            return _features
                .Where(f => f.ProjectPath.PathEquals(projectPath) && f.Status == status)
                .OrderBy(f => f.Priority)
                .ThenBy(f => f.CreatedAt)
                .ToList();
        }
    }

    public BacklogFeature AddFeature(string projectPath, string rawIdea)
    {
        lock (_lock)
        {
            var feature = new BacklogFeature
            {
                ProjectPath = projectPath,
                RawIdea = rawIdea
            };
            _features.Add(feature);
            SaveLocked();
            return feature;
        }
    }

    public void UpdateFeature(BacklogFeature feature)
    {
        lock (_lock)
        {
            var index = _features.FindIndex(f => f.Id == feature.Id);
            if (index >= 0)
            {
                feature.UpdatedAt = DateTime.Now;
                _features[index] = feature;
                SaveLocked();
            }
        }
    }

    /// <summary>
    /// Atomically find and modify a feature under lock. Avoids data races when
    /// callers would otherwise mutate the object outside the lock then call UpdateFeature.
    /// </summary>
    public bool ModifyFeature(string featureId, Action<BacklogFeature> mutator)
    {
        lock (_lock)
        {
            var feature = _features.FirstOrDefault(f => f.Id == featureId);
            if (feature is null) return false;

            mutator(feature);
            feature.UpdatedAt = DateTime.Now;
            SaveLocked();
            return true;
        }
    }

    public void DeleteFeature(string featureId)
    {
        lock (_lock)
        {
            if (_features.RemoveAll(f => f.Id == featureId) > 0)
                SaveLocked();
        }
    }

    /// <summary>
    /// Gets the next pending phase from the highest-priority feature in Queued or InProgress status.
    /// Only features explicitly added to the queue (status=Queued) are picked up.
    /// PlanApproved features sit in backlog until user moves them to queue.
    /// </summary>
    public (BacklogFeature Feature, BacklogPhase Phase)? GetNextPendingPhase(string projectPath)
    {
        lock (_lock)
        {
            var candidates = _features
                .Where(f => f.ProjectPath.PathEquals(projectPath)
                            && f.Status is FeatureStatus.Queued or FeatureStatus.InProgress)
                .OrderBy(f => f.Priority)
                .ThenBy(f => f.CreatedAt);

            foreach (var feature in candidates)
            {
                var phase = feature.Phases
                    .Where(p => p.Status == PhaseStatus.Pending)
                    .OrderBy(p => p.Order)
                    .FirstOrDefault();

                if (phase is not null)
                    return (feature, phase);
            }

            return null;
        }
    }

    public void MarkPhaseStatus(string featureId, string phaseId, PhaseStatus status,
        string? summary = null, List<string>? changedFiles = null, string? commitHash = null,
        string? errorMessage = null, List<string>? userActions = null)
    {
        lock (_lock)
        {
            var feature = _features.FirstOrDefault(f => f.Id == featureId);
            if (feature is null) return;
            var phase = feature.Phases.FirstOrDefault(p => p.Id == phaseId);
            if (phase is null) return;

            phase.Status = status;
            if (summary is not null) phase.Summary = summary;
            if (changedFiles is not null) phase.ChangedFiles = changedFiles;
            if (commitHash is not null) phase.CommitHash = commitHash;
            if (errorMessage is not null) phase.ErrorMessage = errorMessage;
            if (userActions is not null) phase.UserActions = userActions;

            if (status == PhaseStatus.InProgress && phase.StartedAt is null)
                phase.StartedAt = DateTime.Now;
            if (status is PhaseStatus.Done or PhaseStatus.Failed)
                phase.CompletedAt = DateTime.Now;

            // Auto-update feature status based on phases
            UpdateFeatureStatusFromPhases(feature);

            feature.UpdatedAt = DateTime.Now;
            SaveLocked();
        }
    }

    public void MarkFeatureStatus(string featureId, FeatureStatus status)
    {
        lock (_lock)
        {
            var feature = _features.FirstOrDefault(f => f.Id == featureId);
            if (feature is null) return;

            feature.Status = status;
            feature.UpdatedAt = DateTime.Now;
            SaveLocked();
        }
    }

    public List<BacklogFeature> GetFeaturesNeedingUserInput()
    {
        lock (_lock)
        {
            return _features
                .Where(f => f.NeedsUserInput)
                .OrderBy(f => f.Priority)
                .ToList();
        }
    }

    /// <summary>
    /// Move a feature to archive.json with a timestamp.
    /// </summary>
    public void ArchiveFeature(string featureId)
    {
        lock (_lock)
        {
            var feature = _features.FirstOrDefault(f => f.Id == featureId);
            if (feature is null) return;

            feature.ArchivedAt = DateTime.Now;

            // Write to archive FIRST (duplicate is recoverable; loss is not)
            var archived = LoadArchivedLocked();
            // Prevent duplicates if archive already contains this feature (e.g. partial failure retry)
            archived.RemoveAll(f => f.Id == featureId);
            archived.Add(feature);
            SaveArchivedLocked(archived);

            // Then remove from active backlog
            _features.RemoveAll(f => f.Id == featureId);
            SaveLocked();
        }
    }

    public List<BacklogFeature> GetArchivedFeatures(string? projectPath = null)
    {
        lock (_lock)
        {
            var archived = LoadArchivedLocked();
            if (string.IsNullOrEmpty(projectPath))
                return archived;

            return archived
                .Where(f => f.ProjectPath.PathEquals(projectPath))
                .ToList();
        }
    }

    private List<BacklogFeature> LoadArchivedLocked()
    {
        if (!File.Exists(ArchivePath))
            return [];

        try
        {
            var json = File.ReadAllText(ArchivePath);
            var migrated = MigrateJson(json);
            var list = JsonSerializer.Deserialize<List<BacklogFeature>>(migrated, JsonDefaults.ReadOptions) ?? [];
            if (migrated != json)
                SaveArchivedLocked(list);
            return list;
        }
        catch
        {
            return [];
        }
    }

    private void SaveArchivedLocked(List<BacklogFeature> archived)
    {
        Directory.CreateDirectory(BacklogDir);
        var json = JsonSerializer.Serialize(archived, JsonDefaults.Options);
        File.WriteAllText(ArchivePath, json);
    }

    /// <summary>
    /// Fix features that were migrated from AnalysisQuestion → PlanningQuestion
    /// but have no planner session. Reset them to Planning so they don't show a stale question.
    /// </summary>
    private bool PostMigrationFixup()
    {
        var changed = false;
        foreach (var f in _features)
        {
            if (f.Status == FeatureStatus.AwaitingUser
                && f.AwaitingReason == AwaitingUserReason.PlanningQuestion
                && string.IsNullOrEmpty(f.PlannerSessionId))
            {
                f.Status = FeatureStatus.Planning;
                f.AwaitingReason = null;
                f.PlannerQuestion = null;
                f.NeedsUserInput = false;
                changed = true;
            }
        }
        return changed;
    }

    private static void UpdateFeatureStatusFromPhases(BacklogFeature feature)
    {
        if (feature.Phases.Count == 0) return;

        if (feature.Phases.All(p => p.Status == PhaseStatus.Done))
            feature.Status = FeatureStatus.Done;
        else if (feature.Phases.Any(p => p.Status is PhaseStatus.InProgress or PhaseStatus.InReview))
            feature.Status = FeatureStatus.InProgress;
    }
}
