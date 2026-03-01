using System.IO;
using System.Text.Json;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class BacklogService
{
    private static readonly string BacklogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin");

    private static readonly string BacklogPath = Path.Combine(BacklogDir, "backlog.json");

    private readonly object _lock = new();
    private List<BacklogFeature> _features = [];

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(BacklogPath))
            {
                _features = [];
                return;
            }

            try
            {
                var json = File.ReadAllText(BacklogPath);
                _features = JsonSerializer.Deserialize<List<BacklogFeature>>(json, JsonDefaults.ReadOptions) ?? [];
            }
            catch
            {
                _features = [];
            }
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(BacklogDir);
        var json = JsonSerializer.Serialize(_features, JsonDefaults.Options);
        File.WriteAllText(BacklogPath, json);
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
    /// Gets the next pending phase from the highest-priority planned feature for the given project.
    /// </summary>
    public (BacklogFeature Feature, BacklogPhase Phase)? GetNextPendingPhase(string projectPath)
    {
        lock (_lock)
        {
            var planned = _features
                .Where(f => f.ProjectPath.PathEquals(projectPath)
                            && f.Status is FeatureStatus.Planned or FeatureStatus.InProgress)
                .OrderBy(f => f.Priority)
                .ThenBy(f => f.CreatedAt);

            foreach (var feature in planned)
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
        string? errorMessage = null)
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

    private static void UpdateFeatureStatusFromPhases(BacklogFeature feature)
    {
        if (feature.Phases.Count == 0) return;

        if (feature.Phases.All(p => p.Status == PhaseStatus.Done))
            feature.Status = FeatureStatus.Done;
        else if (feature.Phases.Any(p => p.Status is PhaseStatus.InProgress or PhaseStatus.InReview))
            feature.Status = FeatureStatus.InProgress;
    }
}
