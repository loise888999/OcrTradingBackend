using System.Text.Json;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface IOcrLayoutService
{
    Task<OcrLayoutSettings> LoadAsync(CancellationToken ct);
    Task<OcrLayoutSettings> SaveLocalAsync(OcrLayoutSettings layout, CancellationToken ct);
    OcrZone? TryGetCityZone(OcrLayoutSettings layout);
    OcrZone? TryGetCoordinateZone(OcrLayoutSettings layout);
}

public sealed class OcrLayoutService : IOcrLayoutService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<OcrLayoutService> _logger;

    public OcrLayoutService(
        IConfiguration configuration,
        ILogger<OcrLayoutService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<OcrLayoutSettings> LoadAsync(CancellationToken ct)
    {
        var localPath = GetLocalLayoutPath();
        var defaultPath = GetDefaultLayoutPath();

        if (File.Exists(localPath))
            return await LoadFromFileAsync(localPath, ct);

        if (File.Exists(defaultPath))
            return await LoadFromFileAsync(defaultPath, ct);

        return new OcrLayoutSettings();
    }

    public async Task<OcrLayoutSettings> SaveLocalAsync(
        OcrLayoutSettings layout,
        CancellationToken ct)
    {
        layout.Version = Math.Max(1, layout.Version);

        var localPath = GetLocalLayoutPath();
        var folder = Path.GetDirectoryName(localPath);

        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var json = JsonSerializer.Serialize(layout, JsonOptions);
        await File.WriteAllTextAsync(localPath, json, ct);

        _logger.LogInformation(
            "Saved OCR layout to {LayoutPath}",
            localPath);

        return layout;
    }

    public OcrZone? TryGetCityZone(OcrLayoutSettings layout)
    {
        if (!layout.Enabled || !layout.UseLayoutForCity)
            return null;

        return layout.Zones.City is { IsValid: true } box
            ? box.ToZone("City")
            : null;
    }

    public OcrZone? TryGetCoordinateZone(OcrLayoutSettings layout)
    {
        if (!layout.Enabled || !layout.UseLayoutForCoordinate)
            return null;

        return layout.Zones.Coordinate is { IsValid: true } box
            ? box.ToZone("Coordinate")
            : null;
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
