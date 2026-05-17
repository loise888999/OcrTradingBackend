using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

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
    private readonly ICoordinateOcrSettingsService _coordinateOcrSettings;
    private readonly ICoordinateTemplateOcrService _coordinateTemplateOcr;
    private readonly IPriceTradeTypeTemplateSettingsService _priceTradeTypeTemplateSettings;
    private readonly IPriceTradeTypeTemplateOcrService _priceTradeTypeTemplateOcr;
    private readonly IPriceOcrBatchService _priceBatch;
    private readonly IPriceLayoutRowCacheService _priceLayoutRowCache;
    private readonly IPriceLayoutRowFingerprintService _priceLayoutRowFingerprint;
    private readonly IPriceRecentHashCacheService _priceRecentHashCache;
    private readonly OcrLastResultState _lastResults;
    private readonly ILogger<OcrCycleRunner> _logger;
    private readonly CoordinateFarJumpConfirmationGate _coordinateFarJumpGate;
    private readonly ICoordinateStreamService _coordinateStream;

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
        ICoordinateOcrSettingsService coordinateOcrSettings,
        ICoordinateTemplateOcrService coordinateTemplateOcr,
        IPriceTradeTypeTemplateSettingsService priceTradeTypeTemplateSettings,
        IPriceTradeTypeTemplateOcrService priceTradeTypeTemplateOcr,
        IPriceOcrBatchService priceBatch,
        IPriceLayoutRowCacheService priceLayoutRowCache,
        IPriceLayoutRowFingerprintService priceLayoutRowFingerprint,
        IPriceRecentHashCacheService priceRecentHashCache,
        OcrLastResultState lastResults,
        CoordinateFarJumpConfirmationGate coordinateFarJumpGate,
        ICoordinateStreamService coordinateStream,
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
        _coordinateOcrSettings = coordinateOcrSettings;
        _coordinateTemplateOcr = coordinateTemplateOcr;
        _priceTradeTypeTemplateSettings = priceTradeTypeTemplateSettings;
        _priceTradeTypeTemplateOcr = priceTradeTypeTemplateOcr;
        _priceBatch = priceBatch;
        _priceLayoutRowCache = priceLayoutRowCache;
        _priceLayoutRowFingerprint = priceLayoutRowFingerprint;
        _priceRecentHashCache = priceRecentHashCache;
        _lastResults = lastResults;
        _coordinateFarJumpGate = coordinateFarJumpGate;
        _coordinateStream = coordinateStream;
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

            var priceTradeTypeTemplateSettingsForDue =
                _priceTradeTypeTemplateSettings.GetEffective(settings);

            var useFastTradeTypeTemplate =
                IsFastTradeTypeTemplateMode(priceTradeTypeTemplateSettingsForDue);

            var rowPriceDue =
                !coordinateRecentlyVisible &&
                wasInKnownCityBeforeCoordinate &&
                IsPriceOcrDue(settings, ignorePriceFastMode: useFastTradeTypeTemplate);

            var tradeTypeStateChangedToKnown = false;

            var coordinateOcrSettingsForDue =
                _coordinateOcrSettings.GetEffective(settings);

            var coordinateDue =
                coordinateZone is not null &&
                IsCoordinateOcrDue(settings, coordinateOcrSettingsForDue);

            if (!coordinateRecentlyVisible &&
                wasInKnownCityBeforeCoordinate &&
                CanDetectTradeTypeFromLayout(layout))
            {
                if (useFastTradeTypeTemplate)
                {
                    if (IsTradeTypeTemplateProbeDue(priceTradeTypeTemplateSettingsForDue) || rowPriceDue)
                    {
                        var probedTradeType = await ProbeTradeTypeStateAsync(
                            layout,
                            settings,
                            priceTradeTypeTemplateSettingsForDue,
                            ct);

                        _control.LastTradeTypeProbeUtc = DateTime.UtcNow;
                        tradeTypeStateChangedToKnown = UpdateCurrentTradeTypeState(probedTradeType);
                    }

                    detectedTradeType = NormalizeTradeTypeState(_control.CurrentTradeTypeState);
                }
                else if (rowPriceDue)
                {
                    detectedTradeType = await DetectTradeTypeFromLayoutAsync(layout, settings, ct);
                    UpdateCurrentTradeTypeState(detectedTradeType);
                }

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
                    layout.Zones.Coordinate,
                    previousCoordinate,
                    settings,
                    ct);

                if (parsed is not null)
                {
                    coordinateWasReadThisCycle = true;
                    _control.LastCoordinateReadUtc = DateTime.UtcNow;
                    _control.ProbablyAtSea = true;

                    var farJumpDecision = _coordinateFarJumpGate.Evaluate(
                        parsed,
                        previousCoordinate,
                        settings);

                    if (!farJumpDecision.Accepted)
                    {
                        if (farJumpDecision.ResetPending)
                        {
                            _logger.LogInformation(
                                "Coordinate far jump pending reset. X={X}; Y={Y}; Count={Count}/{Required}; RawText={RawText}",
                                parsed.X,
                                parsed.Y,
                                farJumpDecision.PendingCount,
                                farJumpDecision.RequiredCount,
                                parsed.RawText);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Coordinate far jump pending. X={X}; Y={Y}; Count={Count}/{Required}; RawText={RawText}",
                                parsed.X,
                                parsed.Y,
                                farJumpDecision.PendingCount,
                                farJumpDecision.RequiredCount,
                                parsed.RawText);
                        }
                    }
                    else
                    {
                        if (farJumpDecision.AcceptedAfterConfirmation)
                        {
                            _logger.LogInformation(
                                "Coordinate far jump accepted after confirmation. X={X}; Y={Y}; Count={Count}/{Required}; RawText={RawText}",
                                parsed.X,
                                parsed.Y,
                                farJumpDecision.PendingCount,
                                farJumpDecision.RequiredCount,
                                parsed.RawText);
                        }

                        if (await AddUniqueCoordinateAsync(parsed, ct))
                        {
                            _coordinateStream.Publish(parsed);
                        }
                        SetLatestCityUnknownIfNeeded(latestCityBeforeCoordinate, parsed.RawText);

                        if (ignoreCoordinateJumpThisRead)
                        {
                            _logger.LogInformation(
                                "Coordinate appeared after known city. Ignored max jump range for this first coordinate read.");
                        }
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
                    var tradeTypeForPriceRead = useFastTradeTypeTemplate
                        ? NormalizeTradeTypeState(_control.CurrentTradeTypeState)
                        : detectedTradeType;

                    var shouldReadPriceRows = useFastTradeTypeTemplate
                        ? PriceCaptureMergeService.IsKnownTradeType(tradeTypeForPriceRead) &&
                          (tradeTypeStateChangedToKnown || rowPriceDue)
                        : rowPriceDue;

                    if (shouldReadPriceRows)
                    {
                        await TryReadPricesAsync(
                            latestCity!,
                            settings,
                            layout,
                            tradeTypeForPriceRead,
                            ct);

                        if (PriceCaptureMergeService.IsKnownTradeType(tradeTypeForPriceRead))
                            _control.LastPriceReadTradeTypeState = tradeTypeForPriceRead!;
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
        OcrLayoutBox? coordinateBox,
        CoordinateCapture? previousCoordinate,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var coordinateOcrSettings = _coordinateOcrSettings.GetEffective(settings);

        if (coordinateOcrSettings.CoordinateReadMode.Equals(
                CoordinateOcrModes.FastTemplate,
                StringComparison.OrdinalIgnoreCase))
        {
            var fast = TryReadCoordinateWithFastTemplate(
                coordinateZone,
                previousCoordinate,
                settings,
                coordinateOcrSettings);

            if (fast is not null)
                return fast;

            if (!coordinateOcrSettings.CoordinateTemplateFallbackToNormalOcr)
                return null;
        }

        var sw = Stopwatch.StartNew();
        var nomalReadOCR = await TryReadCoordinateWithNormalOcrAsync(
            coordinateZone,
            coordinateBox,
            previousCoordinate,
            coordinateOcrSettings,
            settings,
            ct);
        sw.Stop();
        _logger.LogWarning("Normal OCR ms" + sw.ElapsedMilliseconds);

        return nomalReadOCR;
    }

    private ParsedCoordinate? TryReadCoordinateWithFastTemplate(
        OcrZone coordinateZone,
        CoordinateCapture? previousCoordinate,
        OcrRuntimeSettings settings,
        CoordinateOcrSettingsResponse coordinateOcrSettings)
    {
        using var bitmap = _capture.Capture(coordinateZone);
        var sw = Stopwatch.StartNew();
        var attempt = _coordinateTemplateOcr.TryRead(bitmap, coordinateOcrSettings);
        sw.Stop();
        _logger.LogWarning("Fast OCR ms" + sw.ElapsedMilliseconds);

        if (attempt.Success && attempt.Parsed is not null)
        {
            _coordinateTemplateOcr.ResetFailures();
            _lastResults.SetCoordinate("fast-template", attempt.RawText ?? attempt.Parsed.RawText, attempt.Parsed, null);
            return attempt.Parsed with { RawText = $"fast-template: {attempt.Parsed.RawText}" };
        }

        _lastResults.SetCoordinate("fast-template", attempt.RawText ?? string.Empty, null, null);

        if (coordinateOcrSettings.CoordinateTemplateFallbackToNormalOcr)
        {
            _logger.LogInformation(
                "Fast coordinate OCR failed; falling back to normal OCR. Reason={Reason}; NeedsRecalibration={NeedsRecalibration}",
                attempt.Reason,
                attempt.NeedsRecalibration);
        }

        return null;
    }

    private async Task<ParsedCoordinate?> TryReadCoordinateWithNormalOcrAsync(
        OcrZone coordinateZone,
        OcrLayoutBox? coordinateBox,
        CoordinateCapture? previousCoordinate,
        CoordinateOcrSettingsResponse coordinateOcrSettings,
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
                    {
                        MaybeAddAutoProfileSample(fixedBitmap, coordinateBox, result, coordinateOcrSettings, settings);
                        return result;
                    }
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
                {
                    MaybeAddAutoProfileSample(fixedBitmap, coordinateBox, direct, coordinateOcrSettings, settings);
                    return direct;
                }

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
                        {
                            MaybeAddAutoProfileSample(fixedBitmap, coordinateBox, result, coordinateOcrSettings, settings);
                            return result;
                        }
                    }
                }
            }
        }

        return null;
    }

    private void MaybeAddAutoProfileSample(
    Bitmap sourceBitmap,
    OcrLayoutBox? coordinateBox,
    ParsedCoordinate parsed,
    CoordinateOcrSettingsResponse coordinateOcrSettings,
    OcrRuntimeSettings settings)
    {
        _logger.LogWarning(
            "AUTO PROFILE CHECK: Enabled={Enabled}; ReadMode={ReadMode}; OnlyNormalMode={OnlyNormalMode}; BoxValid={BoxValid}; RequireDigitOcr={RequireDigitOcr}; Parsed={Parsed}",
            coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled,
            coordinateOcrSettings.CoordinateReadMode,
            coordinateOcrSettings.CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode,
            coordinateBox is { IsValid: true },
            coordinateOcrSettings.CoordinateTemplateRequirePerDigitOcrValidation,
            $"{parsed.X},{parsed.Y}");

        if (!coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled)
        {
            _logger.LogWarning("AUTO PROFILE STOPPED: CoordinateTemplateAutoProfileEnabled is false.");
            return;
        }

        if (coordinateOcrSettings.CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode &&
            !coordinateOcrSettings.CoordinateReadMode.Equals(CoordinateOcrModes.NormalOcr, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "AUTO PROFILE STOPPED: CoordinateReadMode is {ReadMode}, but auto profile only runs in NormalOcr mode.",
                coordinateOcrSettings.CoordinateReadMode);

            return;
        }

        if (coordinateBox is not { IsValid: true })
        {
            _logger.LogWarning("AUTO PROFILE STOPPED: coordinate layout box is missing or invalid.");
            return;
        }

        try
        {
            _logger.LogWarning("AUTO PROFILE ENTERING: AddProfileSampleFromNormalOcr.");

            var status = _coordinateTemplateOcr.AddProfileSampleFromNormalOcr(
                sourceBitmap,
                coordinateBox,
                parsed,
                coordinateOcrSettings,
                settings,
                digitCrop => ReadCalibrationDigitOcr(digitCrop, settings));

            _logger.LogWarning(
                "AUTO PROFILE RESULT: Accepted={Accepted}; Learned={Learned}; Missing={Missing}; DigitOcrValidated={Validated}; DigitOcrRejected={Rejected}; Message={Message}",
                status.LastSampleAccepted,
                string.Join(",", status.LastLearnedDigits),
                string.Join(",", status.MissingDigitTemplates),
                string.Join(",", status.LastDigitOcrValidatedDigits),
                string.Join(",", status.LastDigitOcrRejectedDigits),
                status.LastAutoSampleMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AUTO PROFILE ERROR: Failed to add coordinate template sample for parsed coordinate {Coordinate}.",
                $"{parsed.X},{parsed.Y}");
        }
    }


    private string? ReadCalibrationDigitOcr(Bitmap digitCrop, OcrRuntimeSettings settings)
    {
        var attempts = new List<(int Padding, int Scale, int Threshold, bool Cleanup)>
        {
            (2, 2, settings.CoordinateOcrThreshold, false),
            (2, 2, Math.Clamp(settings.CoordinateOcrThreshold - 20, 0, 255), false),
            (2, 2, Math.Clamp(settings.CoordinateOcrThreshold + 20, 0, 255), false),
            (4, 2, settings.CoordinateOcrThreshold, false),
            (2, Math.Clamp(settings.CoordinateOcrUpscale, 1, 6), settings.CoordinateOcrThreshold, false),
            (2, 3, settings.CoordinateOcrThreshold, false),
            (2, 4, settings.CoordinateOcrThreshold, false),
            (2, 2, settings.CoordinateOcrThreshold, true)
        };

        foreach (var attempt in attempts)
        {
            using var padded = AddDigitPadding(digitCrop, attempt.Padding);

            Bitmap? prepared = null;

            try
            {
                prepared = OcrImagePreprocessor.PrepareCoordinateImage(
                    padded,
                    scale: attempt.Scale,
                    threshold: attempt.Threshold,
                    cleanupOptions: attempt.Cleanup
                        ? OcrImagePreprocessor.BuildCoordinateCleanupOptions(settings)
                        : null);

                var read = _ocr.ReadText(
                    "coordinate-template-digit-calibration",
                    prepared,
                    OcrFieldKind.Coordinate,
                    settings).Text;

                var normalized = NormalizeSingleCalibrationDigit(read);

                if (normalized is not null)
                    return normalized;
            }
            finally
            {
                prepared?.Dispose();
            }
        }

        return null;
    }

    private static Bitmap AddDigitPadding(Bitmap source, int padding)
    {
        var output = new Bitmap(
            source.Width + padding * 2,
            source.Height + padding * 2);

        using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.Black);

        graphics.DrawImage(
            source,
            new Rectangle(padding, padding, source.Width, source.Height),
            new Rectangle(0, 0, source.Width, source.Height),
            GraphicsUnit.Pixel);

        return output;
    }

    private static string? NormalizeSingleCalibrationDigit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var digits = raw
            .Where(char.IsDigit)
            .ToArray();

        if (digits.Length == 1)
            return digits[0].ToString();

        if (digits.Length > 1 && digits.Distinct().Count() == 1)
            return digits[0].ToString();

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

        if (parsed is null &&
            settings.CoordinateFarJumpConfirmationEnabled &&
            previousCoordinate is not null)
        {
            parsed = _coordinateParser.TryParse(
                raw,
                settings.WorldWidth,
                settings.WorldHeight);
        }

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

    private bool IsPriceOcrDue(
        OcrRuntimeSettings settings,
        bool ignorePriceFastMode = false)
    {
        var now = DateTime.UtcNow;

        var fastModeActive =
            !ignorePriceFastMode &&
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

    private bool IsTradeTypeTemplateProbeDue(
        PriceTradeTypeTemplateSettingsResponse settings)
    {
        if (_control.LastTradeTypeProbeUtc is null)
            return true;

        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(settings.PriceTradeTypeTemplateProbeIntervalMs, 25, 60_000));

        return DateTime.UtcNow - _control.LastTradeTypeProbeUtc.Value >= interval;
    }

    private bool UpdateCurrentTradeTypeState(string? tradeType)
    {
        var previous = NormalizeTradeTypeState(_control.CurrentTradeTypeState);
        var current = NormalizeTradeTypeState(tradeType);

        _control.CurrentTradeTypeState = current;

        if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
            return false;

        _control.LastTradeTypeStateChangeUtc = DateTime.UtcNow;
        if (PriceCaptureMergeService.IsKnownTradeType(current))
            _control.PriceLayoutRowFifoResetPending = true;

        _logger.LogInformation(
            "Buy/Sell fast state changed. Previous={Previous}; Current={Current}",
            previous,
            current);

        return PriceCaptureMergeService.IsKnownTradeType(current);
    }

    private static bool IsFastTradeTypeTemplateMode(
        PriceTradeTypeTemplateSettingsResponse settings)
        => PriceTradeTypeReadModes
            .Normalize(settings.PriceTradeTypeReadMode)
            .Equals(PriceTradeTypeReadModes.FastTemplate, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTradeTypeState(string? tradeType)
    {
        if (string.Equals(tradeType, "Buy", StringComparison.OrdinalIgnoreCase))
            return "Buy";

        if (string.Equals(tradeType, "Sell", StringComparison.OrdinalIgnoreCase))
            return "Sell";

        return "Unknown";
    }

    private bool IsCoordinateOcrDue(
    OcrRuntimeSettings settings,
    CoordinateOcrSettingsResponse coordinateOcrSettings)
    {
        if (_control.LastCoordinateAttemptUtc is null)
            return true;

        var baseIntervalMs = Math.Clamp(
            settings.CoordinateIntervalMilliseconds,
            250,
            60_000);

        var effectiveIntervalMs = baseIntervalMs;

        if (coordinateOcrSettings.CoordinateReadMode.Equals(
                CoordinateOcrModes.FastTemplate,
                StringComparison.OrdinalIgnoreCase))
        {
            var multiplier = Math.Clamp(
                coordinateOcrSettings.CoordinateTemplateFastModeSpeedMultiplier,
                1,
                50);

            effectiveIntervalMs = Math.Max(
                50,
                baseIntervalMs / multiplier);
        }

        var interval = TimeSpan.FromMilliseconds(effectiveIntervalMs);

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

        var visibleRows = layout.Price.Rows
            .Where(x => x.Enabled)
            .OrderBy(x => x.Index)
            .Take(Math.Max(1, layout.Price.VisibleRows))
            .ToList();

        var parsed = settings.PriceLayoutRowFifoEnabled
            ? await TryReadPricesFromLayoutFifoAsync(visibleRows, tradeType, settings, ct)
            : await TryReadAllLayoutPriceRowsAsync(visibleRows, tradeType, settings, ct);

        var rawRows = parsed
            .Select(x => x.RawText)
            .ToList();

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

    private async Task<List<ParsedPriceLine>> TryReadAllLayoutPriceRowsAsync(
        IReadOnlyList<OcrPriceRowLayout> visibleRows,
        string tradeType,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var parsed = new List<ParsedPriceLine>();

        foreach (var row in visibleRows)
        {
            var parsedRow = await TryReadLayoutPriceRowAsync(
                row,
                tradeType,
                settings,
                ct);

            if (parsedRow.Parsed is not null)
                parsed.Add(parsedRow.Parsed);
        }

        return parsed;
    }

    private async Task<List<ParsedPriceLine>> TryReadPricesFromLayoutFifoAsync(
        IReadOnlyList<OcrPriceRowLayout> visibleRows,
        string tradeType,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        if (visibleRows.Count == 0)
            return new List<ParsedPriceLine>();

        if (settings.PriceLayoutRowFifoResetOnTradeStateChange &&
            _control.PriceLayoutRowFifoResetPending)
        {
            _control.PriceLayoutRowFifoNextIndex = 0;
            _control.PriceLayoutRowFifoResetPending = false;
        }
        else if (!settings.PriceLayoutRowFifoResetOnTradeStateChange)
        {
            _control.PriceLayoutRowFifoResetPending = false;
        }

        var budget = Math.Clamp(settings.PriceLayoutRowsPerCycle, 1, visibleRows.Count);
        var parsedByRow = new Dictionary<int, ParsedPriceLine>();
        var rowsInProbeOrder = PriceLayoutRowFifoPlanner.OrderRows(
            visibleRows,
            _control.PriceLayoutRowFifoNextIndex);

        var consumedBudget = 0;
        var inspectedRows = 0;

        foreach (var row in rowsInProbeOrder)
        {
            if (consumedBudget >= budget)
                break;

            var parsedRow = await TryReadLayoutPriceRowAsync(
                row,
                tradeType,
                settings,
                ct);

            inspectedRows++;

            if (parsedRow.Parsed is not null)
                parsedByRow[row.Index] = parsedRow.Parsed;

            if (parsedRow.ConsumedOcrBudget)
                consumedBudget++;
        }

        _control.PriceLayoutRowFifoNextIndex = PriceLayoutRowFifoPlanner.AdvanceNextIndex(
            _control.PriceLayoutRowFifoNextIndex,
            inspectedRows,
            visibleRows.Count);

        foreach (var row in visibleRows)
        {
            if (parsedByRow.ContainsKey(row.Index))
                continue;

            if (_priceLayoutRowCache.TryGetLatest(
                    GetLayoutRowCacheKey(row.Index, tradeType),
                    tradeType,
                    out var cached))
            {
                var rebased = RebaseLayoutRow(cached, row.Index, tradeType);
                if (rebased is not null)
                    parsedByRow[row.Index] = rebased;
            }
        }

        return visibleRows
            .Where(row => parsedByRow.ContainsKey(row.Index))
            .Select(row => parsedByRow[row.Index])
            .ToList();
    }

    private async Task<string> DetectTradeTypeFromLayoutAsync(
        OcrLayoutSettings layout,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var templateSettings = _priceTradeTypeTemplateSettings.GetEffective(settings);

        if (IsFastTradeTypeTemplateMode(templateSettings))
        {
            return await ProbeTradeTypeStateAsync(
                layout,
                settings,
                templateSettings,
                ct);
        }

        return await DetectTradeTypeFromLayoutOcrAsync(
            layout,
            settings,
            templateSettings,
            learnTemplates: templateSettings.PriceTradeTypeTemplateAutoProfileEnabled,
            ct);
    }

    private async Task<string> ProbeTradeTypeStateAsync(
        OcrLayoutSettings layout,
        OcrRuntimeSettings settings,
        PriceTradeTypeTemplateSettingsResponse templateSettings,
        CancellationToken ct)
    {
        var detection = await TryDetectTradeTypeFromTemplateAsync(
            layout,
            settings,
            templateSettings,
            ct);

        if (PriceCaptureMergeService.IsKnownTradeType(detection.TradeType))
            return detection.TradeType;

        if (detection.ShouldCountFailure)
        {
            _priceTradeTypeTemplateOcr.MaybeCountFailedFastRead(
                templateSettings,
                detection.FailureReason);
        }

        return "Unknown";
    }

    private async Task<string> DetectTradeTypeFromLayoutOcrAsync(
        OcrLayoutSettings layout,
        OcrRuntimeSettings settings,
        PriceTradeTypeTemplateSettingsResponse templateSettings,
        bool learnTemplates,
        CancellationToken ct)
    {
        if (layout.Price.BuyValidationBox is { IsValid: true } buyBox)
        {
            var buyRead = await ReadTradeTypeValidationBoxWithOcrAsync(
                region: "Buy",
                kind: "price-layout-validation",
                source: "buy-validation",
                box: buyBox,
                preprocess: settings.PriceLayoutValidationPreprocess,
                settings: settings,
                ct: ct);

            var matched = LooksLikeBuyText(buyRead.RawText);
            var learned = false;

            if (matched && learnTemplates)
            {
                learned = LearnTradeTypeTemplateFromBox(
                    buyBox,
                    "buy-validation",
                    "Buy",
                    buyRead.RawText,
                    settings,
                    templateSettings,
                    ct);
            }

            RecordTradeTypeValidationAttempt(
                region: "Buy",
                source: buyRead.Source,
                success: matched,
                detectedTradeType: matched ? "Buy" : null,
                score: null,
                threshold: templateSettings.PriceTradeTypeTemplateMaxScore,
                rawText: buyRead.RawText,
                usedNormalOcr: true,
                learnedTemplate: learned,
                reason: matched ? "Normal OCR matched Buy." : buyRead.Reason,
                debugImagePath: buyRead.DebugImagePath);

            if (matched)
                return "Buy";
        }

        if (layout.Price.SellValidationBox is { IsValid: true } sellBox)
        {
            var sellRead = await ReadTradeTypeValidationBoxWithOcrAsync(
                region: "Sell",
                kind: "price-layout-validation",
                source: "sell-validation",
                box: sellBox,
                preprocess: settings.PriceLayoutValidationPreprocess,
                settings: settings,
                ct: ct);

            var matched = LooksLikeSellText(sellRead.RawText);
            var learned = false;

            if (matched && learnTemplates)
            {
                learned = LearnTradeTypeTemplateFromBox(
                    sellBox,
                    "sell-validation",
                    "Sell",
                    sellRead.RawText,
                    settings,
                    templateSettings,
                    ct);
            }

            RecordTradeTypeValidationAttempt(
                region: "Sell",
                source: sellRead.Source,
                success: matched,
                detectedTradeType: matched ? "Sell" : null,
                score: null,
                threshold: templateSettings.PriceTradeTypeTemplateMaxScore,
                rawText: sellRead.RawText,
                usedNormalOcr: true,
                learnedTemplate: learned,
                reason: matched ? "Normal OCR matched Sell." : sellRead.Reason,
                debugImagePath: sellRead.DebugImagePath);

            if (matched)
                return "Sell";
        }

        return "Unknown";
    }

    private async Task<TradeTypeTemplateDetection> TryDetectTradeTypeFromTemplateAsync(
        OcrLayoutSettings layout,
        OcrRuntimeSettings settings,
        PriceTradeTypeTemplateSettingsResponse templateSettings,
        CancellationToken ct)
    {
        var shouldCountFailure = false;
        var failureReasons = new List<string>();
        var profile = _priceTradeTypeTemplateOcr.GetProfileStatus(
            templateSettings.PriceTradeTypeTemplateAutoProfileEnabled);

        foreach (var candidate in BuildTradeTypeProbeOrder(layout))
        {
            var read = await TryReadTradeTypeTemplateBoxAsync(
                region: candidate.Region,
                source: candidate.Source,
                box: candidate.Box,
                settings: settings,
                templateSettings: templateSettings,
                ct: ct);

            if (read.Attempt.Success && read.Attempt.TradeType is not null)
                return new TradeTypeTemplateDetection(read.Attempt.TradeType, false, read.Attempt.Reason);

            if (read.TextVisible && profile.ProfileReady)
                shouldCountFailure = true;

            failureReasons.Add($"{candidate.Region}: {read.Attempt.Reason}");
        }

        var reason = failureReasons.Count == 0
            ? "No valid Buy/Sell validation box is configured."
            : $"Fast Buy/Sell template failed. {string.Join(" ", failureReasons)}";

        return new TradeTypeTemplateDetection("Unknown", shouldCountFailure, reason);
    }

    private IReadOnlyList<TradeTypeProbeCandidate> BuildTradeTypeProbeOrder(
        OcrLayoutSettings layout)
    {
        var candidates = new List<TradeTypeProbeCandidate>(2);
        var current = NormalizeTradeTypeState(_control.CurrentTradeTypeState);

        if (current == "Sell")
        {
            AddSellProbeCandidate(layout, candidates);
            AddBuyProbeCandidate(layout, candidates);
        }
        else
        {
            AddBuyProbeCandidate(layout, candidates);
            AddSellProbeCandidate(layout, candidates);
        }

        return candidates;
    }

    private static void AddBuyProbeCandidate(
        OcrLayoutSettings layout,
        List<TradeTypeProbeCandidate> candidates)
    {
        if (layout.Price.BuyValidationBox is { IsValid: true } buyBox)
        {
            candidates.Add(new TradeTypeProbeCandidate(
                "Buy",
                "buy-validation-fast-template",
                buyBox));
        }
    }

    private static void AddSellProbeCandidate(
        OcrLayoutSettings layout,
        List<TradeTypeProbeCandidate> candidates)
    {
        if (layout.Price.SellValidationBox is { IsValid: true } sellBox)
        {
            candidates.Add(new TradeTypeProbeCandidate(
                "Sell",
                "sell-validation-fast-template",
                sellBox));
        }
    }

    private async Task<LayoutPriceRowRead> TryReadLayoutPriceRowAsync(
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
            return LayoutPriceRowRead.Empty(false);
        }

        var rowRead = await TryReadCombinedLayoutPriceRowAsync(
            row,
            tradeType,
            settings,
            ct);

        if (rowRead.Parsed is not null)
            return new LayoutPriceRowRead(
                rowRead.Parsed,
                rowRead.ConsumedOcrBudget);

        if (!settings.PriceLayoutFieldFallbackEnabled ||
            row.ItemName is not { IsValid: true } itemBox ||
            row.Price is not { IsValid: true } priceBox ||
            row.Multiplier is not { IsValid: true } multiplierBox)
        {
            RememberLayoutRowCache(row.Index, tradeType, rowRead.Fingerprint, null);
            return LayoutPriceRowRead.Empty(rowRead.ConsumedOcrBudget);
        }

        var consumedOcrBudget = true;

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
            return LayoutPriceRowRead.Empty(consumedOcrBudget);
        }

        if (!TryParseLayoutDecimal(priceRaw, out var price))
        {
            RememberLayoutRowCache(row.Index, tradeType, rowRead.Fingerprint, null);
            return LayoutPriceRowRead.Empty(consumedOcrBudget);
        }

        if (!TryParseLayoutDecimal(multiplierRaw, out var multiplier))
        {
            RememberLayoutRowCache(row.Index, tradeType, rowRead.Fingerprint, null);
            return LayoutPriceRowRead.Empty(consumedOcrBudget);
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

        return new LayoutPriceRowRead(parsed, consumedOcrBudget);
    }

    private async Task<LayoutRowRead> TryReadCombinedLayoutPriceRowAsync(
        OcrPriceRowLayout row,
        string tradeType,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        var rowZone = TryGetLayoutRowZone(row);
        if (rowZone is null)
            return LayoutRowRead.Empty(null, false);

        using var bitmap = _capture.Capture(rowZone);

        var source = $"row-{row.Index}-combined";
        if (ShouldSkipOcrByTextPresence("price-layout-row", source, "before-preprocess", bitmap, settings))
            return LayoutRowRead.Empty(null, false);

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
                RebaseLayoutRow(cached, row.Index, tradeType),
                false);
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
                return LayoutRowRead.Empty(fingerprint, false);

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
                return new LayoutRowRead(fingerprint, parsed, !read.WasHashHit);
            }

            if (settings.OcrBenchmarkLogging)
            {
                _logger.LogInformation(
                    "Combined layout row OCR did not parse. Row={RowIndex}; TradeType={TradeType}; RawText={RawText}",
                    row.Index,
                    tradeType,
                    read.Text);
            }

            return LayoutRowRead.Empty(fingerprint, !read.WasHashHit);
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

    private sealed record LayoutRowRead(
        PriceLayoutRowFingerprint? Fingerprint,
        ParsedPriceLine? Parsed,
        bool ConsumedOcrBudget)
    {
        public static LayoutRowRead Empty(
            PriceLayoutRowFingerprint? fingerprint,
            bool consumedOcrBudget)
        {
            return new LayoutRowRead(fingerprint, null, consumedOcrBudget);
        }
    }

    private sealed record LayoutPriceRowRead(
        ParsedPriceLine? Parsed,
        bool ConsumedOcrBudget)
    {
        public static LayoutPriceRowRead Empty(bool consumedOcrBudget)
        {
            return new LayoutPriceRowRead(null, consumedOcrBudget);
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

    private async Task<TradeTypeValidationOcrRead> ReadTradeTypeValidationBoxWithOcrAsync(
        string region,
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
                "Skipped {Region} trade-type OCR box because the game window could not be resolved.",
                region);

            return new TradeTypeValidationOcrRead(
                region,
                source,
                string.Empty,
                null,
                "Game window could not be resolved.");
        }

        using var bitmap = _capture.Capture(captureZone);

        if (ShouldSkipOcrByTextPresence(kind, source, "before-preprocess", bitmap, settings))
        {
            return new TradeTypeValidationOcrRead(
                region,
                source,
                string.Empty,
                null,
                "No visible text in validation box before preprocessing.");
        }

        Bitmap? preprocessed = null;

        try
        {
            var imageToRead = bitmap;
            var readSource = source;

            if (preprocess)
            {
                preprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);

                if (preprocessed is not null)
                {
                    imageToRead = preprocessed;
                    readSource = $"{source}-preprocessed";
                }
            }

            var read = TryReadOcrText(kind, readSource, imageToRead, settings);
            if (read is null)
            {
                return new TradeTypeValidationOcrRead(
                    region,
                    readSource,
                    string.Empty,
                    null,
                    "No visible text in validation box after preprocessing.");
            }

            var debugPath = await _debug.SaveAsync(
                kind,
                readSource,
                imageToRead,
                read.Text,
                ct);

            var reason = string.IsNullOrWhiteSpace(read.Text)
                ? "Normal OCR returned empty text."
                : "Normal OCR text did not match this validation box.";

            return new TradeTypeValidationOcrRead(
                region,
                readSource,
                read.Text,
                debugPath,
                reason);
        }
        finally
        {
            preprocessed?.Dispose();
        }
    }

    private async Task<TradeTypeTemplateBoxRead> TryReadTradeTypeTemplateBoxAsync(
        string region,
        string source,
        OcrLayoutBox box,
        OcrRuntimeSettings settings,
        PriceTradeTypeTemplateSettingsResponse templateSettings,
        CancellationToken ct)
    {
        var captureZone = _layoutService.TryGetLayoutBoxZone(box, source);

        if (captureZone is null)
        {
            var missingWindow = new PriceTradeTypeTemplateReadAttempt(
                false,
                null,
                null,
                "Game window could not be resolved.",
                false);

            RecordTradeTypeValidationAttempt(
                region,
                source,
                success: false,
                detectedTradeType: null,
                score: null,
                threshold: templateSettings.PriceTradeTypeTemplateMaxScore,
                rawText: null,
                usedNormalOcr: false,
                learnedTemplate: false,
                reason: missingWindow.Reason,
                debugImagePath: null);

            return new TradeTypeTemplateBoxRead(missingWindow, TextVisible: false);
        }

        using var bitmap = _capture.Capture(captureZone);
        var visibility = _textPresenceAnalyzer.Analyze(bitmap, settings);

        if (!visibility.MayContainText)
        {
            var notVisible = new PriceTradeTypeTemplateReadAttempt(
                false,
                null,
                null,
                $"No visible text. Contrast={visibility.Contrast}; EdgePixelsPercent={visibility.EdgePixelsPercent:0.###}.",
                false);

            RecordTradeTypeValidationAttempt(
                region,
                source,
                success: false,
                detectedTradeType: null,
                score: null,
                threshold: templateSettings.PriceTradeTypeTemplateMaxScore,
                rawText: null,
                usedNormalOcr: false,
                learnedTemplate: false,
                reason: notVisible.Reason,
                debugImagePath: null);

            return new TradeTypeTemplateBoxRead(notVisible, TextVisible: false);
        }

        Bitmap? preprocessed = null;

        try
        {
            var imageToRead = bitmap;
            var readSource = source;

            if (settings.PriceLayoutValidationPreprocess)
            {
                preprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);

                if (preprocessed is not null)
                {
                    imageToRead = preprocessed;
                    readSource = $"{source}-preprocessed";
                }
            }

            var attempt = _priceTradeTypeTemplateOcr.TryRead(
                imageToRead,
                region,
                templateSettings);

            var debugPath = await _debug.SaveAsync(
                "price-trade-type-template",
                readSource,
                imageToRead,
                attempt.Reason,
                ct);

            RecordTradeTypeValidationAttempt(
                region,
                readSource,
                success: attempt.Success,
                detectedTradeType: attempt.TradeType,
                score: attempt.Score,
                threshold: templateSettings.PriceTradeTypeTemplateMaxScore,
                rawText: null,
                usedNormalOcr: false,
                learnedTemplate: false,
                reason: attempt.Reason,
                debugImagePath: debugPath);

            return new TradeTypeTemplateBoxRead(attempt, TextVisible: true);
        }
        finally
        {
            preprocessed?.Dispose();
        }
    }

    private bool LearnTradeTypeTemplateFromBox(
        OcrLayoutBox box,
        string source,
        string tradeType,
        string rawText,
        OcrRuntimeSettings settings,
        PriceTradeTypeTemplateSettingsResponse templateSettings,
        CancellationToken ct)
    {
        var before = _priceTradeTypeTemplateOcr.GetProfileStatus(
            templateSettings.PriceTradeTypeTemplateAutoProfileEnabled);

        var captureZone = _layoutService.TryGetLayoutBoxZone(box, $"{source}-template-learn");
        if (captureZone is null)
            return false;

        using var bitmap = _capture.Capture(captureZone);
        var visibility = _textPresenceAnalyzer.Analyze(bitmap, settings);
        if (!visibility.MayContainText)
            return false;

        Bitmap? preprocessed = null;

        try
        {
            var imageToLearn = bitmap;

            if (settings.PriceLayoutValidationPreprocess)
            {
                preprocessed = _preprocessor.TryPreparePriceImage(bitmap, settings);

                if (preprocessed is not null)
                    imageToLearn = preprocessed;
            }

            var after = _priceTradeTypeTemplateOcr.AddProfileSampleFromNormalOcr(
                imageToLearn,
                box,
                tradeType,
                templateSettings,
                rawText);

            var beforeCount = tradeType.Equals("Buy", StringComparison.OrdinalIgnoreCase)
                ? before.BuyTemplateCount
                : before.SellTemplateCount;
            var afterCount = tradeType.Equals("Buy", StringComparison.OrdinalIgnoreCase)
                ? after.BuyTemplateCount
                : after.SellTemplateCount;

            return afterCount > beforeCount;
        }
        finally
        {
            preprocessed?.Dispose();
        }
    }

    private void RecordTradeTypeValidationAttempt(
        string region,
        string source,
        bool success,
        string? detectedTradeType,
        double? score,
        double threshold,
        string? rawText,
        bool usedNormalOcr,
        bool learnedTemplate,
        string reason,
        string? debugImagePath)
    {
        _priceTradeTypeTemplateOcr.RecordAttempt(new PriceTradeTypeTemplateAttemptLog(
            CapturedAtUtc: DateTime.UtcNow,
            Region: region,
            Source: source,
            Success: success,
            DetectedTradeType: detectedTradeType,
            Score: score,
            Threshold: threshold,
            RawText: rawText,
            UsedNormalOcr: usedNormalOcr,
            LearnedTemplate: learnedTemplate,
            Reason: reason,
            DebugImagePath: debugImagePath));
    }

    private sealed record TradeTypeTemplateDetection(
        string TradeType,
        bool ShouldCountFailure,
        string FailureReason);

    private sealed record TradeTypeProbeCandidate(
        string Region,
        string Source,
        OcrLayoutBox Box);

    private sealed record TradeTypeTemplateBoxRead(
        PriceTradeTypeTemplateReadAttempt Attempt,
        bool TextVisible);

    private sealed record TradeTypeValidationOcrRead(
        string Region,
        string Source,
        string RawText,
        string? DebugImagePath,
        string Reason);

    private static bool LooksLikeBuyText(string raw)
        => TradeTypeMenuTextDetector.LooksLikeBuy(raw);

    private static bool LooksLikeSellText(string raw)
        => TradeTypeMenuTextDetector.LooksLikeSell(raw);

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

    private async Task<bool> AddUniqueCoordinateAsync(
        ParsedCoordinate parsed,
        CancellationToken ct)
    {
        var lastFive = await _db.CoordinateCaptures
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(5)
            .ToListAsync(ct);

        if (lastFive.Any(x => x.X == parsed.X && x.Y == parsed.Y))
            return false;

        _db.CoordinateCaptures.Add(new CoordinateCapture
        {
            X = parsed.X,
            Y = parsed.Y,
            RawText = parsed.RawText,
            CapturedAtUtc = DateTime.UtcNow
        });

        return true;
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
