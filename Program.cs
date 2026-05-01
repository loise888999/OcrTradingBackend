using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

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
builder.Services.AddSingleton<IScreenCaptureService, WindowsScreenCaptureService>();
builder.Services.AddSingleton<IPaddleOcrService, PaddleOcrSharpService>();
builder.Services.AddScoped<ITradingRecommendationService, TradingRecommendationService>();
builder.Services.AddHostedService<OcrBackgroundWorker>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().WithOrigins("http://localhost:5173", "http://localhost:5174", "http://localhost:3000")));

var app = builder.Build();
app.UseCors();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", app = "OCR Trading Backend", timeUtc = DateTime.UtcNow }));
app.MapGet("/api/system/mouse-position", () => { var p = System.Windows.Forms.Cursor.Position; return Results.Ok(new { x = p.X, y = p.Y }); });
app.MapGet("/api/settings", async (AppDbContext db) => Results.Ok(new { zones = await db.OcrZones.OrderBy(z => z.Name).ToListAsync(), settings = await db.AppSettings.ToDictionaryAsync(x => x.Key, x => x.Value) }));
app.MapPost("/api/settings/ocr-zone", async (AppDbContext db, OcrZone zone) =>
{
    var e = await db.OcrZones.FirstOrDefaultAsync(x => x.Name == zone.Name);
    if (e is null) { zone.UpdatedAtUtc = DateTime.UtcNow; db.OcrZones.Add(zone); }
    else { e.TopLeftX = zone.TopLeftX; e.TopLeftY = zone.TopLeftY; e.BottomRightX = zone.BottomRightX; e.BottomRightY = zone.BottomRightY; e.UpdatedAtUtc = DateTime.UtcNow; }
    await db.SaveChangesAsync();
    return Results.Ok(zone);
});
app.MapPost("/api/settings/value", async (AppDbContext db, AppSetting setting) =>
{
    var e = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == setting.Key);
    if (e is null) { setting.UpdatedAtUtc = DateTime.UtcNow; db.AppSettings.Add(setting); }
    else { e.Value = setting.Value; e.UpdatedAtUtc = DateTime.UtcNow; }
    await db.SaveChangesAsync();
    return Results.Ok(setting);
});
app.MapPost("/api/ocr/start", (OcrControlState c) => { c.Enabled = true; c.LastError = null; return Results.Ok(new { c.Enabled }); });
app.MapPost("/api/ocr/stop", (OcrControlState c) => { c.Enabled = false; return Results.Ok(new { c.Enabled }); });
app.MapGet("/api/ocr/status", (OcrControlState c) => Results.Ok(c));
app.MapGet("/api/coordinates/latest", async (AppDbContext db) => Results.Ok(await db.CoordinateCaptures.OrderByDescending(x => x.CapturedAtUtc).Take(5).OrderBy(x => x.CapturedAtUtc).ToListAsync()));
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
app.MapGet("/api/trading/search", async (AppDbContext db, string? city, string? item, string? tradeType, int take = 250) => Results.Ok(await TradingQueryService.SearchAsync(db, city, item, tradeType, take)));
app.MapGet("/api/trading/city-goods", async (AppDbContext db, string city, string? tradeType, int take = 250) => Results.Ok(await TradingQueryService.SearchAsync(db, city, null, tradeType, take)));
app.MapGet("/api/trading/good-locations", async (AppDbContext db, string item, string? tradeType, int take = 250) => Results.Ok(await TradingQueryService.SearchAsync(db, null, item, tradeType, take)));
app.MapGet("/api/trading/recommendations", async (ITradingRecommendationService s) => Results.Ok(await s.GetRecommendationsAsync()));
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
app.MapGet("/api/export/prices.csv", async (AppDbContext db) => Results.Text(CsvExportService.ExportPrices(await db.PriceCaptures.OrderByDescending(x => x.CapturedAtUtc).ToListAsync()), "text/csv"));
app.Run();
