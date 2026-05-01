using Microsoft.Extensions.Options;
using OcrTradingBackend.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OcrTradingBackend.Services;

public sealed class GameWindowSettings
{
    public string ProcessName { get; set; } = "";
    public string[] ProcessNames { get; set; } = Array.Empty<string>();
    public string? TitleContains { get; set; }
    public bool IncludeMinimized { get; set; } = false;

    // When true, the backend first uses the window selected with
    // /api/system/select-window-under-mouse-delayed before falling back to process/title search.
    public bool PreferMouseSelectedWindow { get; set; } = true;
}

public sealed record GameWindowInfo(
    IntPtr Handle,
    string ProcessName,
    string Title,
    int Left,
    int Top,
    int Width,
    int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

public interface IGameWindowLocator
{
    GameWindowInfo? FindWindow();
}

public static class GameWindowSelectionStore
{
    private static readonly object Gate = new();
    private static GameWindowInfo? _selected;

    public static void Set(GameWindowInfo window)
    {
        lock (Gate)
        {
            _selected = window;
        }
    }

    public static GameWindowInfo? Get()
    {
        lock (Gate)
        {
            return _selected;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _selected = null;
        }
    }
}

public sealed class GameWindowLocatorService : IGameWindowLocator
{
    private readonly IOptionsMonitor<GameWindowSettings> _settings;
    private readonly ILogger<GameWindowLocatorService> _logger;

    public GameWindowLocatorService(IOptionsMonitor<GameWindowSettings> settings, ILogger<GameWindowLocatorService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public GameWindowInfo? FindWindow()
    {
        var settings = _settings.CurrentValue;

        if (settings.PreferMouseSelectedWindow)
        {
            var selected = GameWindowSelectionStore.Get();
            var resolvedSelected = ResolveSelectedWindow(selected);
            if (resolvedSelected is not null)
                return resolvedSelected;
        }

        var processNames = BuildProcessNameList(settings);
        if (processNames.Count == 0 && string.IsNullOrWhiteSpace(settings.TitleContains))
        {
            _logger.LogWarning("GameWindow settings are empty and no mouse-selected window is active.");
            return null;
        }

        var windows = GetVisibleWindows(settings.IncludeMinimized);

        var candidates = windows
            .Where(window => MatchesProcessName(window, processNames))
            .Where(window => MatchesTitle(window, settings.TitleContains))
            .OrderByDescending(window => window.Width * window.Height)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static GameWindowInfo? ResolveSelectedWindow(GameWindowInfo? selected)
    {
        if (selected is null) return null;

        var handle = selected.Handle;
        if (handle == IntPtr.Zero) return null;
        if (!IsWindow(handle)) return null;
        if (!IsWindowVisible(handle)) return null;

        // Re-read the rectangle every cycle so moving the window is detected.
        return MouseWindowScanner.BuildGameWindowInfoFromHandle(handle);
    }

    private static List<string> BuildProcessNameList(GameWindowSettings settings)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.ProcessName)) names.Add(settings.ProcessName);
        if (settings.ProcessNames is not null) names.AddRange(settings.ProcessNames.Where(x => !string.IsNullOrWhiteSpace(x)));

        return names
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesProcessName(GameWindowInfo window, IReadOnlyList<string> processNames)
    {
        if (processNames.Count == 0) return true;
        return processNames.Any(name => string.Equals(window.ProcessName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesTitle(GameWindowInfo window, string? titleContains)
    {
        if (string.IsNullOrWhiteSpace(titleContains)) return true;
        return window.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<GameWindowInfo> GetVisibleWindows(bool includeMinimized)
    {
        var result = new List<GameWindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (!includeMinimized && IsIconic(hWnd)) return true;

            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (!GetWindowRect(hWnd, out var rect)) return true;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return true;

            GetWindowThreadProcessId(hWnd, out var processId);

            string processName;
            try
            {
                processName = Process.GetProcessById(processId).ProcessName;
            }
            catch
            {
                processName = "Unknown";
            }

            result.Add(new GameWindowInfo(hWnd, processName, title, rect.Left, rect.Top, width, height));
            return true;
        }, IntPtr.Zero);

        return result;
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
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}



public static class GameWindowResponseMapper
{
    public static GameWindowResponse ToResponse(GameWindowInfo window)
    {
        return new GameWindowResponse(
            window.Handle.ToInt64(),
            window.ProcessName,
            window.Title,
            window.Left,
            window.Top,
            window.Width,
            window.Height
        );
    }
}
