using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public class NotepadViewModel : ViewModelBase
{
    private readonly NotepadStorageService _storage;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _vectorIndexTimer;
    private string? _selectedNote;
    private List<NoteBlock> _noteBlocks = [NoteBlock.CreateText("")];
    private bool _isLoaded;
    private bool _isRenaming;
    private string _renameText = "";
    private bool _suppressAutoSave;
    private bool _shutdownCalled;
    private VectorMemoryService? _vectorMemory;
    private string? _projectPath;

    public void SetVectorMemory(VectorMemoryService? vectorMemory) => _vectorMemory = vectorMemory;
    public void SetProjectPath(string? projectPath) => _projectPath = projectPath;

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

    public List<NoteBlock> NoteBlocks
    {
        get => _noteBlocks;
        set
        {
            if (SetProperty(ref _noteBlocks, value))
            {
                OnPropertyChanged(nameof(NoteContent));
                OnPropertyChanged(nameof(HasImages));
                if (!_suppressAutoSave)
                    StartAutoSaveTimer();
            }
        }
    }

    public string NoteContent
    {
        get => string.Join("\n", _noteBlocks.Where(b => b.Type == NoteBlockType.Text && b.Text != null).Select(b => b.Text));
        set
        {
            // Guard: never overwrite rich blocks (text+images) from text-only setter
            if (HasImages) return;
            _noteBlocks = [NoteBlock.CreateText(value)];
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoteBlocks));
            if (!_suppressAutoSave)
                StartAutoSaveTimer();
        }
    }

    public bool HasImages => _noteBlocks.Any(b => b.Type == NoteBlockType.Image);

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
    public ICommand SaveNoteCommand { get; }
    public ICommand DeleteNoteCommand { get; }
    public ICommand RenameNoteCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }

    public event Action? OnNoteSavedFromMessage;
    public event Action<List<NoteBlock>>? OnSendNoteAsMessage;

    public void Shutdown()
    {
        if (_shutdownCalled) return;
        _shutdownCalled = true;
        FlushAutoSave();
        _isLoaded = false;
    }

    public void Activate() => _shutdownCalled = false;

    public NotepadViewModel(NotepadStorageService storage)
    {
        _storage = storage;

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        _vectorIndexTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _vectorIndexTimer.Tick += VectorIndexTimer_Tick;

        CreateNoteCommand = new RelayCommand(_ => CreateNote());
        SaveNoteCommand = new RelayCommand(_ => SaveNote(), _ => _selectedNote != null || HasContentToSave());
        DeleteNoteCommand = new RelayCommand(_ => DeleteNote(), _ => SelectedNote != null);
        RenameNoteCommand = new RelayCommand(_ => StartRename(), _ => SelectedNote != null);
        CommitRenameCommand = new RelayCommand(_ => CommitRename());
        CancelRenameCommand = new RelayCommand(_ => CancelRename());
    }

    private bool HasContentToSave()
    {
        return _noteBlocks.Any(b =>
            (b.Type == NoteBlockType.Text && !string.IsNullOrWhiteSpace(b.Text)) ||
            b.Type == NoteBlockType.Image);
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

            if (previousSelection != null && Notes.Contains(previousSelection))
                SelectedNote = previousSelection;
            else if (Notes.Count > 0)
                SelectedNote = Notes[0];

            _isLoaded = true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("NOTEPAD_LOAD_ERROR", ex.Message);
        }
    }

    public void AddNoteFromMessage(string? text, List<string>? imagePaths)
    {
        try
        {
            var blocks = new List<NoteBlock>();

            if (!string.IsNullOrEmpty(text))
                blocks.Add(NoteBlock.CreateText(text));

            if (imagePaths != null)
            {
                foreach (var imgPath in imagePaths)
                {
                    if (File.Exists(imgPath))
                    {
                        var cached = _storage.CacheImage(imgPath);
                        blocks.Add(NoteBlock.CreateImage(cached));
                    }
                }
            }

            if (blocks.Count == 0) return;

            SaveBlocksAsNewNote(blocks);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("NOTEPAD_SAVE_FROM_MESSAGE_ERROR", ex.Message);
            MessageBox.Show($"Failed to save to Notepad: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void AddNoteFromComposerBlocks(List<ComposerBlock> composerBlocks)
    {
        try
        {
            var blocks = new List<NoteBlock>();
            foreach (var cb in composerBlocks)
            {
                if (cb is TextComposerBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
                    blocks.Add(NoteBlock.CreateText(textBlock.Text));
                else if (cb is ImageComposerBlock imageBlock && File.Exists(imageBlock.FilePath))
                {
                    var cached = _storage.CacheImage(imageBlock.FilePath);
                    blocks.Add(NoteBlock.CreateImage(cached));
                }
            }

            if (blocks.Count == 0) return;

            SaveBlocksAsNewNote(blocks);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("NOTEPAD_SAVE_FROM_COMPOSER_ERROR", ex.Message);
            MessageBox.Show($"Failed to save to Notepad: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveBlocksAsNewNote(List<NoteBlock> blocks)
    {
        var noteName = _storage.CreateAndSaveNote($"Saved {DateTime.Now:yyyy-MM-dd HH.mm}", blocks);

        if (!_isLoaded) LoadNotes();
        else
        {
            Notes.Add(noteName);
            _suppressAutoSave = true;
            try { SelectedNote = noteName; }
            finally { _suppressAutoSave = false; }
        }

        OnNoteSavedFromMessage?.Invoke();
    }

    public void RequestSendNoteAsMessage()
    {
        if (!HasContentToSave()) return;

        var result = MessageBox.Show(
            "Send this note content (including any images) as a chat message to the assistant?",
            "Send Note",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        OnSendNoteAsMessage?.Invoke(_noteBlocks.ToList());
    }

    public string GetImagePath(string cachedFileName) => _storage.GetImagePath(cachedFileName);

    private void LoadSelectedNoteContent()
    {
        if (_selectedNote == null)
        {
            SetBlocksSilently([NoteBlock.CreateText("")]);
            return;
        }

        try
        {
            var blocks = _storage.LoadNoteBlocks(_selectedNote);
            SetBlocksSilently(blocks);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("NOTEPAD_LOAD_NOTE_ERROR", ex.Message);
            SetBlocksSilently([NoteBlock.CreateText("")]);
        }
    }

    private void SetBlocksSilently(List<NoteBlock> blocks)
    {
        _suppressAutoSave = true;
        _noteBlocks = blocks;
        OnPropertyChanged(nameof(NoteBlocks));
        OnPropertyChanged(nameof(NoteContent));
        OnPropertyChanged(nameof(HasImages));
        _suppressAutoSave = false;
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (_selectedNote != null)
        {
            try
            {
                _storage.SaveNoteBlocks(_selectedNote, _noteBlocks);
                RestartVectorIndexTimer();
            }
            catch (Exception ex) { DiagnosticLogger.Log("NOTEPAD_AUTOSAVE_TICK_ERROR", ex.Message); }
        }
        else if (HasContentToSave())
        {
            try { CreateAndSaveNewNote(); }
            catch (Exception ex) { DiagnosticLogger.Log("NOTEPAD_AUTOCREATE_ERROR", ex.Message); }
        }
    }

    private void StartAutoSaveTimer()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void FlushAutoSave()
    {
        // Flush pending vector index on note switch/shutdown
        if (_vectorIndexTimer.IsEnabled && _selectedNote != null)
        {
            _vectorIndexTimer.Stop();
            IndexNoteInVectorMemory(_selectedNote, _noteBlocks);
        }

        if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            if (_selectedNote != null)
            {
                try { _storage.SaveNoteBlocks(_selectedNote, _noteBlocks); }
                catch (Exception ex) { DiagnosticLogger.Log("NOTEPAD_FLUSH_ERROR", ex.Message); }
            }
            else if (HasContentToSave())
            {
                try { CreateAndSaveNewNote(); }
                catch (Exception ex) { DiagnosticLogger.Log("NOTEPAD_FLUSH_CREATE_ERROR", ex.Message); }
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

    private void SaveNote()
    {
        if (_selectedNote != null)
        {
            _autoSaveTimer.Stop();
            try
            {
                _storage.SaveNoteBlocks(_selectedNote, _noteBlocks);
                IndexNoteInVectorMemory(_selectedNote, _noteBlocks);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save note: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        if (!HasContentToSave()) return;

        _autoSaveTimer.Stop();
        try { CreateAndSaveNewNote(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save note: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CreateAndSaveNewNote()
    {
        var name = _storage.CreateNote($"Note {DateTime.Now:yyyy-MM-dd HH.mm}");
        _storage.SaveNoteBlocks(name, _noteBlocks);
        IndexNoteInVectorMemory(name, _noteBlocks);
        Notes.Add(name);
        _suppressAutoSave = true;
        try { SelectedNote = name; }
        finally { _suppressAutoSave = false; }
    }

    private void RestartVectorIndexTimer()
    {
        _vectorIndexTimer.Stop();
        _vectorIndexTimer.Start();
    }

    private void VectorIndexTimer_Tick(object? sender, EventArgs e)
    {
        _vectorIndexTimer.Stop();
        if (_selectedNote != null)
            IndexNoteInVectorMemory(_selectedNote, _noteBlocks);
    }

    private void IndexNoteInVectorMemory(string noteName, List<NoteBlock> blocks)
    {
        if (_vectorMemory?.IsAvailable != true || string.IsNullOrEmpty(_projectPath)) return;

        var textContent = string.Join("\n", blocks
            .Where(b => b.Type == NoteBlockType.Text && !string.IsNullOrWhiteSpace(b.Text))
            .Select(b => b.Text));
        if (string.IsNullOrWhiteSpace(textContent)) return;

        var vm = _vectorMemory;
        var pp = _projectPath;
        _ = Task.Run(() =>
        {
            try
            {
                vm.IndexDocument(pp, "notepad", noteName, textContent,
                    new Dictionary<string, string> { ["name"] = noteName });
            }
            catch (Exception ex) { DiagnosticLogger.Log("NOTEPAD_VECTOR_INDEX_ERROR", ex.Message); }
        });
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
            if (_vectorMemory?.IsAvailable == true && !string.IsNullOrEmpty(_projectPath))
            {
                var vm = _vectorMemory;
                var pp = _projectPath;
                _ = Task.Run(() => { try { vm.RemoveDocument(pp, "notepad", name); } catch (Exception ex) { DiagnosticLogger.Log("NOTEPAD_VECTOR_REMOVE_ERROR", ex.Message); } });
            }
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
