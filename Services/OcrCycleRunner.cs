using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface IOcrCycleRunner
{
    Task RunOneCycleAsync(CancellationToken ct);
    Task<OcrManualReadResponse> TestZoneAsync(string zoneKind, CancellationToken ct);
}

public sealed class OcrCycleRunner : IOcrCycleRunner
{
    private readonly AppDbContext _db;
    private readonly IScreenCaptureService _capture;
    private readonly IOcrCachedTextService _ocr;
    private readonly ICoordinateParser _coordinateParser;
    private readonly ICityParser _cityParser;
    private readonly IPriceParser _priceParser;
    private readonly IStrictTradeGoodMatcher _strictTradeGoodMatcher;
    private readonly IPendingTradeGoodService _pendingTradeGoodService;
    private readonly IWindowRelativeOcrZoneService _zoneService;
    private readonly OcrControlState _control;
    private readonly IOptionsMonitor<OcrRuntimeSettings> _settings;
    private readonly IOcrDebugSnapshotService _debug;
    private readonly IOcrImagePreprocessingService _preprocessor;
    private readonly IOcrTextPresenceAnalyzer _textPresenceAnalyzer;
    private readonly IOcrLayoutService _layoutService;
    private readonly IPriceOcrBatchService _priceBatch;
    private readonly IPriceLayoutRowCacheService _priceLayoutRowCache;
    private readonly IPriceLayoutRowFingerprintService _priceLayoutRowFingerprint;
    private readonly IPriceRecentHashCacheService _priceRecentHashCache;
    private readonly OcrLastResultState _lastResults;
    private readonly ILogger<OcrCycleRunner> _logger;

    public OcrCycleRunner(
        AppDbContext db,
        IScreenCaptureService capture,
        IOcrCachedTextService ocr,
        ICoordinateParser coordinateParser,
        ICityParser cityParser,
        IPriceParser priceParser,
        IStrictTradeGoodMatcher strictTradeGoodMatcher,
        IPendingTradeGoodService pendingTradeGoodService,
        IWindowRelativeOcrZoneService zoneService,
        OcrControlState control,
        IOptionsMonitor<OcrRuntimeSettings> settings,
        IOcrDebugSnapshotService debug,
        IOcrImagePreprocessingService preprocessor,
        IOcrTextPresenceAnalyzer textPresenceAnalyzer,
        IOcrLayoutService layoutService,
        IPriceOcrBatchService priceBatch,
        IPriceLayoutRowCacheService priceLayoutRowCache,
        IPriceLayoutRowFingerprintService priceLayoutRowFingerprint,
        IPriceRecentHashCacheService priceRecentHashCache,
        OcrLastResultState lastResults,
        ILogger<OcrCycleRunner> logger)
    {
        _db = db;
        _capture = capture;
        _ocr = ocr;
        _coordinateParser = coordinateParser;
        _cityParser = cityParser;
        _priceParser = priceParser;
        _strictTradeGoodMatcher = strictTradeGoodMatcher;
        _pendingTradeGoodService = pendingTradeGoodService;
        _zoneService = zoneService;
        _control = control;
        _settings = settings;
        _debug = debug;
        _preprocessor = preprocessor;
        _textPresenceAnalyzer = textPresenceAnalyzer;
        _layoutService = layoutService;
        _priceBatch = priceBatch;
        _priceLayoutRowCache = priceLayoutRowCache;
        _priceLayoutRowFingerprint = priceLayoutRowFingerprint;
        _priceRecentHashCache = priceRecentHashCache;
        _lastResults = lastResults;
        _logger = logger;
    }

    public async Task RunOneCycleAsync(CancellationToken ct)
    {
        var settings = _settings.CurrentValue;

        try
        {
            var layout = await _layoutService.LoadAsync(ct);

            if (!layout.Enabled)
            {
                _logger.LogWarning(
                    "OCR layout is disabled. Layout-only OCR mode requires the calibration layout to be enabled.");
                return;
            }

            var coordinateZone = _layoutService.TryGetCoordinateZone(layout);
            var cityZone = _layoutService.TryGetCityZone(layout);

            if (coordinateZone is null)
            {
                _logger.LogWarning(
                    "Coordinate OCR layout box is missing or the game window could not be resolved.");
            }

            if (cityZone is null)
            {
                _logger.LogWarning(
                    "City OCR layout box is missing or the game window could not be resolved.");
            }

            var coordinateWasReadThisCycle = false;
            var sawNotAtSeaSignal = false;
            string? detectedTradeType = null;

            var coordinateRecentlyVisible =
                IsCoordinateRecentlyVisible(settings);

            var cityDue =
                _control.LastCityAttemptUtc is null ||
                DateTime.UtcNow - _control.LastCityAttemptUtc.Value >=
                TimeSpan.FromSeconds(settings.CityIntervalSeconds);

            if (!coordinateRecentlyVisible &&
                cityDue &&
                cityZone is not null)
            {
                _control.LastCityAttemptUtc = DateTime.UtcNow;

                var city = await TryReadCityAsync(cityZone, settings, ct);

                if (city is not null)
                {
                    _db.CityCaptures.Add(new CityCapture
                    {
                        City = city,
                        RawText = city,
                        CapturedAtUtc = DateTime.UtcNow
                    });

                    _priceRecentHashCache.NotifyCityStatus(city);

                    _control.LastCityReadUtc = DateTime.UtcNow;
                    sawNotAtSeaSignal = true;
                    MarkNotAtSea("city");
                }
            }

            var latestCityBeforeCoordinate = await _db.CityCaptures
                .OrderByDescending(x => x.CapturedAtUtc)
                .FirstOrDefaultAsync(ct);

            var wasInKnownCityBeforeCoordinate =
                PriceCaptureMergeService.IsKnownCity(latestCityBeforeCoordinate?.City);

            var priceDue =
                !coordinateRecentlyVisible &&
                wasInKnownCityBeforeCoordinate &&
                IsPriceOcrDue(settings);

            var coordinateDue =
                coordinateZone is not null &&
                IsCoordinateOcrDue(settings);

            if (!coordinateRecentlyVisible &&
                priceDue &&
                CanDetectTradeTypeFromLayout(layout))
            {
                detectedTradeType = await DetectTradeTypeFromLayoutAsync(layout, settings, ct);

                if (PriceCaptureMergeService.IsKnownTradeType(detectedTradeType))
                {
                    sawNotAtSeaSignal = true;
                    MarkNotAtSea("trade-menu");
                }
            }

            var coordinateAllowedByGate =
                coordinateDue &&
                IsCoordinateAllowedBySeaGate(settings, sawNotAtSeaSignal);

            if (coordinateZone is not null &&
                coordinateAllowedByGate)
            {
                _control.LastCoordinateAttemptUtc = DateTime.UtcNow;

                var coordinateRecentlyVisibleBeforeRead =
                    IsCoordinateRecentlyVisible(settings);

                var ignoreCoordinateJumpThisRead =
                    wasInKnownCityBeforeCoordinate &&
                    !coordinateRecentlyVisibleBeforeRead;

                var previousCoordinate = ignoreCoordinateJumpThisRead
                    ? null
                    : await _db.CoordinateCaptures
                        .OrderByDescending(x => x.CapturedAtUtc)
                        .FirstOrDefaultAsync(ct);

                var parsed = await TryReadCoordinateAsync(
                    coordinateZone,
                    previousCoordinate,
                    settings,
                    ct);

                if (parsed is not null)
                {
                    coordinateWasReadThisCycle = true;
                    _control.LastCoordinateReadUtc = DateTime.UtcNow;
                    _control.ProbablyAtSea = true;

                    await AddUniqueCoordinateAsync(parsed, ct);
                    SetLatestCityUnknownIfNeeded(latestCityBeforeCoordinate, parsed.RawText);

                    if (ignoreCoordinateJumpThisRead)
                    {
                        _logger.LogInformation(
                            "Coordinate appeared after known city. Ignored max jump range for this first coordinate read.");
                    }
                }
            }

            coordinateRecentlyVisible = IsCoordinateRecentlyVisible(settings);

            if (!coordinateWasReadThisCycle &&
                !coordinateRecentlyVisible)
            {
                var latestCity = await _db.CityCaptures
                    .OrderByDescending(x => x.CapturedAtUtc)
                    .FirstOrDefaultAsync(ct);

                _priceRecentHashCache.NotifyCityStatus(latestCity?.City);

                if (PriceCaptureMergeService.IsKnownCity(latestCity?.City))
                {
                    if (priceDue)
                    {
                        await TryReadPricesAsync(
                            latestCity!,
                            settings,
                            layout,
                            detectedTradeType,
                            ct);
                    }
                }
                else
                {
                    _priceRecentHashCache.NotifyCityStatus("Unknown");
                    _logger.LogInformation("Skipped price OCR because current city is unknown.");
                }
            }
            else
            {
                _priceRecentHashCache.NotifyCityStatus("Unknown");

                _logger.LogInformation(
                    "Skipped price OCR because coordinate/map is visible recently; current city is treated as Unknown.");
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _lastResults.SetFailure(ex.Message);
            throw;
        }
    }

    public async Task<OcrManualReadResponse> TestZoneAsync(string zoneKind, CancellationToken ct)
    {
        var settings = _settings.CurrentValue;
        var normalized = zoneKind.Trim().ToLowerInvariant();

        var layout = await _layoutService.LoadAsync(ct);

        var zoneName = normalized switch
        {
            "coordinate" => "Coordinate",
            "city" => "City",
            "price" => "PriceLayout",
            _ => throw new ArgumentException($"Unsupported OCR zone kind: {zoneKind}")
        };

        var zone = normalized switch
        {
            "coordinate" => _layoutService.TryGetCoordinateZone(layout),
            "city" => _layoutService.TryGetCityZone(layout),
            "price" => null,
            _ => null
        };

        if (normalized == "price")
        {
            return new OcrManualReadResponse(
                ZoneKind: normalized,
                ZoneName: zoneName,
                ZoneFound: layout.Enabled && layout.UseLayoutForPrice && layout.Price.UseFieldBoxes,
                Attempts: Array.Empty<OcrManualReadAttempt>(),
                BestParsed: "Use /api/ocr-layout/test-box to test individual price layout boxes.");
        }

        if (zone is null)
        {
            return new OcrManualReadResponse(
                ZoneKind: normalized,
                ZoneName: zoneName,
                ZoneFound: false,
                Attempts: Array.Empty<OcrManualReadAttempt>(),
                BestParsed: null);
        }

        var attempts = new List<OcrManualReadAttempt>();

        var previousCoordinate = await _db.CoordinateCaptures
            .OrderByDescending(x => x.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);

        using var bitmap = _capture.Capture(zone);

        attempts.Add(await BuildManualAttemptAsync(
            normalized,
            "direct",
            bitmap,
            previousCoordinate,
            settings,
            ct));

        var preprocessed = normalized switch
        {
            "coordinate" => _preprocessor.TryPrepareCoordinateImage(bitmap, settings),
            "city" => _preprocessor.TryPrepareCityImage(bitmap, settings),
            "price" => _preprocessor.TryPreparePriceImage(bitmap, settings),
            _ => null
        };

        if (preprocessed is not null)
        {
            using (preprocessed)
            {
                attempts.Add(await BuildManualAttemptAsync(
                    normalized,
                    "preprocessed",
                    preprocessed,
                    previousCoordinate,
                    settings,
                    ct));
            }
        }

        var bestParsed = attempts
            .LastOrDefault(x => x.Parsed is not null)
            ?.Parsed;

        return new OcrManualReadResponse(
            ZoneKind: normalized,
            ZoneName: zoneName,
            ZoneFound: true,
            Attempts: attempts,
            BestParsed: bestParsed);
    }

    private async Task<OcrManualReadAttempt> BuildManualAttemptAsync(
        string kind,
        string source,
        Bitmap bitmap,
        CoordinateCapture? previousCoordinate,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var read = ReadOcrText(kind, source, bitmap, settings);
        var raw = read.Text;

        var debugPath = await _debug.SaveAsync(kind, source, bitmap, raw, ct);

        object? parsed = kind switch
        {
            "coordinate" => _coordinateParser.TryParse(
                raw,
                settings.WorldWidth,
                settings.WorldHeight,
                previousCoordinate,
                new CoordinateCorrectionOptions(
                    settings.EnableCoordinateCorrection,
                    settings.MaxCoordinateJumpX,
                    settings.MaxCoordinateJumpY)),

            "city" => _cityParser.TryParse(raw, settings.MinCityNameLength),

            "price" => _priceParser.ParseLines(raw, allowPendingCandidates: true),

            _ => null
        };

        return new OcrManualReadAttempt(
            Source: source,
            RawText: raw,
            Parsed: parsed,
            DebugImagePath: debugPath);
    }

    private async Task<ParsedCoordinate?> TryReadCoordinateAsync(
        OcrZone coordinateZone,
        CoordinateCapture? previousCoordinate,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var forcePreprocess = settings.CoordinateForcePreprocess;

        using (var fixedBitmap = _capture.Capture(coordinateZone))
        {
            if (ShouldSkipOcrByTextPresence("coordinate", "fixed", "before-preprocess", fixedBitmap, settings))
            {
                _lastResults.SetCoordinate("fixed", string.Empty, null, null);
                return null;
            }

            var fixedPreprocessed = _preprocessor.TryPrepareCoordinateImage(fixedBitmap, settings);

            if (forcePreprocess && fixedPreprocessed is not null)
            {
                using (fixedPreprocessed)
                {
                    var result = await TryOcrAndParseCoordinateAsync(
                        fixedPreprocessed,
                        "fixed-preprocessed-forced",
                        previousCoordinate,
                        settings,
                        ct);

                    if (result is not null)
                        return result;
                }
            }
            else
            {
                fixedPreprocessed?.Dispose();

                var direct = await TryOcrAndParseCoordinateAsync(
                    fixedBitmap,
                    "fixed",
                    previousCoordinate,
                    settings,
                    ct);

                if (direct is not null)
                    return direct;

                var preprocessed = _preprocessor.TryPrepareCoordinateImage(fixedBitmap, settings);
                if (preprocessed is not null)
                {
                    using (preprocessed)
                    {
                        var result = await TryOcrAndParseCoordinateAsync(
                            preprocessed,
                            "fixed-preprocessed",
                            previousCoordinate,
                            settings,
                            ct);

                        if (result is not null)
                            return result;
                    }
                }
            }
        }

        return null;
    }

    private async Task<ParsedCoordinate?> TryOcrAndParseCoordinateAsync(
        Bitmap bitmap,
        string source,
        CoordinateCapture? previousCoordinate,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var read = TryReadOcrText("coordinate", source, bitmap, settings);
        if (read is null)
        {
            _lastResults.SetCoordinate(source, string.Empty, null, null);
            return null;
        }

        var raw = read.Text;

        var debugPath = await _debug.SaveAsync("coordinate", source, bitmap, raw, ct);

        if (string.IsNullOrWhiteSpace(raw))
        {
            _lastResults.SetCoordinate(source, raw, null, debugPath);
            return null;
        }

        var parsed = _coordinateParser.TryParse(
            raw,
            settings.WorldWidth,
            settings.WorldHeight,
            previousCoordinate,
            new CoordinateCorrectionOptions(
                settings.EnableCoordinateCorrection,
                settings.MaxCoordinateJumpX,
                settings.MaxCoordinateJumpY));

        _lastResults.SetCoordinate(source, raw, parsed, debugPath);

        if (parsed is null)
            return null;

        return parsed with { RawText = $"{source}: {parsed.RawText}" };
    }

    private async Task<string?> TryReadCityAsync(
        OcrZone cityZone,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        using var bitmap = _capture.Capture(cityZone);

        if (ShouldSkipOcrByTextPresence("city", "direct", "before-preprocess", bitmap, settings))
        {
            _lastResults.SetCity("direct", string.Empty, null, null);
            return null;
        }

        var forcePreprocess = settings.CityForcePreprocess;

        if (forcePreprocess)
        {
            var forcedPreprocessed = _preprocessor.TryPrepareCityImage(bitmap, settings);

            if (forcedPreprocessed is not null)
            {
                using (forcedPreprocessed)
                {
                    var read = TryReadOcrText("city", "preprocessed-forced", forcedPreprocessed, settings);
                    if (read is null)
                    {
                        _lastResults.SetCity("preprocessed-forced", string.Empty, null, null);
                        return null;
                    }

                    var raw = read.Text;

                    var debugPath = await _debug.SaveAsync(
                        "city",
                        "preprocessed-forced",
                        forcedPreprocessed,
                        raw,
                        ct);

                    var city = _cityParser.TryParse(raw, settings.MinCityNameLength);

                    _lastResults.SetCity(
                        "preprocessed-forced",
                        raw,
                        city,
                        debugPath);

                    return city;
                }
            }

            _logger.LogInformation(
                "CityForcePreprocess was enabled, but city preprocessing returned null. Falling back to direct city OCR.");
        }

        var directRead = TryReadOcrText("city", "direct", bitmap, settings);
        if (directRead is null)
        {
            _lastResults.SetCity("direct", string.Empty, null, null);
            return null;
        }

        var directRaw = directRead.Text;

        var directDebugPath = await _debug.SaveAsync(
            "city",
            "direct",
            bitmap,
            directRaw,
            ct);

        var directCity = _cityParser.TryParse(
            directRaw,
            settings.MinCityNameLength);

        _lastResults.SetCity(
            "direct",
            directRaw,
            directCity,
            directDebugPath);

        if (directCity is not null)
            return directCity;

        var preprocessed = _preprocessor.TryPrepareCityImage(bitmap, settings);
        if (preprocessed is null)
            return null;

        using (preprocessed)
        {
            var read = TryReadOcrText("city", "preprocessed", preprocessed, settings);
            if (read is null)
            {
                _lastResults.SetCity("preprocessed", string.Empty, null, null);
                return null;
            }

            var raw = read.Text;

            var debugPath = await _debug.SaveAsync(
                "city",
                "preprocessed",
                preprocessed,
                raw,
                ct);

            var city = _cityParser.TryParse(
                raw,
                settings.MinCityNameLength);

            _lastResults.SetCity(
                "preprocessed",
                raw,
                city,
                debugPath);

            return city;
        }
    }

    private bool IsPriceOcrDue(OcrRuntimeSettings settings)
    {
        var now = DateTime.UtcNow;

        var fastModeActive =
            _control.PriceFastModeUntilUtc is not null &&
            _control.PriceFastModeUntilUtc.Value > now;

        var intervalSeconds = fastModeActive
            ? Math.Max(1, settings.ActivePriceIntervalSeconds)
            : Math.Max(1, settings.PriceIntervalSeconds);

        if (_control.LastPriceAttemptUtc is null)
            return true;

        return now - _control.LastPriceAttemptUtc.Value >=
               TimeSpan.FromSeconds(intervalSeconds);
    }

    private bool IsCoordinateOcrDue(OcrRuntimeSettings settings)
    {
        if (_control.LastCoordinateAttemptUtc is null)
            return true;

        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(settings.CoordinateIntervalMilliseconds, 250, 60_000));

        return DateTime.UtcNow - _control.LastCoordinateAttemptUtc.Value >= interval;
    }

    private bool IsCoordinateRecentlyVisible(OcrRuntimeSettings settings)
    {
        return _control.LastCoordinateReadUtc is not null &&
               DateTime.UtcNow - _control.LastCoordinateReadUtc.Value <
               TimeSpan.FromSeconds(Math.Max(1, settings.CoordinateRecentlyVisibleSeconds));
    }

    private bool HasRecentNotAtSeaSignal(OcrRuntimeSettings settings)
    {
        if (_control.LastNotAtSeaSignalUtc is null)
            return false;

        var threshold = TimeSpan.FromSeconds(
            Math.Max(1, settings.ProbablyAtSeaAfterNoCityOrMenuSeconds));

        return DateTime.UtcNow - _control.LastNotAtSeaSignalUtc.Value < threshold;
    }

    private bool IsCoordinateAllowedBySeaGate(
        OcrRuntimeSettings settings,
        bool sawNotAtSeaSignal)
    {
        if (!settings.CoordinateRequiresProbablyAtSea)
            return true;

        if (sawNotAtSeaSignal || HasRecentNotAtSeaSignal(settings))
            return false;

        if (_control.ProbablyAtSea)
            return true;

        var now = DateTime.UtcNow;
        _control.SeaCandidateSinceUtc ??= _control.LastNotAtSeaSignalUtc ?? now;

        var threshold = TimeSpan.FromSeconds(
            Math.Max(1, settings.ProbablyAtSeaAfterNoCityOrMenuSeconds));

        if (now - _control.SeaCandidateSinceUtc.Value < threshold)
            return false;

        _control.ProbablyAtSea = true;

        _logger.LogInformation(
            "Coordinate OCR probably-at-sea gate opened after no city/menu signal for {Seconds} seconds.",
            threshold.TotalSeconds);

        return true;
    }

    private void MarkNotAtSea(string reason)
    {
        var wasProbablyAtSea = _control.ProbablyAtSea;

        _control.ProbablyAtSea = false;
        _control.SeaCandidateSinceUtc = null;
        _control.LastNotAtSeaSignalUtc = DateTime.UtcNow;

        if (wasProbablyAtSea)
        {
            _logger.LogInformation(
                "Coordinate OCR probably-at-sea gate closed because a not-at-sea signal was detected. Reason={Reason}",
                reason);
        }
    }

    private static bool CanDetectTradeTypeFromLayout(OcrLayoutSettings layout)
    {
        return layout.Enabled &&
               layout.UseLayoutForPrice &&
               layout.Price.UseFieldBoxes &&
               (layout.Price.BuyValidationBox is { IsValid: true } ||
                layout.Price.SellValidationBox is { IsValid: true });
    }

    private void StopPriceFastMode(string reason)
    {
        if (_control.PriceFastModeUntilUtc is null)
            return;

        if (_control.PriceFastModeUntilUtc <= DateTime.UtcNow)
            return;

        _control.PriceFastModeUntilUtc = null;

        _logger.LogInformation(
            "Price fast mode stopped because the price menu was not detected. Reason={Reason}",
            reason);
    }

    private async Task TryReadPricesAsync(
        CityCapture latestCity,
        OcrRuntimeSettings settings,
        OcrLayoutSettings layout,
        string? detectedTradeType,
        CancellationToken ct)
    {
        _control.LastPriceAttemptUtc = DateTime.UtcNow;

        if (!layout.Enabled ||
            !layout.UseLayoutForPrice ||
            !layout.Price.UseFieldBoxes)
        {
            _logger.LogWarning(
                "Skipped price OCR because layout-only mode requires UseLayoutForPrice=true and Price.UseFieldBoxes=true.");

            return;
        }

        await TryReadPricesFromLayoutAsync(layout, latestCity, settings, detectedTradeType, ct);
    }

    private async Task TryReadPricesFromLayoutAsync(
        OcrLayoutSettings layout,
        CityCapture latestCity,
        OcrRuntimeSettings settings,
        string? detectedTradeType,
        CancellationToken ct)
    {
        var tradeType = detectedTradeType ?? await DetectTradeTypeFromLayoutAsync(layout, settings, ct);

        if (!PriceCaptureMergeService.IsKnownTradeType(tradeType))
        {
            if (settings.OcrBenchmarkLogging)
            {
                _logger.LogInformation(
                    "Layout price OCR skipped because Buy/Sell validation was not detected.");
            }

            StopPriceFastMode("layout-price-menu-not-detected");
            await FlushPriceBatchAsync(settings, "layout-price-menu-not-detected", ct);
            return;
        }

        var parsed = new List<ParsedPriceLine>();
        var rawRows = new List<string>();

        foreach (var row in layout.Price.Rows
                     .Where(x => x.Enabled)
                     .OrderBy(x => x.Index)
                     .Take(Math.Max(1, layout.Price.VisibleRows)))
        {
            var parsedRow = await TryReadLayoutPriceRowAsync(
                row,
                tradeType,
                settings,
                ct);

            if (parsedRow is null)
                continue;

            parsed.Add(parsedRow);
            rawRows.Add(parsedRow.RawText);
        }

        var rawText = rawRows.Count == 0
            ? $"Layout field OCR found no valid rows. TradeType={tradeType}"
            : string.Join(Environment.NewLine, rawRows);

        await ProcessParsedPricesAsync(
            selectedSource: "layout-field-boxes",
            selectedRaw: rawText,
            selectedDebugPath: null,
            parsedPrices: parsed,
            latestCity: latestCity,
            settings: settings,
            ct: ct);
    }

    private async Task<string> DetectTradeTypeFromLayoutAsync(
        OcrLayoutSettings layout,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        if (layout.Price.BuyValidationBox is { IsValid: true } buyBox)
        {
            var buyText = await ReadLayoutBoxTextAsync(
                kind: "price-layout-validation",
                source: "buy-validation",
                box: buyBox,
                preprocess: settings.PriceLayoutValidationPreprocess,
                settings: settings,
                ct: ct);

            if (LooksLikeBuyText(buyText))
                return "Buy";
        }

        if (layout.Price.SellValidationBox is { IsValid: true } sellBox)
        {
            var sellText = await ReadLayoutBoxTextAsync(
                kind: "price-layout-validation",
                source: "sell-validation",
                box: sellBox,
                preprocess: settings.PriceLayoutValidationPreprocess,
                settings: settings,
                ct: ct);

            if (LooksLikeSellText(sellText))
                return "Sell";
        }

        return "Unknown";
    }

    private async Task<ParsedPriceLine?> TryReadLayoutPriceRowAsync(
        OcrPriceRowLayout row,
        string tradeType,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var hasRowBox = row.Row is { IsValid: true };
        var hasFieldFallback = row.ItemName is { IsValid: true } &&
                               row.Price is { IsValid: true } &&
                               row.Multiplier is { IsValid: true };

        if (!hasRowBox && !hasFieldFallback)
        {
            return null;
        }

        var rowRead = await TryReadCombinedLayoutPriceRowAsync(
            row,
            tradeType,
            settings,
            ct);

        if (rowRead.Parsed is not null)
            return rowRead.Parsed;

        if (!settings.PriceLayoutFieldFallbackEnabled ||
            row.ItemName is not { IsValid: true } itemBox ||
            row.Price is not { IsValid: true } priceBox ||
            row.Multiplier is not { IsValid: true } multiplierBox)
        {
            RememberLayoutRowCache(row.Index, tradeType, rowRead.Fingerprint, null);
            return null;
        }

        var itemNameRaw = await ReadLayoutBoxTextAsync(
            kind: "price-layout-row",
            source: $"row-{row.Index}-item-name",
            box: itemBox,
            preprocess: settings.PriceLayoutFieldPreprocess,
            settings: settings,
            ct: ct);

        var priceRaw = await ReadLayoutBoxTextAsync(
            kind: "price-layout-row",
            source: $"row-{row.Index}-price",
            box: priceBox,
            preprocess: settings.PriceLayoutFieldPreprocess,
            settings: settings,
            ct: ct);

        var multiplierRaw = await ReadLayoutBoxTextAsync(
            kind: "price-layout-row",
            source: $"row-{row.Index}-multiplier",
            box: multiplierBox,
            preprocess: settings.PriceLayoutFieldPreprocess,
            settings: settings,
            ct: ct);

        var itemName = CleanLayoutFieldText(itemNameRaw);
        if (string.IsNullOrWhiteSpace(itemName))
        {
            RememberLayoutRowCache(row.Index, tradeType, rowRead.Fingerprint, null);
            return null;
        }

        if (!TryParseLayoutDecimal(priceRaw, out var price))
        {
            RememberLayoutRowCache(row.Index, tradeType, rowRead.Fingerprint, null);
            return null;
        }

        if (!TryParseLayoutDecimal(multiplierRaw, out var multiplier))
        {
            RememberLayoutRowCache(row.Index, tradeType, rowRead.Fingerprint, null);
            return null;
        }

        var strict = _strictTradeGoodMatcher.Find(itemName);
        var tradeGoodType = strict?.TradeGoodType ?? "Unknown";

        var rawText =
            $"Row {row.Index}: {itemName} | {priceRaw.Trim()} | {multiplierRaw.Trim()} | {tradeType}";

        var parsed = new ParsedPriceLine(
            itemName,
            tradeGoodType,
            price,
            multiplier,
            tradeType,
            rawText);

        RememberLayoutRowCache(row.Index, tradeType, rowRead.Fingerprint, parsed);

        return parsed;
    }

    private async Task<LayoutRowRead> TryReadCombinedLayoutPriceRowAsync(
        OcrPriceRowLayout row,
        string tradeType,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var rowZone = TryGetLayoutRowZone(row);
        if (rowZone is null)
            return LayoutRowRead.Empty(null);

        using var bitmap = _capture.Capture(rowZone);

        var source = $"row-{row.Index}-combined";
        if (ShouldSkipOcrByTextPresence("price-layout-row", source, "before-preprocess", bitmap, settings))
            return LayoutRowRead.Empty(null);

        var fingerprintStopwatch = Stopwatch.StartNew();
        var fingerprint = _priceLayoutRowFingerprint.Compute(bitmap);
        fingerprintStopwatch.Stop();

        var rowKey = GetLayoutRowCacheKey(row.Index, tradeType);
        var maxDistance = Math.Clamp(settings.PriceLayoutRowFingerprintTolerance, 0, 128);
        if (settings.SkipUnchangedOcrByHash &&
            _priceLayoutRowCache.TryGet(
                rowKey,
                tradeType,
                fingerprint,
                maxDistance,
                out var cached,
                out var distance))
        {
            if (settings.OcrBenchmarkLogging)
            {
                _logger.LogInformation(
                    "Price layout row OCR skipped by fingerprint. Row={RowIndex}; TradeType={TradeType}; Distance={Distance}; FingerprintMs={FingerprintMs}",
                    row.Index,
                    tradeType,
                    distance,
                    fingerprintStopwatch.Elapsed.TotalMilliseconds);
            }

            return new LayoutRowRead(
                fingerprint,
                RebaseLayoutRow(cached, row.Index, tradeType));
        }

        var imageToRead = bitmap;
        Bitmap? preprocessed = null;

        try
        {
            if (settings.PriceLayoutFieldPreprocess)
            {
                preprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);
                if (preprocessed is not null)
                {
                    imageToRead = preprocessed;
                    source = $"{source}-preprocessed";
                }
            }

            var read = TryReadOcrText("price-layout-row", source, imageToRead, settings);
            if (read is null)
                return LayoutRowRead.Empty(fingerprint);

            await _debug.SaveAsync(
                "price-layout-row",
                source,
                imageToRead,
                read.Text,
                ct);

            var parsed = TryParseCombinedLayoutPriceRow(
                row.Index,
                read.Text,
                tradeType);

            if (parsed is not null)
            {
                RememberLayoutRowCache(row.Index, tradeType, fingerprint, parsed);
                return new LayoutRowRead(fingerprint, parsed);
            }

            if (settings.OcrBenchmarkLogging)
            {
                _logger.LogInformation(
                    "Combined layout row OCR did not parse. Row={RowIndex}; TradeType={TradeType}; RawText={RawText}",
                    row.Index,
                    tradeType,
                    read.Text);
            }

            return LayoutRowRead.Empty(fingerprint);
        }
        finally
        {
            preprocessed?.Dispose();
        }
    }

    private void RememberLayoutRowCache(
        int rowIndex,
        string tradeType,
        PriceLayoutRowFingerprint? fingerprint,
        ParsedPriceLine? parsed)
    {
        if (fingerprint is null)
            return;

        _priceLayoutRowCache.Remember(
            GetLayoutRowCacheKey(rowIndex, tradeType),
            tradeType,
            fingerprint,
            parsed);
    }

    private OcrZone? TryGetLayoutRowZone(
        OcrPriceRowLayout row)
    {
        if (row.Row is { IsValid: true } rowBox)
            return _layoutService.TryGetLayoutBoxZone(rowBox, $"row-{row.Index}");

        var zones = new List<OcrZone>();

        if (row.ItemName is { IsValid: true } itemBox)
            AddLayoutBoxZone(itemBox, $"row-{row.Index}-item-name");

        if (row.Price is { IsValid: true } priceBox)
            AddLayoutBoxZone(priceBox, $"row-{row.Index}-price");

        if (row.Multiplier is { IsValid: true } multiplierBox)
            AddLayoutBoxZone(multiplierBox, $"row-{row.Index}-multiplier");

        if (zones.Count == 0)
            return null;

        var left = zones.Min(x => Math.Min(x.TopLeftX, x.BottomRightX));
        var top = zones.Min(x => Math.Min(x.TopLeftY, x.BottomRightY));
        var right = zones.Max(x => Math.Max(x.TopLeftX, x.BottomRightX));
        var bottom = zones.Max(x => Math.Max(x.TopLeftY, x.BottomRightY));

        return new OcrZone
        {
            Name = $"PriceLayoutRow{row.Index}",
            TopLeftX = left,
            TopLeftY = top,
            BottomRightX = right,
            BottomRightY = bottom,
            UpdatedAtUtc = DateTime.UtcNow
        };

        void AddLayoutBoxZone(OcrLayoutBox box, string source)
        {
            var zone = _layoutService.TryGetLayoutBoxZone(box, source);
            if (zone is not null)
                zones.Add(zone);
        }
    }

    private static string GetLayoutRowCacheKey(int rowIndex, string tradeType)
    {
        return $"price-layout-row:{rowIndex}:{tradeType}";
    }

    private static ParsedPriceLine? RebaseLayoutRow(
        ParsedPriceLine? parsed,
        int rowIndex,
        string tradeType)
    {
        if (parsed is null)
            return null;

        var multiplierText = parsed.Multiplier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return parsed with
        {
            TradeType = tradeType,
            RawText = $"Row {rowIndex}: {parsed.ItemName} | {parsed.Price.ToString(CultureInfo.InvariantCulture)} | {multiplierText} | {tradeType}"
        };
    }

    private ParsedPriceLine? TryParseCombinedLayoutPriceRow(
        int rowIndex,
        string rawText,
        string tradeType)
    {
        return PriceLayoutRowParser.TryParseCombinedLayoutPriceRow(
            rowIndex,
            rawText,
            tradeType,
            _strictTradeGoodMatcher.Find);
    }

    private static string ExtractLayoutRowItemText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        var normalized = rawText
            .Replace("Ã¯Â¼â€¦", "%")
            .Replace("ï¼…", "%")
            .Replace(",", "")
            .Replace(".", " ")
            .Replace("\r", " ")
            .Replace("\n", " ");

        var multiplierMatch = Regex.Match(
            normalized,
            @"(?<mult>\d{1,3})\s*%",
            RegexOptions.CultureInvariant);

        var searchEnd = multiplierMatch.Success
            ? multiplierMatch.Index
            : normalized.Length;

        var beforeMultiplier = normalized[..searchEnd];
        var priceMatches = Regex.Matches(
                beforeMultiplier,
                @"\d+",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToList();

        if (priceMatches.Count > 0)
            beforeMultiplier = beforeMultiplier[..priceMatches[^1].Index];

        return CleanLayoutFieldText(beforeMultiplier);
    }

    private static bool TryParseLayoutRowPrice(
        string rawText,
        out decimal price,
        out decimal multiplier)
    {
        price = 0;
        multiplier = 0;

        var normalized = rawText
            .Replace("ï¼…", "%")
            .Replace("％", "%")
            .Replace(",", "")
            .Replace(".", "");

        var multiplierMatch = Regex.Match(
            normalized,
            @"(?<mult>\d{1,3})\s*%",
            RegexOptions.CultureInvariant);

        if (multiplierMatch.Success &&
            decimal.TryParse(
                multiplierMatch.Groups["mult"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsedMultiplier))
        {
            multiplier = parsedMultiplier;
        }
        else
        {
            return false;
        }

        var beforeMultiplier = normalized[..multiplierMatch.Index];
        var numbers = Regex.Matches(
                beforeMultiplier,
                @"\d+",
                RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .ToList();

        if (numbers.Count == 0)
            return false;

        foreach (var number in numbers.AsEnumerable().Reverse())
        {
            if (decimal.TryParse(
                    number,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out price))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record LayoutRowRead(
        PriceLayoutRowFingerprint? Fingerprint,
        ParsedPriceLine? Parsed)
    {
        public static LayoutRowRead Empty(PriceLayoutRowFingerprint? fingerprint)
        {
            return new LayoutRowRead(fingerprint, null);
        }
    }

    private async Task<string> ReadLayoutBoxTextAsync(
        string kind,
        string source,
        OcrLayoutBox box,
        bool preprocess,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var captureZone = _layoutService.TryGetLayoutBoxZone(box, source);

        if (captureZone is null)
        {
            _logger.LogWarning(
                "Skipped layout OCR box {Source} because the game window could not be resolved.",
                source);

            return string.Empty;
        }

        using var bitmap = _capture.Capture(captureZone);

        if (ShouldSkipOcrByTextPresence(kind, source, "before-preprocess", bitmap, settings))
            return string.Empty;

        if (preprocess)
        {
            var preprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);

            if (preprocessed is not null)
            {
                using (preprocessed)
                {
                    var preprocessedSource = $"{source}-preprocessed";
                    var preprocessedRead = TryReadOcrText(kind, preprocessedSource, preprocessed, settings);
                    if (preprocessedRead is null)
                        return string.Empty;

                    await _debug.SaveAsync(
                        kind,
                        preprocessedSource,
                        preprocessed,
                        preprocessedRead.Text,
                        ct);

                    return preprocessedRead.Text;
                }
            }
        }

        var read = TryReadOcrText(kind, source, bitmap, settings);
        if (read is null)
            return string.Empty;

        await _debug.SaveAsync(
            kind,
            source,
            bitmap,
            read.Text,
            ct);

        return read.Text;
    }

    private static bool LooksLikeBuyText(string raw)
    {
        var normalized = NormalizeOcrMenuText(raw);

        return ContainsNormalizedWord(normalized, "buy") ||
               normalized.Contains("for sale", StringComparison.Ordinal) ||
               normalized.Contains("items for sale", StringComparison.Ordinal);
    }

    private static bool LooksLikeSellText(string raw)
    {
        var normalized = NormalizeOcrMenuText(raw);

        return ContainsNormalizedWord(normalized, "sell") ||
               ContainsNormalizedWord(normalized, "inventory") ||
               ContainsNormalizedWord(normalized, "nventory");
    }

    private static string NormalizeOcrMenuText(string? value)
    {
        // Same idea as item-name normalization:
        // remove newlines, tabs, punctuation, and random OCR symbols;
        // keep only letters, numbers, and spaces;
        // compare in lowercase.
        return NormalizeOcrItemName(value);
    }

    private static bool ContainsNormalizedWord(string normalized, string word)
    {
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        var escaped = Regex.Escape(word.ToLowerInvariant());

        return Regex.IsMatch(
            normalized,
            $@"(^|\s){escaped}($|\s)",
            RegexOptions.CultureInvariant);
    }

    private static string CleanLayoutFieldText(string raw)
    {
        return NormalizeOcrItemName(raw);
    }

    private static string NormalizeOcrItemName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // OCR can return new lines, tabs, punctuation, box-drawing symbols, or random characters.
        // For item-name matching we only keep letters, numbers, and single spaces.
        // Everything is lower-case so matching does not depend on OCR casing.
        var normalized = value
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Normalize();

        normalized = Regex.Replace(
            normalized,
            @"[^\p{L}\p{N}]+",
            " ");

        normalized = Regex.Replace(
            normalized,
            @"\s+",
            " ");

        return normalized
            .Trim()
            .ToLowerInvariant();
    }

    private static bool TryParseLayoutDecimal(
        string raw,
        out decimal value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalized = raw
            .Replace("％", "%")
            .Replace(",", "")
            .Replace(".", "")
            .Replace("%", " ");

        var digits = new string(normalized
            .TakeWhile(c => !char.IsLetter(c))
            .Where(char.IsDigit)
            .ToArray());

        if (string.IsNullOrWhiteSpace(digits))
        {
            digits = new string(normalized
                .Where(char.IsDigit)
                .ToArray());
        }

        return decimal.TryParse(
            digits,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);
    }

    private async Task TryCapturePriceForBatchAsync(
        OcrZone priceZone,
        CityCapture latestCity,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var menuValidationEnabled = settings.PriceMenuValidationEnabled;

        if (menuValidationEnabled)
        {
            var menuIsValid = await TryValidatePriceMenuAsync(
                priceZone,
                settings,
                ct);

            if (!menuIsValid)
            {
                if (settings.OcrBenchmarkLogging)
                {
                    _logger.LogInformation(
                        "Price menu validation rejected current screen. Full price-list capture was skipped.");
                }

                StopPriceFastMode("price-menu-not-detected");
                await FlushPriceBatchAsync(settings, "price-menu-not-detected", ct);
                return;
            }
        }

        var captureZone = menuValidationEnabled && settings.PriceCaptureBodyOnlyAfterMenuValidation
            ? BuildPriceBodyCaptureZone(priceZone, settings.PriceMenuValidationTopPercent)
            : priceZone;

        using var bitmap = _capture.Capture(captureZone);

        var forcePreprocess = settings.PriceForcePreprocess;

        if (forcePreprocess)
        {
            var forcedPreprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);

            if (forcedPreprocessed is not null)
            {
                using (forcedPreprocessed)
                {
                    await AddPriceImageToBatchAsync(
                        forcedPreprocessed,
                        menuValidationEnabled
                            ? "price-batch-body-preprocessed-forced"
                            : "price-batch-preprocessed-forced",
                        latestCity,
                        settings,
                        ct);
                }

                return;
            }

            _logger.LogInformation(
                "PriceForcePreprocess was enabled, but price preprocessing returned null. Capturing direct price image for batch.");
        }

        var preprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);

        if (preprocessed is not null)
        {
            using (preprocessed)
            {
                await AddPriceImageToBatchAsync(
                    preprocessed,
                    menuValidationEnabled
                        ? "price-batch-body-preprocessed"
                        : "price-batch-preprocessed",
                    latestCity,
                    settings,
                    ct);
            }

            return;
        }

        await AddPriceImageToBatchAsync(
            bitmap,
            menuValidationEnabled
                ? "price-batch-body-direct"
                : "price-batch-direct",
            latestCity,
            settings,
            ct);
    }

    private async Task<bool> TryValidatePriceMenuAsync(
        OcrZone priceZone,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var validationZone = BuildPriceMenuValidationZone(
            priceZone,
            settings.PriceMenuValidationTopPercent);

        using var bitmap = _capture.Capture(validationZone);

        Bitmap? preprocessed = null;

        try
        {
            var imageToRead = bitmap;
            var source = "price-menu-validation-direct";

            if (settings.PriceMenuValidationUsePreprocess)
            {
                preprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);

                if (preprocessed is not null)
                {
                    imageToRead = preprocessed;
                    source = "price-menu-validation-preprocessed";
                }
            }

            var read = ReadOcrText("price-menu", source, imageToRead, settings);
            var raw = read.Text;
            var debugPath = await _debug.SaveAsync(
                "price-menu",
                source,
                imageToRead,
                raw,
                ct);

            var isValid = IsValidPriceMenuText(raw, settings);

            if (settings.OcrBenchmarkLogging)
            {
                _logger.LogInformation(
                    "Price menu validation. Valid={Valid}; Source={Source}; RawText={RawText}; DebugImagePath={DebugImagePath}",
                    isValid,
                    source,
                    raw,
                    debugPath);
            }

            return isValid;
        }
        finally
        {
            preprocessed?.Dispose();
        }
    }

    private static bool IsValidPriceMenuText(
        string rawText,
        OcrRuntimeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return false;

        foreach (var validWord in PriceMenuValidationValidWords(settings))
        {
            var escaped = Regex.Escape(validWord);
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){escaped}(?![\p{{L}}\p{{N}}])";

            if (Regex.IsMatch(
                    rawText,
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static OcrZone BuildPriceMenuValidationZone(
        OcrZone priceZone,
        double topPercent)
    {
        var bounds = GetNormalizedBounds(priceZone);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var validationHeight = Math.Max(1, (int)Math.Round(height * Math.Clamp(topPercent, 5, 90) / 100.0));

        return new OcrZone
        {
            Name = "PriceMenuValidation",
            TopLeftX = bounds.Left,
            TopLeftY = bounds.Top,
            BottomRightX = bounds.Right,
            BottomRightY = Math.Min(bounds.Bottom, bounds.Top + validationHeight),
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static OcrZone BuildPriceBodyCaptureZone(
        OcrZone priceZone,
        double topPercent)
    {
        var bounds = GetNormalizedBounds(priceZone);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var validationHeight = Math.Max(1, (int)Math.Round(height * Math.Clamp(topPercent, 5, 90) / 100.0));
        var bodyTop = Math.Min(bounds.Bottom - 1, bounds.Top + validationHeight);

        return new OcrZone
        {
            Name = "PriceBody",
            TopLeftX = bounds.Left,
            TopLeftY = bodyTop,
            BottomRightX = bounds.Right,
            BottomRightY = bounds.Bottom,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static (int Left, int Top, int Right, int Bottom) GetNormalizedBounds(OcrZone zone)
    {
        var left = Math.Min(zone.TopLeftX, zone.BottomRightX);
        var top = Math.Min(zone.TopLeftY, zone.BottomRightY);
        var right = Math.Max(zone.TopLeftX, zone.BottomRightX);
        var bottom = Math.Max(zone.TopLeftY, zone.BottomRightY);

        return (left, top, right, bottom);
    }

    private async Task AddPriceImageToBatchAsync(
        Bitmap image,
        string source,
        CityCapture latestCity,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var options = GetPriceOcrBatchOptions(settings);

        var result = _priceBatch.TryAdd(
            image,
            source,
            latestCity.City,
            options);

        if (result.MaxReached && !result.Added)
        {
            await FlushPriceBatchAsync(settings, "max-size-before-add", ct);

            result = _priceBatch.TryAdd(
                image,
                source,
                latestCity.City,
                options);
        }

        if (settings.OcrBenchmarkLogging)
        {
            _logger.LogInformation(
                "Price batch capture: Decision={Decision}; Added={Added}; Duplicate={Duplicate}; Count={Count}; FullHashMs={FullHashMs}; Source={Source}",
                result.Decision,
                result.Added,
                result.Duplicate,
                result.Count,
                result.FullHashElapsed.TotalMilliseconds,
                source);
        }

        if (result.Added)
            _control.LastPriceReadUtc = DateTime.UtcNow;

        if (result.MaxReached)
            await FlushPriceBatchAsync(settings, "max-size", ct);
    }

    private async Task FlushPriceBatchAsync(
        OcrRuntimeSettings settings,
        string reason,
        CancellationToken ct)
    {
        if (!settings.PriceBatchCaptureEnabled)
            return;

        var images = _priceBatch.Drain();
        if (images.Count == 0)
            return;

        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Flushing deferred price OCR batch. Reason={Reason}; ImageCount={ImageCount}",
            reason,
            images.Count);

        try
        {
            foreach (var image in images)
            {
                var city = new CityCapture
                {
                    City = image.City,
                    RawText = $"Deferred price OCR batch. Reason={reason}",
                    CapturedAtUtc = image.CapturedAtUtc
                };

                await ProcessPriceImageAsync(
                    image.Image,
                    image.Source,
                    city,
                    settings,
                    ct);

                var rememberResult = _priceRecentHashCache.RememberProcessed(
                    image.City,
                    image.FullHash,
                    GetPriceRecentHashCacheOptions(settings));

                if (settings.OcrBenchmarkLogging &&
                    rememberResult.Enabled &&
                    rememberResult.IsKnownCity)
                {
                    _logger.LogInformation(
                        "Remembered processed price image hash. City={City}; CacheCount={CacheCount}; WasAlreadyKnown={WasAlreadyKnown}",
                        image.City,
                        rememberResult.Count,
                        rememberResult.WasHit);
                }
            }
        }
        finally
        {
            foreach (var image in images)
                image.Image.Dispose();

            stopwatch.Stop();

            if (settings.OcrBenchmarkLogging)
            {
                _logger.LogInformation(
                    "Deferred price OCR batch flushed. Reason={Reason}; ImageCount={ImageCount}; ElapsedMs={ElapsedMs}",
                    reason,
                    images.Count,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
    }

    private async Task TryReadPricesImmediateAsync(
        OcrZone priceZone,
        CityCapture latestCity,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        using var bitmap = _capture.Capture(priceZone);

        var forcePreprocess = settings.PriceForcePreprocess;

        if (forcePreprocess)
        {
            var forcedPreprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);

            if (forcedPreprocessed is not null)
            {
                using (forcedPreprocessed)
                {
                    await ProcessPriceImageAsync(
                        image: forcedPreprocessed,
                        source: "preprocessed-forced",
                        latestCity: latestCity,
                        settings: settings,
                        ct: ct);
                }

                return;
            }

            _logger.LogInformation(
                "PriceForcePreprocess was enabled, but price preprocessing returned null. Falling back to direct price OCR.");
        }

        var directRead = ReadOcrText("price", "direct", bitmap, settings);
        var directRaw = directRead.Text;

        var directDebugPath = await _debug.SaveAsync(
            "price",
            "direct",
            bitmap,
            directRaw,
            ct);

        var directPrices = _priceParser.ParseLines(
            directRaw,
            allowPendingCandidates: true);

        var directStrictCount = directPrices.Count(price =>
            _strictTradeGoodMatcher.Find(
                GetStrictTradeGoodSourceText(price.RawText, price.ItemName)) is not null);

        var preprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);
        if (preprocessed is null)
        {
            await ProcessParsedPricesAsync(
                selectedSource: "direct",
                selectedRaw: directRaw,
                selectedDebugPath: directDebugPath,
                parsedPrices: directPrices,
                latestCity: latestCity,
                settings: settings,
                ct: ct);

            return;
        }

        using (preprocessed)
        {
            var preprocessedRead = ReadOcrText("price", "preprocessed", preprocessed, settings);
            var preprocessedRaw = preprocessedRead.Text;

            var preprocessedDebugPath = await _debug.SaveAsync(
                "price",
                "preprocessed",
                preprocessed,
                preprocessedRaw,
                ct);

            var preprocessedPrices = _priceParser.ParseLines(
                preprocessedRaw,
                allowPendingCandidates: true);

            var preprocessedStrictCount = preprocessedPrices.Count(price =>
                _strictTradeGoodMatcher.Find(
                    GetStrictTradeGoodSourceText(price.RawText, price.ItemName)) is not null);

            if (preprocessedStrictCount > directStrictCount ||
                (preprocessedStrictCount == directStrictCount &&
                 preprocessedPrices.Count > directPrices.Count))
            {
                await ProcessParsedPricesAsync(
                    selectedSource: "preprocessed",
                    selectedRaw: preprocessedRaw,
                    selectedDebugPath: preprocessedDebugPath,
                    parsedPrices: preprocessedPrices,
                    latestCity: latestCity,
                    settings: settings,
                    ct: ct);
            }
            else
            {
                await ProcessParsedPricesAsync(
                    selectedSource: "direct",
                    selectedRaw: directRaw,
                    selectedDebugPath: directDebugPath,
                    parsedPrices: directPrices,
                    latestCity: latestCity,
                    settings: settings,
                    ct: ct);
            }
        }
    }

    private async Task ProcessPriceImageAsync(
        Bitmap image,
        string source,
        CityCapture latestCity,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var read = ReadOcrText("price", source, image, settings);
        var raw = read.Text;

        var debugPath = await _debug.SaveAsync(
            "price",
            source,
            image,
            raw,
            ct);

        var parsedPrices = _priceParser.ParseLines(
            raw,
            allowPendingCandidates: true);

        await ProcessParsedPricesAsync(
            selectedSource: source,
            selectedRaw: raw,
            selectedDebugPath: debugPath,
            parsedPrices: parsedPrices,
            latestCity: latestCity,
            settings: settings,
            ct: ct);
    }

    private async Task ProcessParsedPricesAsync(
        string selectedSource,
        string selectedRaw,
        string? selectedDebugPath,
        IReadOnlyList<ParsedPriceLine> parsedPrices,
        CityCapture latestCity,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        _lastResults.SetPrice(
            selectedSource,
            selectedRaw,
            parsedPrices,
            parsedPrices.Count,
            selectedDebugPath);

        if (!string.IsNullOrWhiteSpace(selectedRaw))
        {
            _logger.LogInformation(
                "Price OCR raw from {Source}: {RawText}",
                selectedSource,
                selectedRaw);
        }

        if (parsedPrices.Count == 0)
        {
            await OcrRejectedRowLogWriter.LogPriceRowAsync(
                source: selectedSource,
                city: latestCity.City,
                reason: "No price rows parsed from OCR text",
                rawText: selectedRaw,
                parserItemName: null,
                debugImagePath: selectedDebugPath,
                ct: ct);

            return;
        }

        _control.LastPriceReadUtc = DateTime.UtcNow;

        var hadNewPriceState = false;
        var hadUpdatedExistingState = false;
        var acceptedPrices = new List<(ParsedPriceLine Price, StrictTradeGoodMatch StrictTradeGood, PriceCapture Capture)>();

        foreach (var price in parsedPrices)
        {
            if (!PriceCaptureMergeService.IsKnownTradeType(price.TradeType))
            {
                _logger.LogInformation(
                    "Skipped price because trade type is unknown: {ItemName} {Price} {Multiplier}",
                    price.ItemName,
                    price.Price,
                    price.Multiplier);

                await OcrRejectedRowLogWriter.LogPriceRowAsync(
                    source: selectedSource,
                    city: latestCity.City,
                    reason: "Unknown trade type",
                    rawText: price.RawText,
                    parserItemName: price.ItemName,
                    debugImagePath: selectedDebugPath,
                    ct: ct);

                continue;
            }

            var tradeGoodSourceName = CleanPendingTradeGoodName(price.ItemName);
            var strictTradeGood = _strictTradeGoodMatcher.Find(tradeGoodSourceName);

            if (strictTradeGood is null)
            {
                if (!string.IsNullOrWhiteSpace(tradeGoodSourceName))
                {
                    var pending = _pendingTradeGoodService.AddOrUpdate(
                        new PendingTradeGoodCandidateRequest(
                            Name: tradeGoodSourceName,
                            Confidence: GetPendingTradeGoodConfidence(selectedSource),
                            RawText: price.RawText,
                            TradeType: price.TradeType,
                            Price: price.Price,
                            Multiplier: price.Multiplier));

                    _logger.LogInformation(
                        "Added or updated pending OCR trade-good candidate. Name={Name}; SeenCount={SeenCount}; TradeType={TradeType}; Price={Price}; Multiplier={Multiplier}; RawText={RawText}",
                        pending.Name,
                        pending.SeenCount,
                        pending.LastTradeType,
                        pending.LastPrice,
                        pending.LastMultiplier,
                        pending.LastRawText);
                }
                else
                {
                    _logger.LogInformation(
                        "Skipped pending OCR trade-good candidate because item name was empty or invalid. ParserItem={ParserItemName}; RawText={RawText}",
                        price.ItemName,
                        price.RawText);
                }

                await OcrRejectedRowLogWriter.LogPriceRowAsync(
                    source: selectedSource,
                    city: latestCity.City,
                    reason: "No strict trade-good match; pending candidate added or updated when item name was usable",
                    rawText: price.RawText,
                    parserItemName: price.ItemName,
                    debugImagePath: selectedDebugPath,
                    ct: ct);

                continue;
            }

            if (!string.Equals(
                    strictTradeGood.Name,
                    price.ItemName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Strict trade-good match replaced parser item. ParserItem={ParserItemName}; StrictItem={StrictItemName}; MatchedText={MatchedText}; RawText={RawText}",
                    price.ItemName,
                    strictTradeGood.Name,
                    strictTradeGood.MatchedText,
                    price.RawText);
            }

            var priceCapture = new PriceCapture
            {
                City = latestCity.City,
                ItemName = strictTradeGood.Name,
                TradeGoodType = strictTradeGood.TradeGoodType,
                Price = DecimalToInt(price.Price),
                Multiplier = price.Multiplier,
                TradeType = price.TradeType,
                RawText = price.RawText,
                CapturedAtUtc = DateTime.UtcNow
            };

            acceptedPrices.Add((price, strictTradeGood, priceCapture));
        }

        if (acceptedPrices.Count > 0)
        {
            var mergeResults = await PriceCaptureMergeService.AddOrUpdateBatchAsync(
                _db,
                acceptedPrices.Select(x => x.Capture).ToArray(),
                ct);

            for (var i = 0; i < acceptedPrices.Count; i++)
            {
                var (price, strictTradeGood, _) = acceptedPrices[i];
                var mergeResult = mergeResults[i];

                if (mergeResult.Action == PriceCaptureMergeAction.Added)
                    hadNewPriceState = true;
                else if (mergeResult.Action == PriceCaptureMergeAction.UpdatedExisting)
                    hadUpdatedExistingState = true;

                _logger.LogInformation(
                    "{Action}: {TradeType} {ItemName} {TradeGoodType} {Price} {Multiplier}% {Message}",
                    mergeResult.Action,
                    price.TradeType,
                    strictTradeGood.Name,
                    strictTradeGood.TradeGoodType,
                    price.Price,
                    price.Multiplier,
                    mergeResult.Message);
            }
        }

        if (hadNewPriceState)
        {
            _control.LastPriceStateChangeUtc = DateTime.UtcNow;
            _control.PriceFastModeUntilUtc =
                DateTime.UtcNow.AddSeconds(Math.Max(1, settings.PriceFastModeSeconds));

            _logger.LogInformation(
                "Price fast mode active until {PriceFastModeUntilUtc:O}",
                _control.PriceFastModeUntilUtc);
        }
        else if (hadUpdatedExistingState)
        {
            _logger.LogInformation(
                "Price OCR saw the same latest price state; fast mode was not extended.");
        }
    }

    private OcrCachedTextRead? TryReadOcrText(
        string kind,
        string source,
        Bitmap bitmap,
        OcrRuntimeSettings settings)
    {
        if (ShouldSkipOcrByTextPresence(kind, source, "after-preprocess", bitmap, settings))
            return null;

        return ReadOcrText(kind, source, bitmap, settings);
    }

    private bool ShouldSkipOcrByTextPresence(
        string kind,
        string source,
        string stage,
        Bitmap bitmap,
        OcrRuntimeSettings settings)
    {
        if (!ShouldRunTextPresenceGate(settings, stage))
            return false;

        var stopwatch = Stopwatch.StartNew();
        var result = _textPresenceAnalyzer.Analyze(bitmap, settings);
        stopwatch.Stop();

        if (result.MayContainText)
            return false;

        if (settings.OcrBenchmarkLogging)
        {
            _logger.LogInformation(
                "OCR skipped by text-presence gate. Kind={Kind}; Source={Source}; Stage={Stage}; Contrast={Contrast}; EdgePixelsPercent={EdgePixelsPercent:F3}; SampledPixels={SampledPixels}; GateMs={GateMs}",
                kind,
                source,
                stage,
                result.Contrast,
                result.EdgePixelsPercent,
                result.SampledPixels,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        return true;
    }

    private static bool ShouldRunTextPresenceGate(
        OcrRuntimeSettings settings,
        string stage)
    {
        var mode = settings.OcrTextPresenceGateMode.Trim();

        if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase))
            return false;

        return stage switch
        {
            "before-preprocess" =>
                mode.Equals("BeforePreprocess", StringComparison.OrdinalIgnoreCase) ||
                mode.Equals("BeforeAndAfter", StringComparison.OrdinalIgnoreCase),

            "after-preprocess" =>
                mode.Equals("AfterPreprocess", StringComparison.OrdinalIgnoreCase) ||
                mode.Equals("BeforeAndAfter", StringComparison.OrdinalIgnoreCase),

            _ => false
        };
    }

    private OcrCachedTextRead ReadOcrText(
        string kind,
        string source,
        Bitmap bitmap,
        OcrRuntimeSettings settings)
    {
        var fieldKind = GetOcrFieldKind(kind, source);
        var read = _ocr.ReadText(
            $"{kind}:{source}",
            bitmap,
            fieldKind,
            settings);

        if (settings.OcrBenchmarkLogging)
        {
            _logger.LogInformation(
                "OCR read benchmark. Kind={Kind}; Source={Source}; Decision={Decision}; HashHit={HashHit}; FullHashMs={FullHashMs}; OcrMs={OcrMs}; CacheEntries={CacheEntries}; Evicted={Evicted}",
                kind,
                source,
                read.Decision,
                read.WasHashHit,
                read.FullHashElapsed.TotalMilliseconds,
                read.OcrElapsed.TotalMilliseconds,
                read.CacheEntryCount,
                read.EvictedCount);
        }

        return read;
    }

    private static OcrFieldKind GetOcrFieldKind(string kind, string source)
    {
        if (kind.Contains("coordinate", StringComparison.OrdinalIgnoreCase))
            return OcrFieldKind.Coordinate;

        if (kind.Contains("city", StringComparison.OrdinalIgnoreCase))
            return OcrFieldKind.City;

        if (kind.Contains("validation", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("menu", StringComparison.OrdinalIgnoreCase))
        {
            return OcrFieldKind.PriceMenu;
        }

        if (source.Contains("multiplier", StringComparison.OrdinalIgnoreCase))
            return OcrFieldKind.PriceMultiplier;

        if (source.Contains("price", StringComparison.OrdinalIgnoreCase) &&
            !source.Contains("batch", StringComparison.OrdinalIgnoreCase))
        {
            return OcrFieldKind.PriceNumber;
        }

        if (source.Contains("item-name", StringComparison.OrdinalIgnoreCase))
            return OcrFieldKind.PriceItemName;

        return OcrFieldKind.General;
    }

    private static PriceOcrBatchOptions GetPriceOcrBatchOptions(OcrRuntimeSettings settings)
    {
        var recentHashOptions = GetPriceRecentHashCacheOptions(settings);

        return new PriceOcrBatchOptions(
            Enabled: settings.PriceBatchCaptureEnabled,
            MaxImages: settings.PriceBatchMaxImages,
            BenchmarkLogging: settings.OcrBenchmarkLogging,
            RecentHashCacheEnabled: recentHashOptions.Enabled,
            RecentHashCacheMinutes: recentHashOptions.TtlMinutes,
            RecentHashCacheMaxEntries: recentHashOptions.MaxEntries);
    }

    private static PriceRecentHashCacheOptions GetPriceRecentHashCacheOptions(OcrRuntimeSettings settings)
    {
        return new PriceRecentHashCacheOptions(
            Enabled: settings.PriceRecentHashCacheEnabled,
            TtlMinutes: settings.PriceRecentHashCacheMinutes,
            MaxEntries: settings.PriceRecentHashCacheMaxEntries,
            BenchmarkLogging: settings.OcrBenchmarkLogging);
    }

    private static string GetStrictTradeGoodSourceText(string? rawText, string? parserItemName)
    {
        // With calibrated layout field boxes, the item-name box is the cleanest source.
        // The raw row text may contain row number, price, multiplier, and trade type.
        return !string.IsNullOrWhiteSpace(parserItemName)
            ? parserItemName
            : rawText ?? string.Empty;
    }

    private static string CleanPendingTradeGoodName(string? value)
    {
        var cleaned = NormalizeOcrItemName(value);

        if (cleaned.Length < 3)
            return string.Empty;

        if (Regex.IsMatch(cleaned, @"^\d+$"))
            return string.Empty;

        return cleaned;
    }

    private static double GetPendingTradeGoodConfidence(string source)
    {
        return source.Equals("layout-field-boxes", StringComparison.OrdinalIgnoreCase)
            ? 0.85
            : 0.65;
    }

    private static IReadOnlyList<string> PriceMenuValidationValidWords(OcrRuntimeSettings settings)
    {
        var raw = settings.PriceMenuValidationValidWords;

        return raw
            .Split(
                new[] { '|', ';', ',' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .ToArray();
    }

    private void SetLatestCityUnknownIfNeeded(
        CityCapture? latestCity,
        string coordinateRawText)
    {
        if (latestCity is not null &&
            !PriceCaptureMergeService.IsKnownCity(latestCity.City))
        {
            return;
        }

        _db.CityCaptures.Add(new CityCapture
        {
            City = "Unknown",
            RawText = $"Coordinate visible; leaving city/map mode. Coordinate OCR: {coordinateRawText}",
            CapturedAtUtc = DateTime.UtcNow
        });

        _priceRecentHashCache.NotifyCityStatus("Unknown");
    }

    private async Task AddUniqueCoordinateAsync(
        ParsedCoordinate parsed,
        CancellationToken ct)
    {
        var lastFive = await _db.CoordinateCaptures
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(5)
            .ToListAsync(ct);

        if (lastFive.Any(x => x.X == parsed.X && x.Y == parsed.Y))
            return;

        _db.CoordinateCaptures.Add(new CoordinateCapture
        {
            X = parsed.X,
            Y = parsed.Y,
            RawText = parsed.RawText,
            CapturedAtUtc = DateTime.UtcNow
        });
    }

    private static int DecimalToInt(decimal value)
    {
        return decimal.ToInt32(decimal.Truncate(value));
    }
}

public sealed record StrictTradeGoodMatch(
    string Name,
    string TradeGoodType,
    string MatchedText);

public interface IStrictTradeGoodMatcher
{
    StrictTradeGoodMatch? Find(string text);
}

internal sealed class StrictTradeGoodMatcher : IStrictTradeGoodMatcher
{
    private readonly ITradeGoodCatalog _catalog;
    private readonly object _gate = new();
    private long _cachedCatalogVersion = -1;
    private IReadOnlyList<StrictTradeGoodCandidate> _candidates = Array.Empty<StrictTradeGoodCandidate>();

    public StrictTradeGoodMatcher(ITradeGoodCatalog catalog)
    {
        _catalog = catalog;
    }

    public StrictTradeGoodMatch? Find(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var candidate in GetCandidates())
        {
            if (candidate.Regex.IsMatch(text))
            {
                return new StrictTradeGoodMatch(
                    candidate.Name,
                    candidate.TradeGoodType,
                    candidate.MatchedText);
            }
        }

        return null;
    }

    private IReadOnlyList<StrictTradeGoodCandidate> GetCandidates()
    {
        var version = _catalog.Version;
        if (version == _cachedCatalogVersion)
            return _candidates;

        lock (_gate)
        {
            version = _catalog.Version;
            if (version == _cachedCatalogVersion)
                return _candidates;

            _candidates = BuildCandidates(_catalog.GetAll());
            _cachedCatalogVersion = version;
            return _candidates;
        }
    }

    private static IReadOnlyList<StrictTradeGoodCandidate> BuildCandidates(
        IReadOnlyList<TradeGoodDefinition> goods)
    {
        var candidates = new List<StrictTradeGoodCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var good in goods)
        {
            AddCandidate(candidates, seen, good.Name, good.Name, good.Type);

            foreach (var alias in good.Aliases)
                AddCandidate(candidates, seen, good.Name, alias, good.Type);
        }

        return candidates
            .OrderByDescending(x => x.MatchedText.Length)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddCandidate(
        List<StrictTradeGoodCandidate> candidates,
        HashSet<string> seen,
        string canonicalName,
        string matchedText,
        string type)
    {
        if (string.IsNullOrWhiteSpace(canonicalName) ||
            string.IsNullOrWhiteSpace(matchedText))
        {
            return;
        }

        var key = NormalizeKey(matchedText);
        if (!seen.Add(key))
            return;

        var regex = BuildWholeNameRegex(matchedText);

        candidates.Add(new StrictTradeGoodCandidate(
            canonicalName,
            type,
            matchedText,
            regex));
    }

    private static Regex BuildWholeNameRegex(string name)
    {
        var parts = Regex.Split(name.Trim(), @"\s+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(Regex.Escape);

        var escapedNameWithFlexibleWhitespace = string.Join(@"\s+", parts);

        var pattern = $@"(?<![\p{{L}}\p{{N}}]){escapedNameWithFlexibleWhitespace}(?![\p{{L}}\p{{N}}])";

        return new Regex(
            pattern,
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);
    }

    private static string NormalizeKey(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private sealed record StrictTradeGoodCandidate(
        string Name,
        string TradeGoodType,
        string MatchedText,
        Regex Regex);
}
