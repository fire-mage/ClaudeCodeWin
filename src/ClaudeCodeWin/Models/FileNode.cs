using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using ClaudeCodeWin.Infrastructure;

namespace ClaudeCodeWin.Models;

public class FileNode : ViewModelBase
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isLoaded;

    public FileNode(string name, string fullPath, bool isDirectory)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Extension = isDirectory ? "" : Path.GetExtension(name).ToLowerInvariant();

        if (isDirectory)
            Children.Add(new FileNode("Loading...", "", false) { IsPlaceholder = true });
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string Extension { get; }
    public bool IsPlaceholder { get; init; }

    public ObservableCollection<FileNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !_isLoaded && IsDirectory)
                LoadChildren();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Icon => IsDirectory
        ? (IsExpanded ? "\U0001F4C2" : "\U0001F4C1")
        : GetFileIcon();

    public Brush NameColor => IsDirectory ? FolderBrush : GetFileBrush();

    // File Explorer color scheme (VS Code inspired, frozen for perf)
    private static readonly SolidColorBrush FolderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xBD, 0x6F)));  // golden
    private static readonly SolidColorBrush DefaultFileBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private static readonly SolidColorBrush CSharpBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55)));      // green
    private static readonly SolidColorBrush XamlBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));        // blue
    private static readonly SolidColorBrush JsonBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78)));        // orange-brown
    private static readonly SolidColorBrush MarkdownBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));    // blue
    private static readonly SolidColorBrush WebBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0x4D, 0x26)));         // red-orange
    private static readonly SolidColorBrush ScriptBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)));      // teal
    private static readonly SolidColorBrush ConfigBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)));      // muted gray
    private static readonly SolidColorBrush ImageFileBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)));      // purple
    private static readonly SolidColorBrush PythonBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4B, 0x8B, 0xBE)));      // python blue
    private static readonly SolidColorBrush JsBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCB, 0xB1, 0x48)));          // JS yellow

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private Brush GetFileBrush() => Extension switch
    {
        ".cs" => CSharpBrush,
        ".xaml" => XamlBrush,
        ".json" => JsonBrush,
        ".xml" or ".csproj" or ".sln" or ".props" => ConfigBrush,
        ".md" => MarkdownBrush,
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".svg" or ".bmp" => ImageFileBrush,
        ".html" or ".htm" or ".css" => WebBrush,
        ".js" or ".ts" or ".jsx" or ".tsx" => JsBrush,
        ".py" => PythonBrush,
        ".bat" or ".cmd" or ".ps1" or ".sh" => ScriptBrush,
        ".yaml" or ".yml" => ConfigBrush,
        ".gitignore" or ".editorconfig" => ConfigBrush,
        _ => DefaultFileBrush
    };

    private string GetFileIcon() => Extension switch
    {
        ".cs" => "\u2660",      // C# — spade symbol (compact)
        ".xaml" => "\u2B25",    // XAML — diamond
        ".json" => "{ }",
        ".xml" or ".csproj" or ".sln" => "\u2B25",
        ".md" => "\u2263",      // Markdown
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".svg" or ".bmp" => "\U0001F5BC",
        ".exe" or ".dll" => "\u2699",
        ".bat" or ".cmd" or ".ps1" or ".sh" => "\u25B6",
        ".html" or ".htm" or ".css" => "\U0001F310",
        ".js" or ".ts" or ".jsx" or ".tsx" => "JS",
        ".py" => "\U0001F40D",
        ".java" => "\u2615",
        ".c" or ".cpp" or ".h" or ".hpp" => "C",
        ".yaml" or ".yml" => "\u2630",
        ".gitignore" or ".editorconfig" => "\u2699",
        _ => "\U0001F4C4"       // generic file
    };

    // Hidden folders/files that should not be shown
    private static readonly HashSet<string> HiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules",
        ".next", "__pycache__", ".cache", ".nuget", "packages",
        "TestResults", ".claude"
    };

    private static readonly HashSet<string> HiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".suo", ".user", ".cache"
    };

    public void LoadChildren()
    {
        if (_isLoaded || !IsDirectory || string.IsNullOrEmpty(FullPath)) return;
        _isLoaded = true;

        RunOnUI(() =>
        {
            Children.Clear();

            try
            {
                // Directories first, then files, both sorted alphabetically
                var dirInfo = new DirectoryInfo(FullPath);

                var dirs = dirInfo.GetDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden)
                                && !HiddenNames.Contains(d.Name))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var dir in dirs)
                    Children.Add(new FileNode(dir.Name, dir.FullName, true));

                var files = dirInfo.GetFiles()
                    .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden)
                                && !HiddenNames.Contains(f.Name)
                                && !HiddenExtensions.Contains(Path.GetExtension(f.Name)))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                    Children.Add(new FileNode(file.Name, file.FullName, false));
            }
            catch
            {
                // Access denied or other I/O error — leave empty
            }
        });

        OnPropertyChanged(nameof(Icon));
    }

    /// <summary>Forces re-loading of children next time the node is expanded.</summary>
    public void Invalidate()
    {
        _isLoaded = false;
        if (IsExpanded)
            LoadChildren();
    }
}
