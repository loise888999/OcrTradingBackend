using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed class OcrBackgroundWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OcrControlState _control;
    private readonly IOptionsMonitor<OcrRuntimeSettings> _settings;
    private readonly ILogger<OcrBackgroundWorker> _logger;

    public OcrBackgroundWorker(
        IServiceScopeFactory scopeFactory,
        OcrControlState control,
        IOptionsMonitor<OcrRuntimeSettings> settings,
        ILogger<OcrBackgroundWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _control = control;
        _settings = settings;
        _logger = logger;
        _control.Enabled = settings.CurrentValue.Enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settings.CurrentValue;

            try
            {
                if (_control.Enabled)
                    await RunOneCycleAsync(settings, stoppingToken);
            }
            catch (Exception ex)
            {
                _control.LastError = ex.Message;
                _logger.LogError(ex, "OCR background cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.DefaultIntervalSeconds)), stoppingToken);
        }
    }

    private async Task RunOneCycleAsync(OcrRuntimeSettings settings, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var capture = scope.ServiceProvider.GetRequiredService<IScreenCaptureService>();
        var ocr = scope.ServiceProvider.GetRequiredService<IPaddleOcrService>();
        var coordinateParser = scope.ServiceProvider.GetRequiredService<ICoordinateParser>();
        var cityParser = scope.ServiceProvider.GetRequiredService<ICityParser>();
        var priceParser = scope.ServiceProvider.GetRequiredService<IPriceParser>();

        var coordinateZone = await db.OcrZones.FirstOrDefaultAsync(x => x.Name == settings.CoordinateOcrZoneName, ct);
        var cityZone = await db.OcrZones.FirstOrDefaultAsync(x => x.Name == settings.CityOcrZoneName, ct);
        var priceZone = await db.OcrZones.FirstOrDefaultAsync(x => x.Name == settings.PriceOcrZoneName, ct);

        var coordinateWasReadThisCycle = false;

        var latestCityBeforeCoordinate = await db.CityCaptures
            .OrderByDescending(x => x.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);

        var coordinateRecentlyVisibleBeforeRead = _control.LastCoordinateReadUtc is not null &&
            DateTime.UtcNow - _control.LastCoordinateReadUtc.Value < TimeSpan.FromSeconds(settings.CoordinateRecentlyVisibleSeconds);

        var wasInKnownCityBeforeCoordinate = PriceCaptureMergeService.IsKnownCity(latestCityBeforeCoordinate?.City);

        // If we were in a known city/menu and coordinates appear again, this is a transition back to sea/map.
        // In that case, ignore the max jump check for this first coordinate because the ship could now be far away
        // from the last coordinate stored before entering the city.
        var ignoreCoordinateJumpThisRead = wasInKnownCityBeforeCoordinate && !coordinateRecentlyVisibleBeforeRead;

        // 1. Coordinate OCR is the priority and uses the main loop interval.
        if (coordinateZone is not null)
        {
            using var bitmap = capture.Capture(coordinateZone);
            var raw = ocr.DetectText(bitmap);

            var previousCoordinate = ignoreCoordinateJumpThisRead
                ? null
                : await db.CoordinateCaptures
                    .OrderByDescending(x => x.CapturedAtUtc)
                    .FirstOrDefaultAsync(ct);

            var parsed = coordinateParser.TryParse(
                raw,
                settings.WorldWidth,
                settings.WorldHeight,
                previousCoordinate,
                new CoordinateCorrectionOptions(
                    settings.EnableCoordinateCorrection,
                    settings.MaxCoordinateJumpX,
                    settings.MaxCoordinateJumpY));

            if (parsed is not null)
            {
                coordinateWasReadThisCycle = true;
                _control.LastCoordinateReadUtc = DateTime.UtcNow;

                await AddUniqueCoordinateAsync(db, parsed, ct);

                // Important new rule:
                // When coordinates are visible, we are no longer inside a city.
                // Set current city to Unknown so price OCR/export/import logic keeps using the existing Unknown safeguards.
                await SetLatestCityUnknownIfNeededAsync(db, latestCityBeforeCoordinate, raw, ct);

                if (ignoreCoordinateJumpThisRead)
                    Console.WriteLine("Coordinate appeared after known city. Ignored max jump range for this first coordinate read.");
            }
        }

        var coordinateRecentlyVisible = _control.LastCoordinateReadUtc is not null &&
            DateTime.UtcNow - _control.LastCoordinateReadUtc.Value < TimeSpan.FromSeconds(settings.CoordinateRecentlyVisibleSeconds);

        // 2. City OCR only runs when coordinates are not visible/recent.
        var cityDue = _control.LastCityReadUtc is null ||
            DateTime.UtcNow - _control.LastCityReadUtc.Value >= TimeSpan.FromSeconds(settings.CityIntervalSeconds);

        if (!coordinateWasReadThisCycle && !coordinateRecentlyVisible && cityDue && cityZone is not null)
        {
            using var bitmap = capture.Capture(cityZone);
            var raw = ocr.DetectText(bitmap);
            var city = cityParser.TryParse(raw, settings.MinCityNameLength);

            if (city is not null)
            {
                db.CityCaptures.Add(new CityCapture
                {
                    City = city,
                    RawText = raw,
                    CapturedAtUtc = DateTime.UtcNow
                });

                _control.LastCityReadUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }

        // 3. Price OCR only runs when we are not at sea/map, current city is known, and price interval is due.
        if (!coordinateRecentlyVisible && priceZone is not null)
        {
            var latestCity = await db.CityCaptures
                .OrderByDescending(x => x.CapturedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (PriceCaptureMergeService.IsKnownCity(latestCity?.City))
            {
                var priceDue = IsPriceOcrDue(settings);
                if (priceDue)
                    await TryReadPricesAsync(db, capture, ocr, priceParser, priceZone, latestCity!, settings, ct);
            }
            else
            {
                Console.WriteLine("Skipped price OCR because current city is unknown.");
            }
        }
        else if (coordinateRecentlyVisible)
        {
            Console.WriteLine("Skipped price OCR because coordinate/map is visible recently; current city is treated as Unknown.");
        }

        await db.SaveChangesAsync(ct);
    }

    private bool IsPriceOcrDue(OcrRuntimeSettings settings)
    {
        var now = DateTime.UtcNow;
        var fastModeActive = _control.PriceFastModeUntilUtc is not null && _control.PriceFastModeUntilUtc.Value > now;

        var intervalSeconds = fastModeActive
            ? Math.Max(1, settings.ActivePriceIntervalSeconds)
            : Math.Max(1, settings.PriceIntervalSeconds);

        if (_control.LastPriceAttemptUtc is null)
            return true;

        return now - _control.LastPriceAttemptUtc.Value >= TimeSpan.FromSeconds(intervalSeconds);
    }

    private async Task TryReadPricesAsync(
        AppDbContext db,
        IScreenCaptureService capture,
        IPaddleOcrService ocr,
        IPriceParser priceParser,
        OcrZone priceZone,
        CityCapture latestCity,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        _control.LastPriceAttemptUtc = DateTime.UtcNow;

        using var bitmap = capture.Capture(priceZone);
        var raw = ocr.DetectText(bitmap);

        if (!string.IsNullOrWhiteSpace(raw))
        {
            Console.WriteLine("=== PRICE OCR RAW ===");
            Console.WriteLine(raw);
            Console.WriteLine("=====================");
        }

        var parsedPrices = priceParser.ParseLines(raw, allowPendingCandidates: true);
        if (parsedPrices.Count == 0)
            return;

        _control.LastPriceReadUtc = DateTime.UtcNow;

        var hadNewPriceState = false;
        var hadUpdatedExistingState = false;

        foreach (var price in parsedPrices)
        {
            if (!PriceCaptureMergeService.IsKnownTradeType(price.TradeType))
            {
                Console.WriteLine($"Skipped price because trade type is unknown: {price.ItemName} | {price.Price} | {price.Multiplier}%");
                continue;
            }

            var priceCapture = new PriceCapture
            {
                City = latestCity.City,
                ItemName = price.ItemName,
                TradeGoodType = price.TradeGoodType,
                Price = price.Price,
                Multiplier = price.Multiplier,
                TradeType = price.TradeType,
                RawText = price.RawText,
                CapturedAtUtc = DateTime.UtcNow
            };

            var mergeResult = await PriceCaptureMergeService.AddOrUpdateAsync(db, priceCapture, ct);

            if (mergeResult.Action == PriceCaptureMergeAction.Added)
                hadNewPriceState = true;
            else if (mergeResult.Action == PriceCaptureMergeAction.UpdatedExisting)
                hadUpdatedExistingState = true;

            Console.WriteLine($"{mergeResult.Action}: {price.TradeType} {price.ItemName} | {price.TradeGoodType} | {price.Price} | {price.Multiplier}% | {mergeResult.Message}");
        }

        if (hadNewPriceState)
        {
            _control.LastPriceStateChangeUtc = DateTime.UtcNow;
            _control.PriceFastModeUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, settings.PriceFastModeSeconds));
            Console.WriteLine($"Price fast mode active until {_control.PriceFastModeUntilUtc:O}");
        }
        else if (hadUpdatedExistingState)
        {
            Console.WriteLine("Price OCR saw the same latest price state; fast mode was not extended.");
        }
    }

    private static async Task SetLatestCityUnknownIfNeededAsync(AppDbContext db, CityCapture? latestCity, string coordinateRawText, CancellationToken ct)
    {
        if (latestCity is not null && !PriceCaptureMergeService.IsKnownCity(latestCity.City))
            return;

        db.CityCaptures.Add(new CityCapture
        {
            City = "Unknown",
            RawText = $"Coordinate visible; leaving city/map mode. Coordinate OCR: {coordinateRawText}",
            CapturedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
    }

    private static async Task AddUniqueCoordinateAsync(AppDbContext db, ParsedCoordinate parsed, CancellationToken ct)
    {
        var lastFive = await db.CoordinateCaptures
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(5)
            .ToListAsync(ct);

        if (lastFive.Any(x => x.X == parsed.X && x.Y == parsed.Y))
            return;

        db.CoordinateCaptures.Add(new CoordinateCapture
        {
            X = parsed.X,
            Y = parsed.Y,
            RawText = parsed.RawText,
            CapturedAtUtc = DateTime.UtcNow
        });
    }
}
