using System.Text.Json;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface IOcrLayoutService
{
    Task<OcrLayoutSettings> LoadAsync(CancellationToken ct);
    Task<OcrLayoutSettings> SaveLocalAsync(OcrLayoutSettings layout, CancellationToken ct);

    // These return absolute desktop/screen zones by resolving the current game-window position.
    OcrZone? TryGetCityZone(OcrLayoutSettings layout);
    OcrZone? TryGetCoordinateZone(OcrLayoutSettings layout);

    // Layout boxes are stored as game-window-relative pixels.
    // This method converts them to absolute desktop/screen coordinates using the current game-window position.
    OcrZone? TryGetLayoutBoxZone(OcrLayoutBox? box, string fallbackName);
}

public sealed class OcrLayoutService : IOcrLayoutService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IConfiguration _configuration;
    private readonly IGameWindowLocator _windowLocator;
    private readonly ILogger<OcrLayoutService> _logger;

    public OcrLayoutService(
        IConfiguration configuration,
        IGameWindowLocator windowLocator,
        ILogger<OcrLayoutService> logger)
    {
        _configuration = configuration;
        _windowLocator = windowLocator;
        _logger = logger;
    }

    public async Task<OcrLayoutSettings> LoadAsync(CancellationToken ct)
    {
        var localPath = GetLocalLayoutPath();
        var defaultPath = GetDefaultLayoutPath();

        if (File.Exists(localPath))
            return Normalize(await LoadFromFileAsync(localPath, ct));

        if (File.Exists(defaultPath))
            return Normalize(await LoadFromFileAsync(defaultPath, ct));

        return Normalize(new OcrLayoutSettings());
    }

    public async Task<OcrLayoutSettings> SaveLocalAsync(
        OcrLayoutSettings layout,
        CancellationToken ct)
    {
        layout = Normalize(layout);
        layout.Version = Math.Max(1, layout.Version);

        var localPath = GetLocalLayoutPath();
        var folder = Path.GetDirectoryName(localPath);

        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var json = JsonSerializer.Serialize(layout, JsonOptions);
        await File.WriteAllTextAsync(localPath, json, ct);

        _logger.LogInformation(
            "Saved OCR layout to {LayoutPath}. Layout boxes are game-window-relative pixels.",
            localPath);

        return layout;
    }

    public OcrZone? TryGetCityZone(OcrLayoutSettings layout)
    {
        if (!layout.Enabled || !layout.UseLayoutForCity)
            return null;

        return TryGetLayoutBoxZone(layout.Zones.City, "City");
    }

    public OcrZone? TryGetCoordinateZone(OcrLayoutSettings layout)
    {
        if (!layout.Enabled || !layout.UseLayoutForCoordinate)
            return null;

        return TryGetLayoutBoxZone(layout.Zones.Coordinate, "Coordinate");
    }

    public OcrZone? TryGetLayoutBoxZone(OcrLayoutBox? box, string fallbackName)
    {
        if (box is not { IsValid: true })
            return null;

        var window = _windowLocator.FindWindow();

        if (window is null)
        {
            _logger.LogWarning(
                "Cannot resolve OCR layout box {BoxName} because the selected game window was not found.",
                string.IsNullOrWhiteSpace(box.Name) ? fallbackName : box.Name);

            return null;
        }

        return ConvertWindowRelativeBoxToAbsoluteZone(
            box,
            fallbackName,
            window);
    }

    private static OcrZone ConvertWindowRelativeBoxToAbsoluteZone(
        OcrLayoutBox box,
        string fallbackName,
        GameWindowInfo window)
    {
        var name = string.IsNullOrWhiteSpace(box.Name)
            ? fallbackName
            : box.Name.Trim();

        var width = Math.Max(1, box.Width);
        var height = Math.Max(1, box.Height);

        var left = window.Left + box.X;
        var top = window.Top + box.Y;

        return new OcrZone
        {
            Name = name,
            TopLeftX = left,
            TopLeftY = top,
            BottomRightX = left + width,
            BottomRightY = top + height,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private async Task<OcrLayoutSettings> LoadFromFileAsync(
        string path,
        CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var layout = JsonSerializer.Deserialize<OcrLayoutSettings>(json, JsonOptions);

            return layout ?? new OcrLayoutSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load OCR layout from {LayoutPath}. Falling back to empty layout.",
                path);

            return new OcrLayoutSettings();
        }
    }

    private static OcrLayoutSettings Normalize(OcrLayoutSettings? layout)
    {
        layout ??= new OcrLayoutSettings();

        layout.CoordinateMode = "window-relative-pixels";
        layout.Zones ??= new OcrBasicLayoutZones();
        layout.Price ??= new OcrPriceLayout();
        layout.Price.Rows ??= new List<OcrPriceRowLayout>();
        layout.Price.VisibleRows = Math.Max(1, Math.Min(20, layout.Price.VisibleRows));

        return layout;
    }

    private string GetDefaultLayoutPath()
    {
        var configured = _configuration.GetValue<string>(
            "OcrSettings:OcrLayoutDefaultPath");

        if (!string.IsNullOrWhiteSpace(configured))
            return ResolvePath(configured);

        return ResolvePath(Path.Combine("Data", "ocr-layout.default.json"));
    }

    private string GetLocalLayoutPath()
    {
        var configured = _configuration.GetValue<string>(
            "OcrSettings:OcrLayoutLocalPath");

        if (!string.IsNullOrWhiteSpace(configured))
            return ResolvePath(configured);

        return ResolvePath(Path.Combine("Data", "ocr-layout.local.json"));
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(Directory.GetCurrentDirectory(), path);
    }
}
