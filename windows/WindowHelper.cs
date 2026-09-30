using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Brink;

public static class WindowHelper
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    /// Never steal focus, never show in Alt-Tab — the WPF equivalent of the
    /// macOS non-activating panel.
    public static void MakeUnfocusablePanel(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// Cursor position relative to the visual, converted from physical screen pixels.
    public static Point CursorDip(Visual reference)
    {
        if (!GetCursorPos(out var p)) return new Point(double.NaN, double.NaN);
        return reference.PointFromScreen(new Point(p.X, p.Y));
    }

    public static bool IsCursorOver(Window window) => window.IsVisible
        && new Rect(0, 0, window.ActualWidth, window.ActualHeight).Contains(CursorDip(window));
}
