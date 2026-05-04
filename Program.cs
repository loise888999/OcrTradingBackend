using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

try
{
    System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
}
catch { }

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("https://localhost:5001", "http://localhost:5000");

static IReadOnlyList<string> SplitMulti(string? value) =>
    string.IsNullOrWhiteSpace(value)
        ? Array.Empty<string>()
        : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.Configure<OcrRuntimeSettings>(builder.Configuration.GetSection("OcrSettings"));
builder.Services.Configure<GameWindowSettings>(builder.Configuration.GetSection("GameWindow"));

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton<OcrControlState>();
builder.Services.AddSingleton<OcrLastResultState>();
builder.Services.AddSingleton<ICoordinateParser, CoordinateParser>();
builder.Services.AddSingleton<ICityCatalog, CityCatalog>();
builder.Services.AddSingleton<ICityParser, CityParser>();
builder.Services.AddSingleton<ITradeGoodCatalog, TradeGoodCatalog>();
builder.Services.AddSingleton<IPendingTradeGoodService, PendingTradeGoodService>();
builder.Services.AddSingleton<IPriceParser, PriceParser>();
builder.Services.AddSingleton<IScreenCaptureService, WindowsScreenCaptureService>();
builder.Services.AddSingleton<IPaddleOcrService, PaddleOcrSharpService>();
builder.Services.AddSingleton<IGameWindowLocator, GameWindowLocatorService>();
builder.Services.AddSingleton<IMapRegionCatalog, MapRegionCatalog>();
builder.Services.AddSingleton<IPriceRecentHashCacheService, PriceRecentHashCacheService>();
builder.Services.AddSingleton<IOcrImageHasher, OcrImageHasher>();
builder.Services.AddSingleton<IOcrImageTextCache, OcrImageTextCache>();
builder.Services.AddSingleton<IPriceOcrBatchService, PriceOcrBatchService>();
builder.Services.AddSingleton<IOcrDebugSnapshotService, OcrDebugSnapshotService>();
builder.Services.AddSingleton<IOcrImagePreprocessingService, OcrImagePreprocessingService>();
builder.Services.AddSingleton<IOcrLayoutService, OcrLayoutService>();
builder.Services.AddScoped<IWindowRelativeOcrZoneService, WindowRelativeOcrZoneService>();
builder.Services.AddScoped<ITradingRecommendationService, TradingRecommendationService>();
builder.Services.AddScoped<ITradingAdvancedService, TradingAdvancedService>();
builder.Services.AddScoped<IOcrCycleRunner, OcrCycleRunner>();
builder.Services.AddHostedService<OcrBackgroundWorker>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithOrigins("http://localhost:5173", "http://localhost:5174", "http://localhost:3000")));

var app = builder.Build();
app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", app = "OCR Trading Backend", timeUtc = DateTime.UtcNow }));

app.MapGet("/api/system/mouse-position", (IConfiguration config) =>
{
    var p = System.Windows.Forms.Cursor.Position;
    var offsetX = config.GetValue<int>("MouseCalibration:OffsetX", 0);
    var offsetY = config.GetValue<int>("MouseCalibration:OffsetY", 0);
    return Results.Ok(new { x = p.X + offsetX, y = p.Y + offsetY, rawX = p.X, rawY = p.Y, offsetX, offsetY });
});

app.MapGet("/api/system/game-window", (IWindowRelativeOcrZoneService zoneService) =>
{
    var window = zoneService.FindWindow();
    return window is null
        ? Results.NotFound(new { message = "Game window not found." })
        : Results.Ok(GameWindowResponseMapper.ToResponse(window));
});

app.MapGet("/api/system/window-under-mouse-delayed", async (int seconds = 5, CancellationToken ct = default) =>
{
    var delaySeconds = Math.Clamp(seconds, 1, 30);
    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
    var window = MouseWindowScanner.GetWindowUnderMouse();
    return window is null ? Results.NotFound(new { message = "No window found under mouse after delay." }) : Results.Ok(window);
});

app.MapMethods("/api/system/select-window-under-mouse-delayed", new[] { "GET", "POST" }, async (HttpRequest request, CancellationToken ct) =>
{
    var secondsText = request.Query["seconds"].FirstOrDefault();
    var seconds = int.TryParse(secondsText, out var parsedSeconds) ? parsedSeconds : 5;
    var delaySeconds = Math.Clamp(seconds, 1, 30);

    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);

    var mouseWindow = MouseWindowScanner.GetWindowUnderMouse();
    var gameWindow = MouseWindowScanner.ToGameWindowInfo(mouseWindow);
    if (gameWindow is null) return Results.NotFound(new { message = "No window found under mouse after delay." });

    GameWindowSelectionStore.Set(gameWindow);
    return Results.Ok(GameWindowResponseMapper.ToResponse(gameWindow));
});

app.MapPost("/api/system/clear-selected-game-window", () =>
{
    GameWindowSelectionStore.Clear();
    return Results.Ok(new { cleared = true });
});

app.MapGet("/api/settings", async (AppDbContext db) => Results.Ok(new
{
    zones = await db.OcrZones.OrderBy(z => z.Name).ToListAsync(),
    settings = await db.AppSettings.ToDictionaryAsync(x => x.Key, x => x.Value)
}));

app.MapPost("/api/settings/ocr-zone", async (AppDbContext db, IWindowRelativeOcrZoneService zoneService, OcrZone zone, CancellationToken ct) =>
{
    var saved = await zoneService.SaveZoneAsync(db, zone, ct);
    return Results.Ok(saved);
});

app.MapPost("/api/settings/value", async (AppDbContext db, AppSetting setting) =>
{
    var e = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == setting.Key);
    if (e is null)
    {
        setting.UpdatedAtUtc = DateTime.UtcNow;
        db.AppSettings.Add(setting);
    }
    else
    {
        e.Value = setting.Value;
        e.UpdatedAtUtc = DateTime.UtcNow;
    }
    await db.SaveChangesAsync();
    return Results.Ok(setting);
});

app.MapPost("/api/ocr/start", (OcrControlState c) => { c.Enabled = true; c.LastError = null; return Results.Ok(new { c.Enabled }); });
app.MapPost("/api/ocr/stop", (OcrControlState c) => { c.Enabled = false; return Results.Ok(new { c.Enabled }); });
app.MapGet("/api/ocr/status", (OcrControlState c) => Results.Ok(c));

app.MapGet("/api/ocr/last-results", (OcrLastResultState state) =>
    Results.Ok(state.GetSnapshot()));

app.MapPost("/api/ocr/test/{zoneKind}", async (
    string zoneKind,
    IOcrCycleRunner runner,
    CancellationToken ct) =>
{
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "coordinate",
        "city",
        "price"
    };

    if (!allowed.Contains(zoneKind))
    {
        return Results.BadRequest(new
        {
            message = "Unsupported OCR zone kind.",
            allowed = allowed.OrderBy(x => x).ToArray()
        });
    }

    var result = await runner.TestZoneAsync(zoneKind, ct);
    return Results.Ok(result);
});

app.MapGet("/api/coordinates/latest", async (AppDbContext db, int take = 20) =>
{
    var limit = Math.Clamp(take, 2, 100);
    var rows = await db.CoordinateCaptures.OrderByDescending(x => x.CapturedAtUtc).Take(limit).OrderBy(x => x.CapturedAtUtc).ToListAsync();
    return Results.Ok(rows);
});

app.MapGet("/api/prices/history", async (AppDbContext db, string? city, string? item, string? tradeType, int take = 250) =>
{
    var q = db.PriceCaptures.AsQueryable();
    if (!string.IsNullOrWhiteSpace(city)) q = q.Where(x => x.City == city);
    if (!string.IsNullOrWhiteSpace(item)) q = q.Where(x => x.ItemName.Contains(item));
    if (!string.IsNullOrWhiteSpace(tradeType)) q = q.Where(x => x.TradeType == tradeType);
    return Results.Ok(await q.OrderByDescending(x => x.CapturedAtUtc).Take(Math.Clamp(take, 1, 2000)).ToListAsync());
});

app.MapGet("/api/cities/latest", async (AppDbContext db) => Results.Ok(await db.CityCaptures.OrderByDescending(x => x.CapturedAtUtc).FirstOrDefaultAsync()));
app.MapGet("/api/cities", (ICityCatalog c) => Results.Ok(c.GetAll()));

app.MapPost("/api/cities", (ICityCatalog c, SaveCityRequest request) =>
{
    var result = c.AddCity(request);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPut("/api/cities/{name}", (ICityCatalog c, string name, SaveCityRequest request) =>
{
    var result = c.UpdateCity(name, request);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapDelete("/api/cities/{name}", (ICityCatalog c, string name) =>
{
    var result = c.DeleteCity(name);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/export/cities.csv", (ICityCatalog c) =>
{
    var csv = c.ExportCsv();
    var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
    return Results.File(bytes, "text/csv", "cities.csv");
});

app.MapPost("/api/import/cities.csv", async (
    HttpRequest request,
    ICityCatalog c,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "Expected multipart/form-data with a file field named 'file'." });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");

    if (file is null || file.Length == 0)
        return Results.BadRequest(new { message = "No CSV file was uploaded." });

    await using var stream = file.OpenReadStream();
    var result = await c.ImportCsvAsync(stream, ct);

    return Results.Ok(result);
});

app.MapGet("/api/map-regions", (IMapRegionCatalog catalog) =>
    Results.Ok(catalog.GetAll()));

app.MapPost("/api/map-regions", (IMapRegionCatalog catalog, SaveMapRegionRequest request) =>
{
    var result = catalog.Upsert(request);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPut("/api/map-regions/{id}", (IMapRegionCatalog catalog, string id, SaveMapRegionRequest request) =>
{
    var result = catalog.Upsert(request, id);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapDelete("/api/map-regions/{id}", (IMapRegionCatalog catalog, string id) =>
{
    var result = catalog.Delete(id);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/regions/main", (ICityCatalog c) => Results.Ok(c.GetMainRegions()));
app.MapGet("/api/regions/sub", (ICityCatalog c, string? mainRegion) => Results.Ok(c.GetSubRegions(mainRegion)));
app.MapGet("/api/regions/sea-trade", (ICityCatalog c, string? mainRegion, string? subRegion) => Results.Ok(c.GetSeaTradeRegions(mainRegion, subRegion)));

app.MapGet("/api/trade-goods", (ITradeGoodCatalog c) => Results.Ok(c.GetAll()));
app.MapGet("/api/trade-goods/suggestions", (ITradeGoodCatalog catalog, string name, int take = 8) => Results.Ok(catalog.SuggestSimilar(name, take)));
app.MapPost("/api/trade-goods", (
    ITradeGoodCatalog catalog,
    OcrTradingBackend.Models.AddTradeGoodRequest request) =>
{
    var result = catalog.AddTradeGood(request);
    return result.Added ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/pending-trade-goods", (IPendingTradeGoodService service, bool includeResolved = false) => Results.Ok(service.GetAll(includeResolved)));
app.MapPost("/api/pending-trade-goods/{id}/accept", (IPendingTradeGoodService service, string id, AcceptPendingTradeGoodRequest request) =>
{
    var result = service.Accept(id, request);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});
app.MapPost("/api/pending-trade-goods/{id}/dismiss", (IPendingTradeGoodService service, string id) =>
{
    var result = service.Dismiss(id);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/trading/search", async (AppDbContext db, ICityCatalog cities, string? city, string? item, string? tradeType, string? mainRegion, string? subRegion, string? seaTradeRegion, int take = 250) =>
    Results.Ok(await TradingQueryService.SearchAsync(db, cities, city, item, tradeType, mainRegion, subRegion, seaTradeRegion, take)));

app.MapGet("/api/trading/city-goods", async (AppDbContext db, ICityCatalog cities, string city, string? tradeType, string? mainRegion, string? subRegion, string? seaTradeRegion, int take = 250) =>
    Results.Ok(await TradingQueryService.SearchAsync(db, cities, city, null, tradeType, mainRegion, subRegion, seaTradeRegion, take)));

app.MapGet("/api/trading/good-locations", async (AppDbContext db, ICityCatalog cities, string item, string? tradeType, string? mainRegion, string? subRegion, string? seaTradeRegion, int take = 250) =>
    Results.Ok(await TradingQueryService.SearchAsync(db, cities, null, item, tradeType, mainRegion, subRegion, seaTradeRegion, take)));

app.MapGet("/api/trading/recommendations", async (
    ITradingRecommendationService s,
    string? mainRegion,
    string? subRegion,
    string? seaTradeRegion,
    string? buyMainRegion,
    string? buySubRegion,
    string? buySeaTradeRegion,
    string? sellMainRegion,
    string? sellSubRegion,
    string? sellSeaTradeRegion,
    string? item,
    int routesPerItem = 1,
    int take = 50,
    int minProfit = 1) =>
{
    var filter = new TradingRegionFilter(
        mainRegion,
        subRegion,
        seaTradeRegion,
        buyMainRegion,
        buySubRegion,
        buySeaTradeRegion,
        sellMainRegion,
        sellSubRegion,
        sellSeaTradeRegion,
        item,
        routesPerItem,
        take,
        minProfit);

    return Results.Ok(await s.GetRecommendationsAsync(filter));
});

app.MapGet("/api/export/prices.csv", async (AppDbContext db) =>
{
    var csv = CsvExportService.ExportPrices(
        await db.PriceCaptures
            .OrderByDescending(x => x.CapturedAtUtc)
            .ToListAsync());

    var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
    return Results.File(bytes, "text/csv", "prices.csv");
});

app.MapPost("/api/import/prices.csv", async (HttpRequest request, AppDbContext db, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "Expected multipart/form-data with a file field named 'file'." });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");

    if (file is null || file.Length == 0)
        return Results.BadRequest(new { message = "No CSV file was uploaded." });

    await using var stream = file.OpenReadStream();
    var result = await PriceCsvImportService.ImportAsync(db, stream, ct);

    return Results.Ok(result);
});

app.MapGet("/api/export/trade-goods.csv", (ITradeGoodCatalog catalog) =>
{
    var csv = TradeGoodsCsvService.Export(catalog.GetAll());
    var bytes = System.Text.Encoding.UTF8.GetBytes(csv);

    return Results.File(bytes, "text/csv", "trade-goods.csv");
});

app.MapPost("/api/import/trade-goods.csv", async (
    HttpRequest request,
    ITradeGoodCatalog catalog,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "Expected multipart/form-data with a file field named 'file'." });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");

    if (file is null || file.Length == 0)
        return Results.BadRequest(new { message = "No CSV file was uploaded." });

    await using var stream = file.OpenReadStream();
    var result = await TradeGoodsCsvService.ImportAsync(catalog, stream, ct);

    return Results.Ok(result);
});


app.MapGet("/api/trading/good-lookup", async (
    ITradingAdvancedService service,
    string? item,
    string? type,
    string? mainRegion,
    string? subRegion,
    int take = 250) =>
    Results.Ok(await service.LookupBuyGoodsAsync(item, type, mainRegion, subRegion, take)));

app.MapGet("/api/trading/known-prices", async (
    ITradingAdvancedService service,
    string? item,
    string? type,
    string? tradeType,
    string? mainRegion,
    string? subRegion,
    string? seaTradeRegion,
    int take = 500) =>
    Results.Ok(await service.GetKnownPricesAsync(item, type, tradeType, mainRegion, subRegion, seaTradeRegion, take)));

app.MapGet("/api/trading/advanced-routes", async (
    ITradingAdvancedService service,
    string? item,
    string? type,
    string? buyRegions,
    string? sellRegions,
    int minProfit = 1,
    int routesPerItem = 25,
    int take = 100) =>
    Results.Ok(await service.GetAdvancedRoutesAsync(item, type, SplitMulti(buyRegions), SplitMulti(sellRegions), minProfit, routesPerItem, take)));

app.MapGet("/api/trading/multi-good-routes", async (
    ITradingAdvancedService service,
    string? type,
    string? buyRegions,
    string? sellRegions,
    int minProfitPerGood = 1,
    int minTotalProfit = 1,
    int minItems = 2,
    int take = 100) =>
    Results.Ok(await service.GetMultiGoodRoutesAsync(type, SplitMulti(buyRegions), SplitMulti(sellRegions), minProfitPerGood, minTotalProfit, minItems, take)));

app.MapGet("/api/ocr-layout", async (
    IOcrLayoutService layoutService,
    CancellationToken ct) =>
{
    var layout = await layoutService.LoadAsync(ct);
    return Results.Ok(layout);
});

app.MapPost("/api/ocr-layout", async (
    IOcrLayoutService layoutService,
    SaveOcrLayoutRequest request,
    CancellationToken ct) =>
{
    var saved = await layoutService.SaveLocalAsync(request.Layout, ct);
    return Results.Ok(saved);
});

app.MapPost("/api/ocr-layout/test-box", async (
    OcrLayoutTestBoxRequest request,
    IOcrLayoutService layoutService,
    IScreenCaptureService capture,
    IPaddleOcrService ocr,
    IOcrImagePreprocessingService preprocessor,
    IOcrDebugSnapshotService debug,
    CancellationToken ct) =>
{
    if (request.Box is null || !request.Box.IsValid)
    {
        return Results.BadRequest(new
        {
            message = "Box must have positive width and height."
        });
    }

    var captureZone = layoutService.TryGetLayoutBoxZone(request.Box, request.Kind);

    if (captureZone is null)
    {
        return Results.BadRequest(new
        {
            message = "Could not resolve layout box to screen coordinates. Make sure the game window is selected/found first.",
            coordinateMode = "window-relative-pixels",
            box = request.Box
        });
    }

    using var bitmap = capture.Capture(captureZone);

    if (request.Preprocess)
    {
        var preprocessed = preprocessor.TryPreparePriceImage(bitmap);

        if (preprocessed is not null)
        {
            using (preprocessed)
            {
                var preprocessedRaw = ocr.DetectText(preprocessed);
                var preprocessedDebugPath = await debug.SaveAsync(
                    request.Kind,
                    "layout-test-preprocessed",
                    preprocessed,
                    preprocessedRaw,
                    ct);

                return Results.Ok(new OcrLayoutTestBoxResponse(
                    request.Kind,
                    preprocessedRaw,
                    preprocessedDebugPath,
                    request.Box,
                    captureZone));
            }
        }
    }

    var raw = ocr.DetectText(bitmap);
    var debugPath = await debug.SaveAsync(
        request.Kind,
        "layout-test",
        bitmap,
        raw,
        ct);

    return Results.Ok(new OcrLayoutTestBoxResponse(
        request.Kind,
        raw,
        debugPath,
        request.Box,
        captureZone));
});


app.Run();
