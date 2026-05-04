using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;
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
    private readonly IPaddleOcrService _ocr;
    private readonly ICoordinateParser _coordinateParser;
    private readonly ICityParser _cityParser;
    private readonly IPriceParser _priceParser;
    private readonly IWindowRelativeOcrZoneService _zoneService;
    private readonly OcrControlState _control;
    private readonly IOptionsMonitor<OcrRuntimeSettings> _settings;
    private readonly IOcrDebugSnapshotService _debug;
    private readonly IOcrImagePreprocessingService _preprocessor;
    private readonly OcrLastResultState _lastResults;
    private readonly ILogger<OcrCycleRunner> _logger;

    public OcrCycleRunner(
        AppDbContext db,
        IScreenCaptureService capture,
        IPaddleOcrService ocr,
        ICoordinateParser coordinateParser,
        ICityParser cityParser,
        IPriceParser priceParser,
        IWindowRelativeOcrZoneService zoneService,
        OcrControlState control,
        IOptionsMonitor<OcrRuntimeSettings> settings,
        IOcrDebugSnapshotService debug,
        IOcrImagePreprocessingService preprocessor,
        OcrLastResultState lastResults,
        ILogger<OcrCycleRunner> logger)
    {
        _db = db;
        _capture = capture;
        _ocr = ocr;
        _coordinateParser = coordinateParser;
        _cityParser = cityParser;
        _priceParser = priceParser;
        _zoneService = zoneService;
        _control = control;
        _settings = settings;
        _debug = debug;
        _preprocessor = preprocessor;
        _lastResults = lastResults;
        _logger = logger;
    }

    public async Task RunOneCycleAsync(CancellationToken ct)
    {
        var settings = _settings.CurrentValue;

        try
        {
            var storedCoordinateZone = await _db.OcrZones
                .FirstOrDefaultAsync(x => x.Name == settings.CoordinateOcrZoneName, ct);

            var storedCityZone = await _db.OcrZones
                .FirstOrDefaultAsync(x => x.Name == settings.CityOcrZoneName, ct);

            var storedPriceZone = await _db.OcrZones
                .FirstOrDefaultAsync(x => x.Name == settings.PriceOcrZoneName, ct);

            var coordinateZone = await _zoneService.ResolveZoneAsync(_db, storedCoordinateZone, ct);
            var cityZone = await _zoneService.ResolveZoneAsync(_db, storedCityZone, ct);
            var priceZone = await _zoneService.ResolveZoneAsync(_db, storedPriceZone, ct);

            var coordinateWasReadThisCycle = false;

            var latestCityBeforeCoordinate = await _db.CityCaptures
                .OrderByDescending(x => x.CapturedAtUtc)
                .FirstOrDefaultAsync(ct);

            var coordinateRecentlyVisibleBeforeRead =
                _control.LastCoordinateReadUtc is not null &&
                DateTime.UtcNow - _control.LastCoordinateReadUtc.Value <
                TimeSpan.FromSeconds(settings.CoordinateRecentlyVisibleSeconds);

            var wasInKnownCityBeforeCoordinate =
                PriceCaptureMergeService.IsKnownCity(latestCityBeforeCoordinate?.City);

            var ignoreCoordinateJumpThisRead =
                wasInKnownCityBeforeCoordinate &&
                !coordinateRecentlyVisibleBeforeRead;

            if (coordinateZone is not null)
            {
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

                    await AddUniqueCoordinateAsync(parsed, ct);
                    SetLatestCityUnknownIfNeeded(latestCityBeforeCoordinate, parsed.RawText);

                    if (ignoreCoordinateJumpThisRead)
                    {
                        _logger.LogInformation(
                            "Coordinate appeared after known city. Ignored max jump range for this first coordinate read.");
                    }
                }
            }

            var coordinateRecentlyVisible =
                _control.LastCoordinateReadUtc is not null &&
                DateTime.UtcNow - _control.LastCoordinateReadUtc.Value <
                TimeSpan.FromSeconds(settings.CoordinateRecentlyVisibleSeconds);

            var cityDue =
                _control.LastCityReadUtc is null ||
                DateTime.UtcNow - _control.LastCityReadUtc.Value >=
                TimeSpan.FromSeconds(settings.CityIntervalSeconds);

            if (!coordinateWasReadThisCycle &&
                !coordinateRecentlyVisible &&
                cityDue &&
                cityZone is not null)
            {
                var city = await TryReadCityAsync(cityZone, settings, ct);

                if (city is not null)
                {
                    _db.CityCaptures.Add(new CityCapture
                    {
                        City = city,
                        RawText = city,
                        CapturedAtUtc = DateTime.UtcNow
                    });

                    _control.LastCityReadUtc = DateTime.UtcNow;
                }
            }

            if (!coordinateRecentlyVisible && priceZone is not null)
            {
                var latestCity = await _db.CityCaptures
                    .OrderByDescending(x => x.CapturedAtUtc)
                    .FirstOrDefaultAsync(ct);

                if (PriceCaptureMergeService.IsKnownCity(latestCity?.City))
                {
                    var priceDue = IsPriceOcrDue(settings);
                    if (priceDue)
                    {
                        await TryReadPricesAsync(
                            priceZone,
                            latestCity!,
                            settings,
                            ct);
                    }
                }
                else
                {
                    _logger.LogInformation("Skipped price OCR because current city is unknown.");
                }
            }
            else if (coordinateRecentlyVisible)
            {
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

        var zoneName = normalized switch
        {
            "coordinate" => settings.CoordinateOcrZoneName,
            "city" => settings.CityOcrZoneName,
            "price" => settings.PriceOcrZoneName,
            _ => throw new ArgumentException($"Unsupported OCR zone kind: {zoneKind}")
        };

        var storedZone = await _db.OcrZones
            .FirstOrDefaultAsync(x => x.Name == zoneName, ct);

        var zone = await _zoneService.ResolveZoneAsync(_db, storedZone, ct);

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
            "city" => _preprocessor.TryPrepareCityImage(bitmap),
            "price" => _preprocessor.TryPreparePriceImage(bitmap),
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
        var raw = _ocr.DetectText(bitmap);
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
        using (var fixedBitmap = _capture.Capture(coordinateZone))
        {
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

        if (!settings.CoordinateSearchEnabled)
            return null;

        var searchZone = BuildPaddedSearchZone(
            coordinateZone,
            settings.CoordinateSearchPadding);

        using (var searchBitmap = _capture.Capture(searchZone))
        {
            var direct = await TryOcrAndParseCoordinateAsync(
                searchBitmap,
                "search",
                previousCoordinate,
                settings,
                ct);

            if (direct is not null)
                return direct;

            var preprocessed = _preprocessor.TryPrepareCoordinateImage(searchBitmap, settings);
            if (preprocessed is not null)
            {
                using (preprocessed)
                {
                    var result = await TryOcrAndParseCoordinateAsync(
                        preprocessed,
                        "search-preprocessed",
                        previousCoordinate,
                        settings,
                        ct);

                    if (result is not null)
                        return result;
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
        var raw = _ocr.DetectText(bitmap);
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

        var directRaw = _ocr.DetectText(bitmap);
        var directDebugPath = await _debug.SaveAsync("city", "direct", bitmap, directRaw, ct);
        var directCity = _cityParser.TryParse(directRaw, settings.MinCityNameLength);

        _lastResults.SetCity("direct", directRaw, directCity, directDebugPath);

        if (directCity is not null)
            return directCity;

        var preprocessed = _preprocessor.TryPrepareCityImage(bitmap);
        if (preprocessed is null)
            return null;

        using (preprocessed)
        {
            var raw = _ocr.DetectText(preprocessed);
            var debugPath = await _debug.SaveAsync("city", "preprocessed", preprocessed, raw, ct);
            var city = _cityParser.TryParse(raw, settings.MinCityNameLength);

            _lastResults.SetCity("preprocessed", raw, city, debugPath);

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

    private async Task TryReadPricesAsync(
    OcrZone priceZone,
    CityCapture latestCity,
    OcrRuntimeSettings settings,
    CancellationToken ct)
    {
        _control.LastPriceAttemptUtc = DateTime.UtcNow;

        using var bitmap = _capture.Capture(priceZone);

        var raw = _ocr.DetectText(bitmap);
        var debugPath = await _debug.SaveAsync("price", "direct", bitmap, raw, ct);
        var parsedPrices = _priceParser.ParseLines(raw, allowPendingCandidates: true);

        var parsedPricesStrictCount = parsedPrices.Count(price =>
            StrictTradeGoodMatcher.Find(GetStrictTradeGoodSourceText(price.RawText, price.ItemName)) is not null);

        var selectedSource = "direct";
        var selectedRaw = raw;
        var selectedDebugPath = debugPath;

        var preprocessed = _preprocessor.TryPreparePriceImage(bitmap);
        if (preprocessed is not null)
        {
            using (preprocessed)
            {
                var preprocessedRaw = _ocr.DetectText(preprocessed);
                var preprocessedDebugPath = await _debug.SaveAsync(
                    "price",
                    "preprocessed",
                    preprocessed,
                    preprocessedRaw,
                    ct);

                var preprocessedPrices = _priceParser.ParseLines(
                    preprocessedRaw,
                    allowPendingCandidates: true);

                var preprocessedPricesStrictCount = preprocessedPrices.Count(price =>
                    StrictTradeGoodMatcher.Find(GetStrictTradeGoodSourceText(price.RawText, price.ItemName)) is not null);

                if (preprocessedPricesStrictCount > parsedPricesStrictCount ||
                    (preprocessedPricesStrictCount == parsedPricesStrictCount &&
                     preprocessedPrices.Count > parsedPrices.Count))
                {
                    parsedPrices = preprocessedPrices;
                    parsedPricesStrictCount = preprocessedPricesStrictCount;
                    selectedSource = "preprocessed";
                    selectedRaw = preprocessedRaw;
                    selectedDebugPath = preprocessedDebugPath;
                }
            }
        }

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
            return;

        _control.LastPriceReadUtc = DateTime.UtcNow;

        var hadNewPriceState = false;
        var hadUpdatedExistingState = false;

        foreach (var price in parsedPrices)
        {
            if (!PriceCaptureMergeService.IsKnownTradeType(price.TradeType))
            {
                _logger.LogInformation(
                    "Skipped price because trade type is unknown: {ItemName} {Price} {Multiplier}",
                    price.ItemName,
                    price.Price,
                    price.Multiplier);

                continue;
            }

            var strictTradeGood = StrictTradeGoodMatcher.Find(
                GetStrictTradeGoodSourceText(price.RawText, price.ItemName));

            if (strictTradeGood is null)
            {
                _logger.LogInformation(
                    "Skipped price because no strict trade-good match was found. ParserItem={ParserItemName}; RawText={RawText}",
                    price.ItemName,
                    price.RawText);

                continue;
            }

            if (!string.Equals(strictTradeGood.Name, price.ItemName, StringComparison.OrdinalIgnoreCase))
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

            var mergeResult = await PriceCaptureMergeService.AddOrUpdateAsync(
                _db,
                priceCapture,
                ct);

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

    private static string GetStrictTradeGoodSourceText(string? rawText, string? parserItemName)
    {
        return !string.IsNullOrWhiteSpace(rawText)
            ? rawText
            : parserItemName ?? string.Empty;
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

    private static OcrZone BuildPaddedSearchZone(OcrZone zone, int padding)
    {
        var left = Math.Min(zone.TopLeftX, zone.BottomRightX);
        var top = Math.Min(zone.TopLeftY, zone.BottomRightY);
        var right = Math.Max(zone.TopLeftX, zone.BottomRightX);
        var bottom = Math.Max(zone.TopLeftY, zone.BottomRightY);

        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;

        left = Math.Max(virtualScreen.Left, left - padding);
        top = Math.Max(virtualScreen.Top, top - padding);
        right = Math.Min(virtualScreen.Right, right + padding);
        bottom = Math.Min(virtualScreen.Bottom, bottom + padding);

        return new OcrZone
        {
            Name = "CoordinateSearch",
            TopLeftX = left,
            TopLeftY = top,
            BottomRightX = right,
            BottomRightY = bottom,
            UpdatedAtUtc = DateTime.UtcNow
        };
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

internal static class StrictTradeGoodMatcher
{
    private static readonly Lazy<IReadOnlyList<StrictTradeGoodCandidate>> LazyCandidates = new(LoadCandidates);

    public static StrictTradeGoodMatch? Find(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var candidate in LazyCandidates.Value)
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

    private static IReadOnlyList<StrictTradeGoodCandidate> LoadCandidates()
    {
        var csvPath = ResolveTradeGoodsCsvPath();
        if (csvPath is null)
            return Array.Empty<StrictTradeGoodCandidate>();

        var candidates = new List<StrictTradeGoodCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var columns = ParseCsvLine(line);
            if (columns.Count < 2)
                continue;

            var name = columns[0].Trim();
            var type = columns[1].Trim();
            var aliases = columns.Count >= 3 ? columns[2] : string.Empty;

            AddCandidate(candidates, seen, name, name, type);

            foreach (var alias in SplitAliases(aliases))
                AddCandidate(candidates, seen, name, alias, type);
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

        // These boundaries prevent prefix bugs:
        // "Salt" will not match "Saltpeter"
        // "Leather" will not match "Leatherwork"
        var pattern = $@"(?<![\p{{L}}\p{{N}}]){escapedNameWithFlexibleWhitespace}(?![\p{{L}}\p{{N}}])";

        return new Regex(
            pattern,
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);
    }

    private static IEnumerable<string> SplitAliases(string aliases)
    {
        if (string.IsNullOrWhiteSpace(aliases))
            return Array.Empty<string>();

        return aliases
            .Split(
                new[] { '|', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(alias => !string.IsNullOrWhiteSpace(alias));
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Add('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                values.Add(new string(current.ToArray()));
                current.Clear();
                continue;
            }

            current.Add(c);
        }

        values.Add(new string(current.ToArray()));
        return values;
    }

    private static string? ResolveTradeGoodsCsvPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "trade-goods.csv"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "trade-goods.csv")
        };

        return candidates.FirstOrDefault(File.Exists);
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