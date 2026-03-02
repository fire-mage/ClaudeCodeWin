using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public class NotepadViewModel : ViewModelBase
{
    private readonly NotepadStorageService _storage;
    private readonly DispatcherTimer _autoSaveTimer;
    private string? _selectedNote;
    private string _noteContent = "";
    private bool _isLoaded;
    private bool _isRenaming;
    private string _renameText = "";
    private bool _suppressAutoSave;
    private bool _shutdownCalled;

    public ObservableCollection<string> Notes { get; } = [];

    public string? SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (_selectedNote == value) return;
            FlushAutoSave();
            SetProperty(ref _selectedNote, value);
            LoadSelectedNoteContent();
        }
    }

    public string NoteContent
    {
        get => _noteContent;
        set
        {
            if (SetProperty(ref _noteContent, value) && !_suppressAutoSave)
                StartAutoSaveTimer();
        }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetProperty(ref _isRenaming, value);
    }

    public string RenameText
    {
        get => _renameText;
        set => SetProperty(ref _renameText, value);
    }

    public ICommand CreateNoteCommand { get; }
    public ICommand DeleteNoteCommand { get; }
    public ICommand RenameNoteCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }

    /// <summary>Flushes pending auto-save. Call on app shutdown or when navigating away.</summary>
    public void Shutdown()
    {
        if (_shutdownCalled) return;
        _shutdownCalled = true;
        FlushAutoSave();
        _isLoaded = false; // Force re-scan on next activation (picks up external changes)
    }

    /// <summary>Resets shutdown guard so Shutdown() works again on next deactivation.</summary>
    public void Activate() => _shutdownCalled = false;

    public NotepadViewModel(NotepadStorageService storage)
    {
        _storage = storage;

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        CreateNoteCommand = new RelayCommand(_ => CreateNote());
        DeleteNoteCommand = new RelayCommand(_ => DeleteNote(), _ => SelectedNote != null);
        RenameNoteCommand = new RelayCommand(_ => StartRename(), _ => SelectedNote != null);
        CommitRenameCommand = new RelayCommand(_ => CommitRename());
        CancelRenameCommand = new RelayCommand(_ => CancelRename());
    }

    public void LoadNotes()
    {
        if (_isLoaded) return;

        try
        {
            var previousSelection = _selectedNote;
            var names = _storage.GetNoteNames();
            Notes.Clear();
            foreach (var name in names)
                Notes.Add(name);

            // Restore previous selection if it still exists, otherwise pick first
            if (previousSelection != null && Notes.Contains(previousSelection))
                SelectedNote = previousSelection;
            else if (Notes.Count > 0)
                SelectedNote = Notes[0];

            _isLoaded = true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("NOTEPAD_LOAD_ERROR", ex.Message);
            // _isLoaded stays false — allows retry on next tab activation
        }
    }

    private void LoadSelectedNoteContent()
    {
        if (_selectedNote == null)
        {
            SetContentSilently("");
            return;
        }

        try
        {
            var content = _storage.LoadNote(_selectedNote);
            SetContentSilently(content);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("NOTEPAD_LOAD_NOTE_ERROR", ex.Message);
            SetContentSilently("");
        }
    }

    private void SetContentSilently(string content)
    {
        _suppressAutoSave = true;
        NoteContent = content;
        _suppressAutoSave = false;
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (_selectedNote != null)
        {
            try { _storage.SaveNote(_selectedNote, _noteContent); }
            catch (Exception ex) { DiagnosticLogger.Log("NOTEPAD_AUTOSAVE_TICK_ERROR", ex.Message); }
        }
    }

    private void StartAutoSaveTimer()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void FlushAutoSave()
    {
        if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            if (_selectedNote != null)
            {
                try { _storage.SaveNote(_selectedNote, _noteContent); }
                catch (Exception ex) { DiagnosticLogger.Log("NOTEPAD_FLUSH_ERROR", ex.Message); }
            }
        }
    }

    private void CreateNote()
    {
        try
        {
            var name = _storage.CreateNote();
            Notes.Add(name);
            SelectedNote = name;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create note: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteNote()
    {
        if (_selectedNote == null) return;

        var result = MessageBox.Show(
            $"Delete note \"{_selectedNote}\"?",
            "Delete Note",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _autoSaveTimer.Stop();
        var name = _selectedNote;
        var index = Notes.IndexOf(name);

        try
        {
            _storage.DeleteNote(name);
            Notes.Remove(name);

            if (Notes.Count > 0)
                SelectedNote = Notes[Math.Clamp(index, 0, Notes.Count - 1)];
            else
                SelectedNote = null;
        }
        catch (Exception ex)
        {
            StartAutoSaveTimer();
            MessageBox.Show($"Failed to delete: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartRename()
    {
        if (_selectedNote == null) return;
        RenameText = _selectedNote;
        IsRenaming = true;
    }

    private void CommitRename()
    {
        if (!_isRenaming || _selectedNote == null) return;

        var oldName = _selectedNote;
        var newName = _renameText.Trim();
        IsRenaming = false;

        if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
            return;

        FlushAutoSave();

        try
        {
            var actualName = _storage.RenameNote(oldName, newName);

            // Suppress auto-save during collection update — WPF may briefly
            // set SelectedNote=null when the old item is replaced, which would
            // trigger LoadSelectedNoteContent("") and risk saving empty content.
            _suppressAutoSave = true;
            try
            {
                var index = Notes.IndexOf(oldName);
                if (index >= 0)
                    Notes[index] = actualName;
                else
                    Notes.Add(actualName);
                SelectedNote = actualName;
            }
            finally { _suppressAutoSave = false; }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to rename: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelRename()
    {
        IsRenaming = false;
    }
}
