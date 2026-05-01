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
        var coordinateWasRead = false;

        if (coordinateZone is not null)
        {
            using var bitmap = capture.Capture(coordinateZone);
            var raw = ocr.DetectText(bitmap);
            var parsed = coordinateParser.TryParse(raw, settings.WorldWidth, settings.WorldHeight);
            if (parsed is not null)
            {
                coordinateWasRead = true;
                _control.LastCoordinateReadUtc = DateTime.UtcNow;
                await AddUniqueCoordinateAsync(db, parsed, ct);
            }
        }

        if (priceZone is not null)
        {
            var latestCity = await db.CityCaptures
                .OrderByDescending(x => x.CapturedAtUtc)
                .FirstOrDefaultAsync(ct);

            var cityKnown = PriceCaptureMergeService.IsKnownCity(latestCity?.City);

            using var bitmap = capture.Capture(priceZone);
            var raw = ocr.DetectText(bitmap);

            if (!string.IsNullOrWhiteSpace(raw))
            {
                Console.WriteLine("=== PRICE OCR RAW ===");
                Console.WriteLine(raw);
                Console.WriteLine("=====================");
            }

            // Important rule:
            // If city is unknown, do not add prices to DB and do not create pending trade-good suggestions.
            if (cityKnown)
            {
                var parsedPrices = priceParser.ParseLines(raw, allowPendingCandidates: true);

                foreach (var price in parsedPrices)
                {
                    // Important rule:
                    // If we do not know whether this is Buy or Sell, do not add it to DB.
                    if (!PriceCaptureMergeService.IsKnownTradeType(price.TradeType))
                    {
                        Console.WriteLine($"Skipped price because trade type is unknown: {price.ItemName} | {price.Price} | {price.Multiplier}%");
                        continue;
                    }

                    var priceCapture = new PriceCapture
                    {
                        City = latestCity!.City,
                        ItemName = price.ItemName,
                        TradeGoodType = price.TradeGoodType,
                        Price = price.Price,
                        Multiplier = price.Multiplier,
                        TradeType = price.TradeType,
                        RawText = price.RawText,
                        CapturedAtUtc = DateTime.UtcNow
                    };

                    var mergeResult = await PriceCaptureMergeService.AddOrUpdateAsync(db, priceCapture, ct);
                    Console.WriteLine($"{mergeResult.Action}: {price.TradeType} {price.ItemName} | {price.TradeGoodType} | {price.Price} | {price.Multiplier}% | {mergeResult.Message}");
                }

                if (parsedPrices.Count > 0)
                    _control.LastPriceReadUtc = DateTime.UtcNow;
            }
            else
            {
                Console.WriteLine("Skipped price OCR parse because current city is unknown.");
            }
        }

        var coordinateRecentlyVisible = _control.LastCoordinateReadUtc is not null &&
            DateTime.UtcNow - _control.LastCoordinateReadUtc.Value < TimeSpan.FromSeconds(settings.CoordinateRecentlyVisibleSeconds);

        var cityDue = _control.LastCityReadUtc is null ||
            DateTime.UtcNow - _control.LastCityReadUtc.Value >= TimeSpan.FromSeconds(settings.CityIntervalSeconds);

        if (!coordinateWasRead && !coordinateRecentlyVisible && cityDue && cityZone is not null)
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
            }
        }

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
