using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Views;

public partial class TaskOutputWindow : Window
{
    private Process? _process;
    private CancellationTokenSource? _cts;

    public TaskOutputWindow()
    {
        InitializeComponent();
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
            _process?.Dispose();
            _process = null;
            _cts?.Dispose();
            _cts = null;
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
        OutputTextBox.AppendText(text);
        OutputTextBox.ScrollToEnd();
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
