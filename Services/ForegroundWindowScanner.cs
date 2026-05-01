using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OcrTradingBackend.Services;

public sealed record ForegroundWindowInfo(
    string ProcessName,
    int ProcessId,
    string Title,
    long Handle,
    int Left,
    int Top,
    int Width,
    int Height
);

public static class ForegroundWindowScanner
{
    public static ForegroundWindowInfo? GetForegroundWindowInfo()
    {
        var handle = GetForegroundWindow();

        if (handle == IntPtr.Zero)
            return null;

        if (!GetWindowRect(handle, out var rect))
            return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
            return null;

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

        return new ForegroundWindowInfo(
            processName,
            processId,
            title,
            handle.ToInt64(),
            rect.Left,
            rect.Top,
            width,
            height
        );
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);

        if (length <= 0)
            return string.Empty;

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);

        return builder.ToString();
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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}