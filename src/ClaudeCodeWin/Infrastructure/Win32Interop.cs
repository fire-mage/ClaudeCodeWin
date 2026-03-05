using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeCodeWin.Infrastructure;

public static class Win32Interop
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;
    private const uint FLASHW_STOP = 0;

    public static void FlashWindow(Window window, uint count = 5)
    {
        var helper = new WindowInteropHelper(window);
        // Fix: handle can be IntPtr.Zero if window is not yet initialized
        if (helper.Handle == IntPtr.Zero) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = helper.Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = count,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    public static void StopFlash(Window window)
    {
        var helper = new WindowInteropHelper(window);
        // Fix: handle can be IntPtr.Zero if window is not yet initialized
        if (helper.Handle == IntPtr.Zero) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = helper.Handle,
            dwFlags = FLASHW_STOP,
            uCount = 0,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>
    /// Enable dark title bar for the given window. Call after SourceInitialized or in Loaded.
    /// </summary>
    public static void EnableDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
