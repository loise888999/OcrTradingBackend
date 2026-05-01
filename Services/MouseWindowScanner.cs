using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OcrTradingBackend.Services;

public sealed record MouseWindowInfo(
    string ProcessName,
    int ProcessId,
    string Title,
    long Handle,
    int Left,
    int Top,
    int Width,
    int Height,
    int MouseX,
    int MouseY);

public static class MouseWindowScanner
{
    private const uint GA_ROOT = 2;

    public static MouseWindowInfo? GetWindowUnderMouse()
    {
        var mouse = System.Windows.Forms.Cursor.Position;
        var point = new POINT { X = mouse.X, Y = mouse.Y };

        var handle = WindowFromPoint(point);
        if (handle == IntPtr.Zero) return null;

        // If the mouse is over a child control, use the top/root window.
        var root = GetAncestor(handle, GA_ROOT);
        if (root != IntPtr.Zero) handle = root;

        return BuildInfo(handle, mouse.X, mouse.Y);
    }

    public static GameWindowInfo? ToGameWindowInfo(MouseWindowInfo? info)
    {
        if (info is null) return null;
        return new GameWindowInfo(
            new IntPtr(info.Handle),
            info.ProcessName,
            info.Title,
            info.Left,
            info.Top,
            info.Width,
            info.Height);
    }

    internal static GameWindowInfo? BuildGameWindowInfoFromHandle(IntPtr handle)
    {
        var info = BuildInfo(handle, 0, 0);
        return ToGameWindowInfo(info);
    }

    private static MouseWindowInfo? BuildInfo(IntPtr handle, int mouseX, int mouseY)
    {
        if (handle == IntPtr.Zero) return null;
        if (!GetWindowRect(handle, out var rect)) return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        GetWindowThreadProcessId(handle, out var processId);

        string processName;
        try
        {
            processName = Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            processName = "Unknown";
        }

        var title = GetWindowTitle(handle);

        return new MouseWindowInfo(
            processName,
            processId,
            title,
            handle.ToInt64(),
            rect.Left,
            rect.Top,
            width,
            height,
            mouseX,
            mouseY);
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
