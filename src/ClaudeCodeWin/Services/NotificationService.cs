using System.Media;
using System.Windows;
using System.Windows.Shell;
using ClaudeCodeWin.Infrastructure;

namespace ClaudeCodeWin.Services;

public class NotificationService
{
    private Window? _mainWindow;

    public void Initialize(Window mainWindow)
    {
        _mainWindow = mainWindow;

        _mainWindow.Activated += (_, _) =>
        {
            Win32Interop.StopFlash(_mainWindow);
            ClearOverlay();
        };
    }

    public void NotifyIfInactive()
    {
        if (_mainWindow is null) return;

        if (!_mainWindow.IsActive)
        {
            Win32Interop.FlashWindow(_mainWindow);
            SetCompletedOverlay();
            SystemSounds.Exclamation.Play();
        }
    }

    private void SetCompletedOverlay()
    {
        if (_mainWindow is null) return;

        _mainWindow.TaskbarItemInfo ??= new TaskbarItemInfo();
        _mainWindow.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
        _mainWindow.TaskbarItemInfo.ProgressValue = 1.0;
    }

    private void ClearOverlay()
    {
        if (_mainWindow?.TaskbarItemInfo is null) return;

        _mainWindow.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
        _mainWindow.TaskbarItemInfo.ProgressValue = 0;
    }
}
