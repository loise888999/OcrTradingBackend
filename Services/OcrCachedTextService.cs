using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface IOcrCachedTextService
{
    OcrCachedTextRead ReadText(
        string source,
        Bitmap bitmap,
        OcrFieldKind fieldKind,
        OcrRuntimeSettings settings);
}

public sealed class OcrCachedTextService : IOcrCachedTextService
{
    private readonly IPaddleOcrService _ocr;
    private readonly IOcrImageTextCache _cache;

    public OcrCachedTextService(
        IPaddleOcrService ocr,
        IOcrImageTextCache cache)
    {
        _ocr = ocr;
        _cache = cache;
    }

    public OcrCachedTextRead ReadText(
        string source,
        Bitmap bitmap,
        OcrFieldKind fieldKind,
        OcrRuntimeSettings settings)
    {
        var options = new OcrHashCacheOptions(
            Enabled: settings.OcrFullHashCacheEnabled,
            TtlMinutes: settings.OcrFullHashCacheMinutes,
            MaxEntries: settings.OcrFullHashCacheMaxEntries,
            SettingsSignature: BuildSettingsSignature(fieldKind, settings),
            BenchmarkLogging: settings.OcrBenchmarkLogging);

        return _cache.ReadText(
            source,
            bitmap,
            image => _ocr.DetectText(image, fieldKind),
            options);
    }

    private static string BuildSettingsSignature(
        OcrFieldKind fieldKind,
        OcrRuntimeSettings settings)
    {
        var text = string.Join(
            "|",
            "v1",
            fieldKind,
            settings.UseEnglishModels,
            settings.FallbackToBundledModel,
            settings.DetectionModelPath,
            settings.ClassifierModelPath,
            settings.RecognitionModelPath,
            settings.DictionaryPath,
            settings.OcrAllowedCharFilteringEnabled,
            settings.CoordinateOcrAllowedChars,
            settings.PriceNumberOcrAllowedChars,
            settings.PriceMultiplierOcrAllowedChars,
            settings.PriceMenuOcrAllowedChars);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
