using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;


// DPI fix for PCs using Windows display scaling such as 125%, 150%, etc.
// This helps Cursor.Position and screen capture use the same coordinate system.
try
{
    System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
}
catch
{
    // Some environments may not allow setting DPI mode after startup.
    // The app can still run, and MouseCalibration offsets below can be used if needed.
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("https://localhost:5001", "http://localhost:5000");

builder.Services.Configure<OcrRuntimeSettings>(builder.Configuration.GetSection("OcrSettings"));
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton<OcrControlState>();
builder.Services.AddSingleton<ICoordinateParser, CoordinateParser>();
builder.Services.AddSingleton<ICityCatalog, CityCatalog>();
builder.Services.AddSingleton<ICityParser, CityParser>();
builder.Services.AddSingleton<ITradeGoodCatalog, TradeGoodCatalog>();
builder.Services.AddSingleton<IPendingTradeGoodService, PendingTradeGoodService>();
builder.Services.AddSingleton<IPriceParser, PriceParser>();
builder.Services.Configure<GameWindowSettings>(builder.Configuration.GetSection("GameWindow"));
builder.Services.AddSingleton<IGameWindowLocator, GameWindowLocatorService>();
builder.Services.AddScoped<IWindowRelativeOcrZoneService, WindowRelativeOcrZoneService>();
builder.Services.AddSingleton<IScreenCaptureService, WindowsScreenCaptureService>();
builder.Services.AddSingleton<IPaddleOcrService, PaddleOcrSharpService>();
builder.Services.AddScoped<ITradingRecommendationService, TradingRecommendationService>();
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

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    app = "OCR Trading Backend",
    timeUtc = DateTime.UtcNow
}));

// Mouse position endpoint with optional calibration offset.
// If another PC is still slightly off, edit MouseCalibration in appsettings.json.
app.MapGet("/api/system/mouse-position", (IConfiguration config) =>
{
    var p = System.Windows.Forms.Cursor.Position;

    var offsetX = config.GetValue<int>("MouseCalibration:OffsetX", 0);
    var offsetY = config.GetValue<int>("MouseCalibration:OffsetY", 0);

    return Results.Ok(new
    {
        x = p.X + offsetX,
        y = p.Y + offsetY,
        rawX = p.X,
        rawY = p.Y,
        offsetX,
        offsetY
    });
});

app.MapGet("/api/settings", async (AppDbContext db) => Results.Ok(new
{
    zones = await db.OcrZones.OrderBy(z => z.Name).ToListAsync(),
    settings = await db.AppSettings.ToDictionaryAsync(x => x.Key, x => x.Value)
}));

app.MapPost("/api/settings/ocr-zone", async (
    AppDbContext db,
    IWindowRelativeOcrZoneService zoneService,
    OcrZone zone,
    CancellationToken ct) =>
{
    var saved = await zoneService.SaveZoneAsync(db, zone, ct);
    return Results.Ok(saved);
});

app.MapGet("/api/system/window-under-mouse-delayed", async (int seconds = 5, CancellationToken ct = default) =>
{
    var delaySeconds = Math.Clamp(seconds, 1, 30);

    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);

    var window = MouseWindowScanner.GetWindowUnderMouse();

    return window is null
        ? Results.NotFound(new { message = "No window found under mouse after delay." })
        : Results.Ok(window);
});

app.MapMethods(
    "/api/system/select-window-under-mouse-delayed",
    new[] { "GET", "POST" },
    async (HttpRequest request, CancellationToken ct) =>
    {
        var secondsText = request.Query["seconds"].FirstOrDefault();
        var seconds = int.TryParse(secondsText, out var parsedSeconds)
            ? parsedSeconds
            : 5;

        var delaySeconds = Math.Clamp(seconds, 1, 30);

        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);

        var mouseWindow = MouseWindowScanner.GetWindowUnderMouse();
        var gameWindow = MouseWindowScanner.ToGameWindowInfo(mouseWindow);

        if (gameWindow is null)
        {
            return Results.NotFound(new
            {
                message = "No window found under mouse after delay."
            });
        }

        GameWindowSelectionStore.Set(gameWindow);

        return Results.Ok(GameWindowResponseMapper.ToResponse(gameWindow));
    });




app.MapGet("/api/system/game-window", (IWindowRelativeOcrZoneService zoneService) =>
{
    var window = zoneService.FindWindow();

    return window is null
        ? Results.NotFound(new { message = "Game window not found." })
        : Results.Ok(GameWindowResponseMapper.ToResponse(window));
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

app.MapPost("/api/ocr/start", (OcrControlState c) =>
{
    c.Enabled = true;
    c.LastError = null;
    return Results.Ok(new { c.Enabled });
});

app.MapPost("/api/ocr/stop", (OcrControlState c) =>
{
    c.Enabled = false;
    return Results.Ok(new { c.Enabled });
});

app.MapGet("/api/ocr/status", (OcrControlState c) => Results.Ok(c));

app.MapGet("/api/coordinates/latest", async (AppDbContext db, int take = 5) =>
{
    var limit = Math.Clamp(take, 2, 50);
    var rows = await db.CoordinateCaptures
        .OrderByDescending(x => x.CapturedAtUtc)
        .Take(limit)
        .OrderBy(x => x.CapturedAtUtc)
        .ToListAsync();

    return Results.Ok(rows);
});

app.MapGet("/api/prices/history", async (AppDbContext db, string? city, string? item, string? tradeType, int take = 250) =>
{
    var q = db.PriceCaptures.AsQueryable();

    if (!string.IsNullOrWhiteSpace(city)) q = q.Where(x => x.City == city);
    if (!string.IsNullOrWhiteSpace(item)) q = q.Where(x => x.ItemName.Contains(item));
    if (!string.IsNullOrWhiteSpace(tradeType)) q = q.Where(x => x.TradeType == tradeType);

    return Results.Ok(await q
        .OrderByDescending(x => x.CapturedAtUtc)
        .Take(Math.Clamp(take, 1, 2000))
        .ToListAsync());
});

app.MapGet("/api/cities/latest", async (AppDbContext db) =>
    Results.Ok(await db.CityCaptures.OrderByDescending(x => x.CapturedAtUtc).FirstOrDefaultAsync()));

app.MapGet("/api/cities", (ICityCatalog c) => Results.Ok(c.GetAll()));

app.MapGet("/api/trade-goods", (ITradeGoodCatalog c) => Results.Ok(c.GetAll()));

app.MapGet("/api/trade-goods/suggestions", (ITradeGoodCatalog catalog, string name, int take = 8) =>
{
    return Results.Ok(catalog.SuggestSimilar(name, take));
});

app.MapPost("/api/trade-goods", (ITradeGoodCatalog catalog, AddTradeGoodRequest request) =>
{
    var result = catalog.AddTradeGood(request);
    return result.Added ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/pending-trade-goods", (IPendingTradeGoodService service, bool includeResolved = false) =>
{
    return Results.Ok(service.GetAll(includeResolved));
});

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

app.MapGet("/api/trading/search", async (AppDbContext db, string? city, string? item, string? tradeType, int take = 250) =>
    Results.Ok(await TradingQueryService.SearchAsync(db, city, item, tradeType, take)));

app.MapGet("/api/trading/city-goods", async (AppDbContext db, string city, string? tradeType, int take = 250) =>
    Results.Ok(await TradingQueryService.SearchAsync(db, city, null, tradeType, take)));

app.MapGet("/api/trading/good-locations", async (AppDbContext db, string item, string? tradeType, int take = 250) =>
    Results.Ok(await TradingQueryService.SearchAsync(db, null, item, tradeType, take)));

app.MapGet("/api/trading/recommendations", async (ITradingRecommendationService s) =>
    Results.Ok(await s.GetRecommendationsAsync()));

app.MapGet("/api/export/prices.csv", async (AppDbContext db) =>
    Results.Text(
        CsvExportService.ExportPrices(await db.PriceCaptures.OrderByDescending(x => x.CapturedAtUtc).ToListAsync()),
        "text/csv"));

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

app.Run();
