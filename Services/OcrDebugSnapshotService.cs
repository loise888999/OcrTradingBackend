using System.Drawing.Imaging;

namespace OcrTradingBackend.Services;

public interface IOcrDebugSnapshotService
{
    Task<string?> SaveAsync(
        string kind,
        string source,
        Bitmap bitmap,
        string? rawText,
        CancellationToken ct);
}

public sealed class OcrDebugSnapshotService : IOcrDebugSnapshotService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<OcrDebugSnapshotService> _logger;

    public OcrDebugSnapshotService(
        IConfiguration configuration,
        ILogger<OcrDebugSnapshotService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> SaveAsync(
        string kind,
        string source,
        Bitmap bitmap,
        string? rawText,
        CancellationToken ct)
    {
        var enabled = _configuration.GetValue("OcrSettings:SaveDebugImages", false);
        if (!enabled)
            return null;

        try
        {
            var folder = _configuration.GetValue<string>("OcrSettings:DebugImageFolder");
            if (string.IsNullOrWhiteSpace(folder))
                folder = Path.Combine("Data", "debug-ocr");

            if (!Path.IsPathRooted(folder))
                folder = Path.Combine(AppContext.BaseDirectory, folder);

            var safeKind = SanitizeFileName(kind);
            var safeSource = SanitizeFileName(source);
            var targetFolder = Path.Combine(folder, safeKind);

            Directory.CreateDirectory(targetFolder);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var baseName = $"{stamp}_{safeSource}";

            var imagePath = Path.Combine(targetFolder, $"{baseName}.png");
            var textPath = Path.Combine(targetFolder, $"{baseName}.txt");

            bitmap.Save(imagePath, ImageFormat.Png);

            if (rawText is not null)
                await File.WriteAllTextAsync(textPath, rawText, ct);

            return Path.GetRelativePath(AppContext.BaseDirectory, imagePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save OCR debug snapshot for {Kind}/{Source}", kind, source);
            return null;
        }
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
}