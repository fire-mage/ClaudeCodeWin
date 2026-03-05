using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Views;

public partial class TaskOutputWindow : Window
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Brush _currentForeground = Brushes.White;
    private bool _isBold;
    private readonly StringBuilder _plainOutput = new();

    public event Action<string, string>? OnTaskCompleted; // taskName, plainTextOutput

    private static readonly Regex AnsiRegex = new(@"\x1b\[([0-9;]*)m", RegexOptions.Compiled);
    // Fix: was creating a new Regex on every AppendOutput call (hot path during stream processing)
    private static readonly Regex NonColorAnsiRegex = new(@"\x1b\[[^m]*[A-Za-z]", RegexOptions.Compiled);

    // Fix: SolidColorBrush was allocated per ANSI code change — pre-allocate and freeze for performance
    private static readonly Brush AnsiBlack = Freeze(new SolidColorBrush(Color.FromRgb(69, 71, 90)));
    private static readonly Brush AnsiRed = Freeze(new SolidColorBrush(Color.FromRgb(243, 139, 168)));
    private static readonly Brush AnsiGreen = Freeze(new SolidColorBrush(Color.FromRgb(166, 227, 161)));
    private static readonly Brush AnsiYellow = Freeze(new SolidColorBrush(Color.FromRgb(249, 226, 175)));
    private static readonly Brush AnsiBlue = Freeze(new SolidColorBrush(Color.FromRgb(137, 180, 250)));
    private static readonly Brush AnsiMagenta = Freeze(new SolidColorBrush(Color.FromRgb(203, 166, 247)));
    private static readonly Brush AnsiCyan = Freeze(new SolidColorBrush(Color.FromRgb(148, 226, 213)));
    private static readonly Brush AnsiBrightBlack = Freeze(new SolidColorBrush(Color.FromRgb(108, 112, 134)));
    private static readonly Brush AnsiBrightWhite = Freeze(new SolidColorBrush(Color.FromRgb(205, 214, 244)));
    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    // Fix: FindResource("TextBrush") was called on every ANSI reset/code 37/39 — cache it once
    private Brush _textBrush = Brushes.White;

    public TaskOutputWindow()
    {
        InitializeComponent();
        _textBrush = (Brush)FindResource("TextBrush");
        _currentForeground = _textBrush;
    }

    public async Task RunTaskAsync(TaskDefinition task, string? projectDir)
    {
        Title = task.Name;
        StatusText.Text = "Running...";

        _cts = new CancellationTokenSource();

        var workingDir = task.WorkingDirectory;
        if (!string.IsNullOrEmpty(workingDir) && !System.IO.Path.IsPathRooted(workingDir) && !string.IsNullOrEmpty(projectDir))
            workingDir = System.IO.Path.Combine(projectDir, workingDir);
        else if (string.IsNullOrEmpty(workingDir))
            workingDir = projectDir;

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {task.Command}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Enable virtual terminal processing for ANSI support
        startInfo.Environment["TERM"] = "xterm-256color";

        if (!string.IsNullOrEmpty(workingDir))
            startInfo.WorkingDirectory = workingDir;

        try
        {
            _process = Process.Start(startInfo);
            if (_process is null)
            {
                AppendOutput("Failed to start process.\n");
                SetStatus(false);
                return;
            }

            var stdoutTask = ReadStreamAsync(_process.StandardOutput, _cts.Token);
            var stderrTask = ReadStreamAsync(_process.StandardError, _cts.Token);

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await _process.WaitForExitAsync(_cts.Token).ConfigureAwait(false);

            var exitCode = _process.ExitCode;
            Dispatcher.Invoke(() =>
            {
                AppendOutput($"\nProcess exited with code {exitCode}.\n");
                SetStatus(exitCode == 0);
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Invoke(() =>
            {
                AppendOutput("\nTask cancelled.\n");
                SetStatus(false);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                AppendOutput($"\nError: {ex.Message}\n");
                SetStatus(false);
            });
        }
        finally
        {
            // Fix #3: capture local refs and dispose on background thread directly.
            // Process.Dispose() and CTS.Dispose() are thread-safe.
            // Previous approach (BeginInvoke) could be silently dropped if Dispatcher
            // is shut down, leaking the process handle.
            var proc = _process;
            var cts = _cts;
            _process = null;
            _cts = null;
            proc?.Dispose();
            cts?.Dispose();
        }
    }

    private async Task ReadStreamAsync(System.IO.StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[256];
        while (!ct.IsCancellationRequested)
        {
            var count = await reader.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;

            var text = new string(buffer, 0, count);
            Dispatcher.Invoke(() => AppendOutput(text));
        }
    }

    private void AppendOutput(string text)
    {
        // Parse ANSI escape codes and append colored runs
        var lastIndex = 0;
        foreach (Match match in AnsiRegex.Matches(text))
        {
            // Append text before this escape code
            if (match.Index > lastIndex)
            {
                var plainText = text[lastIndex..match.Index];
                AppendRun(plainText);
            }

            // Process the ANSI code
            var codes = match.Groups[1].Value;
            ProcessAnsiCodes(codes);

            lastIndex = match.Index + match.Length;
        }

        // Append remaining text after last escape code
        if (lastIndex < text.Length)
        {
            var remaining = text[lastIndex..];
            // Strip any other escape sequences (like \x1b[K, \x1b[2J, etc.)
            remaining = NonColorAnsiRegex.Replace(remaining, "");
            if (remaining.Length > 0)
                AppendRun(remaining);
        }

        OutputRichTextBox.ScrollToEnd();
    }

    private void AppendRun(string text)
    {
        var run = new Run(text)
        {
            Foreground = _currentForeground,
            FontWeight = _isBold ? FontWeights.Bold : FontWeights.Normal
        };
        OutputParagraph.Inlines.Add(run);
        _plainOutput.Append(text);
    }

    private void ProcessAnsiCodes(string codes)
    {
        if (string.IsNullOrEmpty(codes))
        {
            ResetAnsiState();
            return;
        }

        foreach (var part in codes.Split(';'))
        {
            if (!int.TryParse(part, out var code))
                continue;

            switch (code)
            {
                case 0: ResetAnsiState(); break;
                case 1: _isBold = true; break;
                case 22: _isBold = false; break;
                // Standard foreground colors (use pre-allocated frozen brushes)
                case 30: _currentForeground = AnsiBlack; break;
                case 31: _currentForeground = AnsiRed; break;
                case 32: _currentForeground = AnsiGreen; break;
                case 33: _currentForeground = AnsiYellow; break;
                case 34: _currentForeground = AnsiBlue; break;
                case 35: _currentForeground = AnsiMagenta; break;
                case 36: _currentForeground = AnsiCyan; break;
                case 37: _currentForeground = _textBrush; break;
                case 39: _currentForeground = _textBrush; break;
                // Bright foreground colors
                case 90: _currentForeground = AnsiBrightBlack; break;
                case 91: _currentForeground = AnsiRed; break;
                case 92: _currentForeground = AnsiGreen; break;
                case 93: _currentForeground = AnsiYellow; break;
                case 94: _currentForeground = AnsiBlue; break;
                case 95: _currentForeground = AnsiMagenta; break;
                case 96: _currentForeground = AnsiCyan; break;
                case 97: _currentForeground = AnsiBrightWhite; break;
            }
        }
    }

    private void ResetAnsiState()
    {
        _currentForeground = _textBrush;
        _isBold = false;
    }

    private void SetStatus(bool success)
    {
        if (success)
        {
            StatusIcon.Text = "\u2714";
            StatusIcon.Foreground = (Brush)FindResource("SuccessBrush");
            StatusText.Text = "Completed";
        }
        else
        {
            StatusIcon.Text = "\u2718";
            StatusIcon.Foreground = (Brush)FindResource("ErrorBrush");
            StatusText.Text = "Failed";
        }

        // Fire completion event with full plain text output
        OnTaskCompleted?.Invoke(Title, _plainOutput.ToString());
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_process is not null && !_process.HasExited)
        {
            var result = MessageBox.Show(
                "Task is still running. Kill it?",
                "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _cts?.Cancel();
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            else
            {
                return;
            }
        }

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_process is not null && !_process.HasExited)
        {
            _cts?.Cancel();
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        base.OnClosed(e);
    }
}
