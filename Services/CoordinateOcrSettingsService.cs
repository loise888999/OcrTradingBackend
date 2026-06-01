using Microsoft.Extensions.Options;
using OcrTradingBackend.Models;
using System.Text.Json;

namespace OcrTradingBackend.Services;

public interface ICoordinateOcrSettingsService
{
    CoordinateOcrSettingsResponse Get();
    CoordinateOcrSettingsResponse GetEffective(OcrRuntimeSettings defaults);
    Task<CoordinateOcrSettingsResponse> UpdateAsync(UpdateCoordinateOcrSettingsRequest request, CancellationToken ct);
}

public sealed class CoordinateOcrSettingsService : ICoordinateOcrSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IOptionsMonitor<OcrRuntimeSettings> _defaults;
    private readonly string _path;
    private readonly object _gate = new();

    public CoordinateOcrSettingsService(
        IOptionsMonitor<OcrRuntimeSettings> defaults,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _defaults = defaults;

        var configured = configuration.GetValue<string>("OcrSettings:CoordinateOcrSettingsPath");
        _path = ResolvePath(
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine("Data", "coordinate-ocr-settings.json")
                : configured,
            environment.ContentRootPath);
    }

    public CoordinateOcrSettingsResponse Get()
        => GetEffective(_defaults.CurrentValue);

    public CoordinateOcrSettingsResponse GetEffective(OcrRuntimeSettings defaults)
    {
        var saved = LoadSaved();

        return Normalize(new CoordinateOcrSettingsResponse(
            CoordinateReadMode: saved?.CoordinateReadMode ?? defaults.CoordinateReadMode,
            CoordinateTemplateFallbackToNormalOcr: saved?.CoordinateTemplateFallbackToNormalOcr ?? defaults.CoordinateTemplateFallbackToNormalOcr,
            CoordinateTemplateCountFailedReadsForRecalibration: saved?.CoordinateTemplateCountFailedReadsForRecalibration ?? defaults.CoordinateTemplateCountFailedReadsForRecalibration,
            CoordinateTemplateRecalibrationFailureLimit: saved?.CoordinateTemplateRecalibrationFailureLimit ?? defaults.CoordinateTemplateRecalibrationFailureLimit,
            CoordinateTemplateRequireVisibleTextForFailure: saved?.CoordinateTemplateRequireVisibleTextForFailure ?? defaults.CoordinateTemplateRequireVisibleTextForFailure,
            CoordinateTemplateMinTextPixelsPercent: saved?.CoordinateTemplateMinTextPixelsPercent ?? defaults.CoordinateTemplateMinTextPixelsPercent,
            CoordinateTemplateMinContrast: saved?.CoordinateTemplateMinContrast ?? defaults.CoordinateTemplateMinContrast,
            CoordinateTemplateAutoProfileEnabled: saved?.CoordinateTemplateAutoProfileEnabled ?? defaults.CoordinateTemplateAutoProfileEnabled,
            CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode: saved?.CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode ?? defaults.CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode,
            CoordinateTemplateAutoProfileMaxSamples: saved?.CoordinateTemplateAutoProfileMaxSamples ?? defaults.CoordinateTemplateAutoProfileMaxSamples,
            CoordinateTemplateAutoProfileValidationMaxDigitScore: saved?.CoordinateTemplateAutoProfileValidationMaxDigitScore ?? defaults.CoordinateTemplateAutoProfileValidationMaxDigitScore,
            CoordinateTemplateMaxTemplatesPerDigit: saved?.CoordinateTemplateMaxTemplatesPerDigit ?? defaults.CoordinateTemplateMaxTemplatesPerDigit,
            CoordinateTemplateRequirePerDigitOcrValidation: saved?.CoordinateTemplateRequirePerDigitOcrValidation ?? defaults.CoordinateTemplateRequirePerDigitOcrValidation,
            CoordinateTemplateDebugPrintDigitBitmaps: saved?.CoordinateTemplateDebugPrintDigitBitmaps ?? defaults.CoordinateTemplateDebugPrintDigitBitmaps,
            CoordinateTemplateNormalizeDigitPaddingEnabled: saved?.CoordinateTemplateNormalizeDigitPaddingEnabled ?? defaults.CoordinateTemplateNormalizeDigitPaddingEnabled,
            CoordinateTemplateDigitHorizontalPaddingPixels: saved?.CoordinateTemplateDigitHorizontalPaddingPixels ?? defaults.CoordinateTemplateDigitHorizontalPaddingPixels,
            CoordinateTemplateDigitVerticalPaddingPixels: saved?.CoordinateTemplateDigitVerticalPaddingPixels ?? defaults.CoordinateTemplateDigitVerticalPaddingPixels,
            CoordinateTemplateFastModeSpeedMultiplier: saved?.CoordinateTemplateFastModeSpeedMultiplier ?? defaults.CoordinateTemplateFastModeSpeedMultiplier,
            CoordinateTemplateBrightnessThreshold: saved?.CoordinateTemplateBrightnessThreshold ?? defaults.CoordinateTemplateBrightnessThreshold));
    }

    public async Task<CoordinateOcrSettingsResponse> UpdateAsync(
        UpdateCoordinateOcrSettingsRequest request,
        CancellationToken ct)
    {
        var current = Get();

        var updated = Normalize(new CoordinateOcrSettingsResponse(
            CoordinateReadMode: request.CoordinateReadMode ?? current.CoordinateReadMode,
            CoordinateTemplateFallbackToNormalOcr: request.CoordinateTemplateFallbackToNormalOcr ?? current.CoordinateTemplateFallbackToNormalOcr,
            CoordinateTemplateCountFailedReadsForRecalibration: request.CoordinateTemplateCountFailedReadsForRecalibration ?? current.CoordinateTemplateCountFailedReadsForRecalibration,
            CoordinateTemplateRecalibrationFailureLimit: request.CoordinateTemplateRecalibrationFailureLimit ?? current.CoordinateTemplateRecalibrationFailureLimit,
            CoordinateTemplateRequireVisibleTextForFailure: request.CoordinateTemplateRequireVisibleTextForFailure ?? current.CoordinateTemplateRequireVisibleTextForFailure,
            CoordinateTemplateMinTextPixelsPercent: request.CoordinateTemplateMinTextPixelsPercent ?? current.CoordinateTemplateMinTextPixelsPercent,
            CoordinateTemplateMinContrast: request.CoordinateTemplateMinContrast ?? current.CoordinateTemplateMinContrast,
            CoordinateTemplateAutoProfileEnabled: request.CoordinateTemplateAutoProfileEnabled ?? current.CoordinateTemplateAutoProfileEnabled,
            CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode: request.CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode ?? current.CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode,
            CoordinateTemplateAutoProfileMaxSamples: request.CoordinateTemplateAutoProfileMaxSamples ?? current.CoordinateTemplateAutoProfileMaxSamples,
            CoordinateTemplateAutoProfileValidationMaxDigitScore: request.CoordinateTemplateAutoProfileValidationMaxDigitScore ?? current.CoordinateTemplateAutoProfileValidationMaxDigitScore,
            CoordinateTemplateMaxTemplatesPerDigit: request.CoordinateTemplateMaxTemplatesPerDigit ?? current.CoordinateTemplateMaxTemplatesPerDigit,
            CoordinateTemplateRequirePerDigitOcrValidation: request.CoordinateTemplateRequirePerDigitOcrValidation ?? current.CoordinateTemplateRequirePerDigitOcrValidation,
            CoordinateTemplateDebugPrintDigitBitmaps: request.CoordinateTemplateDebugPrintDigitBitmaps ?? current.CoordinateTemplateDebugPrintDigitBitmaps,
            CoordinateTemplateNormalizeDigitPaddingEnabled: request.CoordinateTemplateNormalizeDigitPaddingEnabled ?? current.CoordinateTemplateNormalizeDigitPaddingEnabled,
            CoordinateTemplateDigitHorizontalPaddingPixels: request.CoordinateTemplateDigitHorizontalPaddingPixels ?? current.CoordinateTemplateDigitHorizontalPaddingPixels,
            CoordinateTemplateDigitVerticalPaddingPixels: request.CoordinateTemplateDigitVerticalPaddingPixels ?? current.CoordinateTemplateDigitVerticalPaddingPixels,
            CoordinateTemplateFastModeSpeedMultiplier: request.CoordinateTemplateFastModeSpeedMultiplier ?? current.CoordinateTemplateFastModeSpeedMultiplier,
            CoordinateTemplateBrightnessThreshold: request.CoordinateTemplateBrightnessThreshold ?? current.CoordinateTemplateBrightnessThreshold));

        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var tempPath = $"{_path}.tmp";
        var json = JsonSerializer.Serialize(updated, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, _path, overwrite: true);

        return updated;
    }

    private CoordinateOcrSettingsResponse? LoadSaved()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    return null;

                var json = File.ReadAllText(_path);
                var saved = JsonSerializer.Deserialize<CoordinateOcrSettingsResponse>(
                    json,
                    JsonOptions);

                if (saved is null)
                    return null;

                if (!HasProperty(json, nameof(CoordinateOcrSettingsResponse.CoordinateTemplateBrightnessThreshold)))
                {
                    saved = saved with
                    {
                        CoordinateTemplateBrightnessThreshold = _defaults.CurrentValue.CoordinateTemplateBrightnessThreshold
                    };
                }

                return Normalize(saved);
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool HasProperty(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateObject()
                .Any(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static CoordinateOcrSettingsResponse Normalize(CoordinateOcrSettingsResponse settings)
        => settings with
        {
            CoordinateReadMode = CoordinateOcrModes.Normalize(settings.CoordinateReadMode),
            CoordinateTemplateRecalibrationFailureLimit = Math.Clamp(settings.CoordinateTemplateRecalibrationFailureLimit, 1, 100),
            CoordinateTemplateMinTextPixelsPercent = Math.Clamp(settings.CoordinateTemplateMinTextPixelsPercent, 0, 100),
            CoordinateTemplateMinContrast = Math.Clamp(settings.CoordinateTemplateMinContrast, 0, 255),
            CoordinateTemplateAutoProfileMaxSamples = Math.Clamp(settings.CoordinateTemplateAutoProfileMaxSamples, 1, 10_000),
            CoordinateTemplateAutoProfileValidationMaxDigitScore = Math.Clamp(settings.CoordinateTemplateAutoProfileValidationMaxDigitScore, 0, 1),
            CoordinateTemplateMaxTemplatesPerDigit = Math.Clamp(settings.CoordinateTemplateMaxTemplatesPerDigit, 1, 100),
            CoordinateTemplateFastModeSpeedMultiplier = Math.Clamp(settings.CoordinateTemplateFastModeSpeedMultiplier, 1, 50),
            CoordinateTemplateBrightnessThreshold = Math.Clamp(settings.CoordinateTemplateBrightnessThreshold, 0, 255),
            CoordinateTemplateDigitHorizontalPaddingPixels = Math.Clamp(settings.CoordinateTemplateDigitHorizontalPaddingPixels, 0, 3),
            CoordinateTemplateDigitVerticalPaddingPixels = Math.Clamp(settings.CoordinateTemplateDigitVerticalPaddingPixels, 0, 3)
        };

    private static string ResolvePath(string path, string root)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
}
