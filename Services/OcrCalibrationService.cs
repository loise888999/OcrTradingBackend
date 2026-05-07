using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface IOcrCalibrationService
{
    Task<OcrCalibrationResponse> ScoreAsync(CancellationToken ct);
}

public sealed class OcrCalibrationService : IOcrCalibrationService
{
    private readonly IOcrLayoutService _layoutService;
    private readonly IScreenCaptureService _capture;
    private readonly IOcrCachedTextService _ocr;
    private readonly IOcrImagePreprocessingService _preprocessor;
    private readonly IOcrDebugSnapshotService _debug;
    private readonly ICoordinateParser _coordinateParser;
    private readonly ICityParser _cityParser;
    private readonly IStrictTradeGoodMatcher _strictTradeGoodMatcher;
    private readonly IOptionsMonitor<OcrRuntimeSettings> _settings;

    public OcrCalibrationService(
        IOcrLayoutService layoutService,
        IScreenCaptureService capture,
        IOcrCachedTextService ocr,
        IOcrImagePreprocessingService preprocessor,
        IOcrDebugSnapshotService debug,
        ICoordinateParser coordinateParser,
        ICityParser cityParser,
        IStrictTradeGoodMatcher strictTradeGoodMatcher,
        IOptionsMonitor<OcrRuntimeSettings> settings)
    {
        _layoutService = layoutService;
        _capture = capture;
        _ocr = ocr;
        _preprocessor = preprocessor;
        _debug = debug;
        _coordinateParser = coordinateParser;
        _cityParser = cityParser;
        _strictTradeGoodMatcher = strictTradeGoodMatcher;
        _settings = settings;
    }

    public async Task<OcrCalibrationResponse> ScoreAsync(CancellationToken ct)
    {
        var layout = await _layoutService.LoadAsync(ct);
        var settings = _settings.CurrentValue;
        var checks = new List<OcrCalibrationCheck>();

        if (!layout.Enabled)
        {
            checks.Add(Skipped(
                "layout",
                "OCR layout",
                "layout",
                "Layout is disabled."));
        }

        checks.Add(await ScoreCityAsync(layout.Zones.City, settings, ct));
        checks.Add(await ScoreCoordinateAsync(layout.Zones.Coordinate, settings, ct));
        checks.Add(await ScoreTradeMenuAsync(layout.Price.BuyValidationBox, layout.Price.SellValidationBox, settings, ct));

        foreach (var row in layout.Price.Rows
                     .Where(x => x.Enabled)
                     .OrderBy(x => x.Index)
                     .Take(Math.Max(1, layout.Price.VisibleRows)))
        {
            checks.Add(await ScorePriceRowBoxAsync(row, settings, ct));
        }

        var scored = checks.Where(x => x.Status != "skipped").ToList();
        var score = scored.Count == 0 ? 0 : Math.Round(scored.Average(x => x.Score), 3);

        return new OcrCalibrationResponse(
            LayoutEnabled: layout.Enabled,
            Score: score,
            PassedChecks: checks.Count(x => x.Status == "pass"),
            WarningChecks: checks.Count(x => x.Status == "warn"),
            FailedChecks: checks.Count(x => x.Status == "fail"),
            SkippedChecks: checks.Count(x => x.Status == "skipped"),
            Checks: checks,
            Recommendations: BuildRecommendations(checks, score));
    }

    private async Task<OcrCalibrationCheck> ScoreCityAsync(
        OcrLayoutBox? box,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var read = await ReadBoxAsync(
            "city",
            "City",
            box,
            OcrFieldKind.City,
            preprocess: settings.CityForcePreprocess,
            settings,
            ct);

        if (read.Check is not null)
            return read.Check;

        var city = _cityParser.TryParse(read.RawText, settings.MinCityNameLength);
        if (city is not null)
        {
            return Pass(read, "City parsed.", city);
        }

        return string.IsNullOrWhiteSpace(read.RawText)
            ? Fail(read, "No city text detected.")
            : Warn(read, "Text detected, but no known city parsed.");
    }

    private async Task<OcrCalibrationCheck> ScoreCoordinateAsync(
        OcrLayoutBox? box,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var read = await ReadBoxAsync(
            "coordinate",
            "Coordinate",
            box,
            OcrFieldKind.Coordinate,
            preprocess: settings.CoordinateForcePreprocess,
            settings,
            ct);

        if (read.Check is not null)
            return read.Check;

        var parsed = _coordinateParser.TryParse(
            read.RawText,
            settings.WorldWidth,
            settings.WorldHeight);

        if (parsed is not null)
        {
            return Pass(read, "Coordinate parsed.", $"{parsed.X},{parsed.Y}");
        }

        return string.IsNullOrWhiteSpace(read.RawText)
            ? Fail(read, "No coordinate text detected.")
            : Warn(read, "Text detected, but no valid coordinate parsed.");
    }

    private async Task<OcrCalibrationCheck> ScoreTradeMenuAsync(
        OcrLayoutBox? buyBox,
        OcrLayoutBox? sellBox,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var buyRead = await ReadBoxAsync(
            "buy-validation",
            "Buy validation",
            buyBox,
            OcrFieldKind.PriceMenu,
            preprocess: settings.PriceLayoutValidationPreprocess,
            settings,
            ct);

        var sellRead = await ReadBoxAsync(
            "sell-validation",
            "Sell validation",
            sellBox,
            OcrFieldKind.PriceMenu,
            preprocess: settings.PriceLayoutValidationPreprocess,
            settings,
            ct);

        var buyVisible = buyRead.Check is null && LooksLikeBuyMenu(buyRead.RawText);
        var sellVisible = sellRead.Check is null && LooksLikeSellMenu(sellRead.RawText);
        var rawText = $"Buy: {buyRead.RawText}\nSell: {sellRead.RawText}".Trim();
        var parsedText = buyVisible ? "Buy" : sellVisible ? "Sell" : null;
        var box = buyVisible ? buyRead.Box : sellVisible ? sellRead.Box : buyRead.Box ?? sellRead.Box;
        var zone = buyVisible ? buyRead.CaptureZone : sellVisible ? sellRead.CaptureZone : buyRead.CaptureZone ?? sellRead.CaptureZone;
        var debugPath = buyVisible ? buyRead.DebugImagePath : sellVisible ? sellRead.DebugImagePath : buyRead.DebugImagePath ?? sellRead.DebugImagePath;

        if (buyVisible || sellVisible)
        {
            return new OcrCalibrationCheck(
                "trade-menu",
                "Trade menu",
                OcrFieldKind.PriceMenu.ToString(),
                "pass",
                1.0,
                rawText,
                parsedText,
                "Buy or Sell menu signal detected.",
                debugPath,
                box,
                zone);
        }

        if (buyRead.Check?.Status == "skipped" && sellRead.Check?.Status == "skipped")
        {
            return Skipped("trade-menu", "Trade menu", OcrFieldKind.PriceMenu.ToString(), "Buy and Sell validation boxes are missing or invalid.");
        }

        return new OcrCalibrationCheck(
            "trade-menu",
            "Trade menu",
            OcrFieldKind.PriceMenu.ToString(),
            "warn",
            0.5,
            rawText,
            null,
            "No Buy/Sell signal found. This is ok if no trade menu is open; open Buy or Sell menu and score again.",
            debugPath,
            box,
            zone);
    }

    private async Task<OcrCalibrationCheck> ScorePriceRowBoxAsync(
        OcrPriceRowLayout row,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var read = await ReadBoxAsync(
            $"row-{row.Index}-row",
            $"Row {row.Index}",
            row.Row,
            OcrFieldKind.General,
            preprocess: settings.PriceLayoutFieldPreprocess,
            settings,
            ct);

        if (read.Check is not null)
            return read.Check;

        var itemScore = _strictTradeGoodMatcher.Find(NormalizeWords(read.RawText)) is not null ? 0.45 : 0;
        var priceScore = TryParsePrice(read.RawText, out var price) ? 0.3 : 0;
        var multiplierScore = TryParseMultiplier(read.RawText, out var multiplier) ? 0.25 : 0;
        var score = Math.Round(itemScore + priceScore + multiplierScore, 3);

        if (score >= 0.95)
            return Pass(read, "Whole row parsed as item + price + multiplier.", $"{price} / {multiplier}%");

        if (score >= 0.45)
            return Warn(read, "Whole row partially parsed. Adjust row crop until item, price, and multiplier are all inside.");

        return Fail(read, "Whole row did not parse. Box likely misses row text or includes too much noise.");
    }

    private async Task<BoxRead> ReadBoxAsync(
        string key,
        string label,
        OcrLayoutBox? box,
        OcrFieldKind fieldKind,
        bool preprocess,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        if (box is not { IsValid: true })
            return new BoxRead(key, label, fieldKind.ToString(), "", null, box, null, Skipped(key, label, fieldKind.ToString(), "Box is missing or invalid."));

        var captureZone = _layoutService.TryGetLayoutBoxZone(box, key);
        if (captureZone is null)
            return new BoxRead(key, label, fieldKind.ToString(), "", null, box, null, Fail(key, label, fieldKind.ToString(), box, null, "Game window not found; cannot resolve box."));

        using var bitmap = _capture.Capture(captureZone);
        Bitmap? prepared = null;

        try
        {
            var image = bitmap;
            var source = "calibration-direct";

            if (preprocess)
            {
                prepared = fieldKind switch
                {
                    OcrFieldKind.City => _preprocessor.TryPrepareCityImage(bitmap, settings),
                    OcrFieldKind.Coordinate => _preprocessor.TryPrepareCoordinateImage(bitmap, settings),
                    _ => _preprocessor.TryPreparePriceImage(bitmap, settings)
                };

                if (prepared is not null)
                {
                    image = prepared;
                    source = "calibration-preprocessed";
                }
            }

            var raw = _ocr.ReadText(
                $"calibration:{key}:{source}",
                image,
                fieldKind,
                settings).Text;
            var debugPath = await _debug.SaveAsync("calibration", $"{key}-{source}", image, raw, ct);

            return new BoxRead(key, label, fieldKind.ToString(), raw, debugPath, box, captureZone, null);
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    private static IReadOnlyList<string> BuildRecommendations(
        IReadOnlyList<OcrCalibrationCheck> checks,
        double score)
    {
        var recommendations = new List<string>();

        if (score < 0.65)
            recommendations.Add("Recalibrate boxes: several OCR crops fail or parse only partial text.");

        if (checks.Any(x => x.Status == "fail" && x.Kind.Contains("Coordinate", StringComparison.OrdinalIgnoreCase)))
            recommendations.Add("Coordinate box: expand crop by 2-5 px around digits and keep map coordinate text centered.");

        if (checks.Any(x => x.Key.Equals("trade-menu", StringComparison.OrdinalIgnoreCase) && x.Status != "pass"))
            recommendations.Add("Trade menu: open either Buy or Sell menu, then score again. Only one menu needs to pass.");

        if (checks.Any(x => x.Key.StartsWith("row-", StringComparison.OrdinalIgnoreCase) && x.Status != "pass"))
            recommendations.Add("Trade rows: use one whole-row crop per row. Include item name, price, and multiplier together.");

        return recommendations;
    }

    private static OcrCalibrationCheck Pass(BoxRead read, string message, string? parsedText = null)
        => read.ToCheck("pass", 1.0, message, parsedText);

    private static OcrCalibrationCheck Warn(BoxRead read, string message, string? parsedText = null)
        => read.ToCheck("warn", 0.5, message, parsedText);

    private static OcrCalibrationCheck Fail(BoxRead read, string message, string? parsedText = null)
        => read.ToCheck("fail", 0.0, message, parsedText);

    private static OcrCalibrationCheck Skipped(string key, string label, string kind, string message)
        => new(key, label, kind, "skipped", 0, "", null, message, null, null, null);

    private static OcrCalibrationCheck Fail(
        string key,
        string label,
        string kind,
        OcrLayoutBox? box,
        OcrZone? captureZone,
        string message)
        => new(key, label, kind, "fail", 0, "", null, message, null, box, captureZone);

    private static bool TryParsePrice(string text, out decimal value)
    {
        value = 0;
        var digits = new string((text ?? string.Empty)
            .Replace(",", "")
            .Replace(".", "")
            .TakeWhile(c => !char.IsLetter(c))
            .Where(char.IsDigit)
            .ToArray());

        if (string.IsNullOrWhiteSpace(digits))
        {
            digits = new string((text ?? string.Empty)
                .Where(char.IsDigit)
                .ToArray());
        }

        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out value) &&
               value > 0;
    }

    private static bool TryParseMultiplier(string text, out decimal value)
    {
        value = 0;
        var match = Regex.Match(text ?? string.Empty, @"(?<mult>\d{1,3})\s*%?");
        return match.Success &&
               decimal.TryParse(match.Groups["mult"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out value) &&
               value is > 0 and <= 199;
    }

    private static bool LooksLikeBuyMenu(string rawText)
    {
        var normalized = NormalizeWords(rawText);
        return ContainsWord(normalized, "buy") ||
               normalized.Contains("for sale", StringComparison.Ordinal);
    }

    private static bool LooksLikeSellMenu(string rawText)
    {
        var normalized = NormalizeWords(rawText);
        return ContainsWord(normalized, "sell") ||
               ContainsWord(normalized, "inventory") ||
               ContainsWord(normalized, "nventory");
    }

    private static string NormalizeWords(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = Regex.Replace(value, @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static bool ContainsWord(string normalized, string word)
    {
        return Regex.IsMatch(
            normalized,
            $@"(^|\s){Regex.Escape(word)}($|\s)",
            RegexOptions.CultureInvariant);
    }

    private sealed record BoxRead(
        string Key,
        string Label,
        string Kind,
        string RawText,
        string? DebugImagePath,
        OcrLayoutBox? Box,
        OcrZone? CaptureZone,
        OcrCalibrationCheck? Check)
    {
        public OcrCalibrationCheck ToCheck(
            string status,
            double score,
            string message,
            string? parsedText)
        {
            return new OcrCalibrationCheck(
                Key,
                Label,
                Kind,
                status,
                score,
                RawText,
                parsedText,
                message,
                DebugImagePath,
                Box,
                CaptureZone);
        }
    }
}
