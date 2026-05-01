using System.Drawing;
using System.Runtime.InteropServices;
using PaddleOCRSharp;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface IScreenCaptureService { Bitmap Capture(OcrZone zone); }

public sealed class WindowsScreenCaptureService : IScreenCaptureService
{
    public Bitmap Capture(OcrZone zone)
    {
        var left = Math.Min(zone.TopLeftX, zone.BottomRightX);
        var top = Math.Min(zone.TopLeftY, zone.BottomRightY);
        var width = Math.Abs(zone.BottomRightX - zone.TopLeftX);
        var height = Math.Abs(zone.BottomRightY - zone.TopLeftY);
        if (width <= 0 || height <= 0) throw new InvalidOperationException($"OCR zone '{zone.Name}' has invalid size.");
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);
        return bitmap;
    }
}

public interface IPaddleOcrService { string DetectText(Bitmap bitmap); }

internal static class NativeDllLoader
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
    public static void AddDllDirectory(string path) { if (Directory.Exists(path)) SetDllDirectory(path); }
}

public sealed class PaddleOcrSharpService : IPaddleOcrService, IDisposable
{
    private readonly PaddleOCREngine _engine;
    private readonly object _lock = new();

    public PaddleOcrSharpService()
    {
        var baseDir = AppContext.BaseDirectory;
        NativeDllLoader.AddDllDirectory(baseDir);
        Console.WriteLine($"PaddleOCR BaseDirectory: {baseDir}");
        OCRModelConfig? config = null;
        var parameter = new OCRParameter
        {
            cpu_math_library_num_threads = Math.Max(2, Environment.ProcessorCount / 2),
            enable_mkldnn = true,
            cls = false,
            det = true,
            use_angle_cls = false
        };
        _engine = new PaddleOCREngine(config, parameter);
    }

    public string DetectText(Bitmap bitmap)
    {
        lock (_lock)
        {
            var result = _engine.DetectText(bitmap);
            return result?.TextBlocks is null || result.TextBlocks.Count == 0
                ? string.Empty
                : string.Join("\n", result.TextBlocks.Select(x => x.Text));
        }
    }

    public void Dispose() => _engine.Dispose();
}

public sealed class FakePaddleOcrService : IPaddleOcrService
{
    public string DetectText(Bitmap bitmap) => string.Empty;
}
