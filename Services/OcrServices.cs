using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
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

public enum OcrFieldKind
{
    General,
    City,
    Coordinate,
    PriceMenu,
    PriceItemName,
    PriceNumber,
    PriceMultiplier
}

public interface IPaddleOcrService
{
    string DetectText(Bitmap bitmap, OcrFieldKind fieldKind = OcrFieldKind.General);
}

internal static class NativeDllLoader
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
    public static void AddDllDirectory(string path) { if (Directory.Exists(path)) SetDllDirectory(path); }
}

public sealed class PaddleOcrSharpService : IPaddleOcrService, IDisposable
{
    private readonly PaddleOCREngine _engine;
    private readonly IOptionsMonitor<OcrRuntimeSettings> _settings;
    private readonly object _lock = new();

    public PaddleOcrSharpService(
        IOptionsMonitor<OcrRuntimeSettings> settings,
        ILogger<PaddleOcrSharpService> logger)
    {
        _settings = settings;
        var ocrSettings = settings.CurrentValue;
        var baseDir = AppContext.BaseDirectory;
        NativeDllLoader.AddDllDirectory(baseDir);
        Console.WriteLine($"PaddleOCR BaseDirectory: {baseDir}");

        var modelConfig = TryBuildEnglishModelConfig(
            ocrSettings,
            baseDir,
            logger,
            out var usingEnglishModel);

        var hasClassifier = modelConfig is not null &&
                            !string.IsNullOrWhiteSpace(modelConfig.cls_infer);

        var parameter = new OCRParameter
        {
            cpu_math_library_num_threads = Math.Max(2, Environment.ProcessorCount / 2),
            enable_mkldnn = true,
            cls = hasClassifier,
            det = true,
            use_angle_cls = hasClassifier
        };
        _engine = new PaddleOCREngine(modelConfig, parameter);

        if (usingEnglishModel)
        {
            logger.LogInformation(
                "Using English OCR recognition model. RecognitionModelPath={RecognitionModelPath}; DictionaryPath={DictionaryPath}; DetectionModelPath={DetectionModelPath}",
                modelConfig!.rec_infer,
                modelConfig.keys,
                modelConfig.det_infer);
        }
        else
        {
            logger.LogInformation("Using bundled PaddleOCRSharp default OCR model.");
        }
    }

    public string DetectText(Bitmap bitmap, OcrFieldKind fieldKind = OcrFieldKind.General)
    {
        string text;

        lock (_lock)
        {
            var result = _engine.DetectText(bitmap);
            text = result?.TextBlocks is null || result.TextBlocks.Count == 0
                ? string.Empty
                : string.Join("\n", result.TextBlocks.Select(x => x.Text));
        }

        return OcrFieldTextFilter.Filter(text, fieldKind, _settings.CurrentValue);
    }

    public void Dispose() => _engine.Dispose();

    private static OCRModelConfig? TryBuildEnglishModelConfig(
        OcrRuntimeSettings settings,
        string baseDir,
        ILogger logger,
        out bool usingEnglishModel)
    {
        usingEnglishModel = false;

        if (!settings.UseEnglishModels)
            return null;

        var recognitionPath = ResolveExistingModelDirectory(settings.RecognitionModelPath, baseDir);
        var dictionaryPath = ResolveExistingFile(settings.DictionaryPath, baseDir);

        if (recognitionPath is null || dictionaryPath is null)
        {
            var message =
                "English OCR was requested, but the English recognition model or dictionary was not found. " +
                $"RecognitionModelPath='{settings.RecognitionModelPath}'; DictionaryPath='{settings.DictionaryPath}'.";

            if (!settings.FallbackToBundledModel)
                throw new InvalidOperationException(message);

            logger.LogWarning("{Message} Falling back to bundled PaddleOCRSharp default model.", message);
            return null;
        }

        var detectionPath = ResolveExistingModelDirectory(settings.DetectionModelPath, baseDir);
        if (detectionPath is null)
        {
            detectionPath = ResolveBundledModelDirectory(baseDir, "yt_PP-OCRv5_mobile_det_infer");
            if (detectionPath is null)
            {
                var message =
                    "English OCR recognition files were found, but no detection model was configured and the bundled PaddleOCRSharp detector was not found.";

                if (!settings.FallbackToBundledModel)
                    throw new InvalidOperationException(message);

                logger.LogWarning("{Message} Falling back to bundled PaddleOCRSharp default model.", message);
                return null;
            }
        }

        var classifierPath = ResolveExistingModelDirectory(settings.ClassifierModelPath, baseDir);
        if (!string.IsNullOrWhiteSpace(settings.ClassifierModelPath) &&
            classifierPath is null)
        {
            logger.LogWarning(
                "English OCR classifier path was configured but not found. ClassifierModelPath={ClassifierModelPath}. Angle classification will stay disabled.",
                settings.ClassifierModelPath);
        }

        usingEnglishModel = true;

        return new OCRModelConfig
        {
            det_infer = detectionPath,
            cls_infer = classifierPath ?? string.Empty,
            rec_infer = recognitionPath,
            keys = dictionaryPath
        };
    }

    private static string? ResolveExistingModelDirectory(
        string? configuredPath,
        string baseDir)
    {
        return EnglishOcrModelPathResolver
            .ResolvePathCandidates(configuredPath, baseDir)
            .FirstOrDefault(IsCompletePaddleModelDirectory);
    }

    private static string? ResolveExistingFile(
        string? configuredPath,
        string baseDir)
    {
        var resolved = EnglishOcrModelPathResolver.ResolvePath(configuredPath, baseDir);
        return resolved is not null && File.Exists(resolved) ? resolved : null;
    }

    private static bool IsCompletePaddleModelDirectory(string path)
    {
        return Directory.Exists(path) &&
               File.Exists(Path.Combine(path, "inference.json")) &&
               File.Exists(Path.Combine(path, "inference.pdiparams")) &&
               File.Exists(Path.Combine(path, "inference.yml"));
    }

    private static string? ResolveBundledModelDirectory(
        string baseDir,
        string modelFolderName)
    {
        var candidates = new[]
        {
            Path.Combine(baseDir, "inference", modelFolderName),
            Path.Combine(Directory.GetCurrentDirectory(), "inference", modelFolderName)
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(Directory.Exists);
    }
}

public static class OcrFieldTextFilter
{
    public static string Filter(
        string text,
        OcrFieldKind fieldKind,
        OcrRuntimeSettings settings)
    {
        if (string.IsNullOrEmpty(text) ||
            !settings.OcrAllowedCharFilteringEnabled)
        {
            return text;
        }

        var allowedChars = fieldKind switch
        {
            OcrFieldKind.Coordinate => settings.CoordinateOcrAllowedChars,
            OcrFieldKind.PriceMenu => settings.PriceMenuOcrAllowedChars,
            OcrFieldKind.PriceNumber => settings.PriceNumberOcrAllowedChars,
            OcrFieldKind.PriceMultiplier => settings.PriceMultiplierOcrAllowedChars,
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(allowedChars))
            return text;

        var allowed = allowedChars.ToHashSet();
        var filtered = new string(text
            .Where(c => allowed.Contains(c))
            .ToArray());

        return NormalizeFilteredWhitespace(filtered);
    }

    private static string NormalizeFilteredWhitespace(string text)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join("\n", lines);
    }
}

public sealed class OcrRuntimeSettingsValidator : IValidateOptions<OcrRuntimeSettings>
{
    public ValidateOptionsResult Validate(string? name, OcrRuntimeSettings settings)
    {
        var failures = new List<string>();

        if (!IsValidTextPresenceGateMode(settings.OcrTextPresenceGateMode))
        {
            failures.Add(
                "OcrSettings:OcrTextPresenceGateMode must be one of: Off, BeforePreprocess, AfterPreprocess, BeforeAndAfter.");
        }

        if (!CoordinateOcrModes.IsValid(settings.CoordinateReadMode))
        {
            failures.Add(
                "OcrSettings:CoordinateReadMode must be one of: NormalOcr, FastTemplate.");
        }

        if (!PriceTradeTypeReadModes.IsValid(settings.PriceTradeTypeReadMode))
        {
            failures.Add(
                "OcrSettings:PriceTradeTypeReadMode must be one of: NormalOcr, FastTemplate.");
        }

        if (settings.CoordinateTemplateRecalibrationFailureLimit < 1)
        {
            failures.Add(
                "OcrSettings:CoordinateTemplateRecalibrationFailureLimit must be at least 1.");
        }

        if (settings.CoordinateTemplateAutoProfileMaxSamples < 1)
        {
            failures.Add(
                "OcrSettings:CoordinateTemplateAutoProfileMaxSamples must be at least 1.");
        }

        if (settings.CoordinateTemplateAutoProfileValidationMaxDigitScore is < 0 or > 1)
        {
            failures.Add(
                "OcrSettings:CoordinateTemplateAutoProfileValidationMaxDigitScore must be between 0 and 1.");
        }

        if (settings.CoordinateTemplateMaxTemplatesPerDigit < 1)
        {
            failures.Add(
                "OcrSettings:CoordinateTemplateMaxTemplatesPerDigit must be at least 1.");
        }

        if (settings.PriceTradeTypeTemplateMaxTemplatesPerType < 1)
        {
            failures.Add(
                "OcrSettings:PriceTradeTypeTemplateMaxTemplatesPerType must be at least 1.");
        }

        if (settings.PriceTradeTypeTemplateMaxScore is < 0 or > 1)
        {
            failures.Add(
                "OcrSettings:PriceTradeTypeTemplateMaxScore must be between 0 and 1.");
        }

        if (settings.PriceTradeTypeTemplateRecalibrationFailureLimit < 1)
        {
            failures.Add(
                "OcrSettings:PriceTradeTypeTemplateRecalibrationFailureLimit must be at least 1.");
        }

        if (settings.PriceTradeTypeTemplateProbeIntervalMs < 25)
        {
            failures.Add(
                "OcrSettings:PriceTradeTypeTemplateProbeIntervalMs must be at least 25.");
        }

        if (!settings.UseEnglishModels || settings.FallbackToBundledModel)
        {
            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        var baseDir = AppContext.BaseDirectory;

        RequireModelDirectory(
            failures,
            "OcrSettings:RecognitionModelPath",
            settings.RecognitionModelPath,
            baseDir);

        RequireFile(
            failures,
            "OcrSettings:DictionaryPath",
            settings.DictionaryPath,
            baseDir);

        if (!string.IsNullOrWhiteSpace(settings.ClassifierModelPath))
        {
            RequireModelDirectory(
                failures,
                "OcrSettings:ClassifierModelPath",
                settings.ClassifierModelPath,
                baseDir);
        }

        if (!string.IsNullOrWhiteSpace(settings.DetectionModelPath))
        {
            RequireModelDirectory(
                failures,
                "OcrSettings:DetectionModelPath",
                settings.DetectionModelPath,
                baseDir);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsValidTextPresenceGateMode(string? mode)
    {
        return mode is not null &&
               (mode.Equals("Off", StringComparison.OrdinalIgnoreCase) ||
                mode.Equals("BeforePreprocess", StringComparison.OrdinalIgnoreCase) ||
                mode.Equals("AfterPreprocess", StringComparison.OrdinalIgnoreCase) ||
                mode.Equals("BeforeAndAfter", StringComparison.OrdinalIgnoreCase));
    }

    private static void RequireModelDirectory(
        List<string> failures,
        string settingName,
        string? configuredPath,
        string baseDir)
    {
        var resolved = EnglishOcrModelPathResolver
            .ResolvePathCandidates(configuredPath, baseDir)
            .FirstOrDefault(IsCompletePaddleModelDirectory);

        if (resolved is null)
        {
            failures.Add(
                $"English OCR models are enabled and bundled fallback is disabled, but {settingName} does not point to a complete PaddleOCR model directory. " +
                "The directory must contain inference.json, inference.pdiparams, and inference.yml. " +
                $"Configured value: '{configuredPath ?? string.Empty}'.");
        }
    }

    private static bool IsCompletePaddleModelDirectory(string path)
    {
        return Directory.Exists(path) &&
               File.Exists(Path.Combine(path, "inference.json")) &&
               File.Exists(Path.Combine(path, "inference.pdiparams")) &&
               File.Exists(Path.Combine(path, "inference.yml"));
    }

    private static void RequireFile(
        List<string> failures,
        string settingName,
        string? configuredPath,
        string baseDir)
    {
        var resolved = EnglishOcrModelPathResolver.ResolvePath(configuredPath, baseDir);

        if (resolved is null || !File.Exists(resolved))
        {
            failures.Add(
                $"English OCR models are enabled, but {settingName} does not point to an existing file. " +
                $"Configured value: '{configuredPath ?? string.Empty}'.");
        }
    }
}

internal static class EnglishOcrModelPathResolver
{
    public static string? ResolvePath(
        string? configuredPath,
        string baseDir)
    {
        return ResolvePathCandidates(configuredPath, baseDir).FirstOrDefault(path =>
            Directory.Exists(path) || File.Exists(path));
    }

    public static IEnumerable<string> ResolvePathCandidates(
        string? configuredPath,
        string baseDir)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            yield break;

        if (Path.IsPathRooted(configuredPath))
        {
            yield return Path.GetFullPath(configuredPath);
            yield break;
        }

        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
        yield return Path.GetFullPath(Path.Combine(baseDir, configuredPath));
    }
}
