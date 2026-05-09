using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;
using System.Drawing.Imaging;

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

static OcrFieldKind GetLayoutTestFieldKind(string kind)
{
    var normalized = kind.Trim().ToLowerInvariant();

    if (normalized.Contains("coordinate"))
        return OcrFieldKind.Coordinate;
    if (normalized.Contains("city"))
        return OcrFieldKind.City;
    if (normalized.Contains("validation") || normalized.Contains("menu") || normalized.Contains("buy") || normalized.Contains("sell"))
        return OcrFieldKind.PriceMenu;
    if (normalized.Contains("multiplier"))
        return OcrFieldKind.PriceMultiplier;
    if (normalized.Contains("price"))
        return OcrFieldKind.PriceNumber;
    if (normalized.Contains("item"))
        return OcrFieldKind.PriceItemName;

    return OcrFieldKind.General;
}

static Bitmap? TryPrepareLayoutTestImage(
    IOcrImagePreprocessingService preprocessor,
    Bitmap bitmap,
    OcrFieldKind fieldKind,
    OcrRuntimeSettings settings)
{
    return fieldKind switch
    {
        OcrFieldKind.Coordinate => preprocessor.TryPrepareCoordinateImage(bitmap, settings),
        OcrFieldKind.City => preprocessor.TryPrepareCityImage(bitmap, settings),
        _ => preprocessor.TryPreparePriceImage(bitmap, settings)
    };
}

static string? BuildOcrDebugImageUrl(string? debugImagePath)
{
    if (string.IsNullOrWhiteSpace(debugImagePath))
        return null;

    return $"/api/ocr-debug-image?path={Uri.EscapeDataString(debugImagePath.Replace('\\', '/'))}";
}

static string EncodePngDataUrl(Bitmap bitmap)
{
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}";
}

static (double Score, string Status, string Message, string? ParsedText) ScoreLayoutTestBox(
    string kind,
    OcrFieldKind fieldKind,
    string rawText,
    OcrRuntimeSettings settings,
    ICoordinateParser coordinateParser,
    ICityParser cityParser,
    IStrictTradeGoodMatcher strictTradeGoodMatcher)
{
    if (string.IsNullOrWhiteSpace(rawText))
        return (0, "fail", "No OCR text detected.", null);

    if (fieldKind == OcrFieldKind.Coordinate)
    {
        var parsed = coordinateParser.TryParse(rawText, settings.WorldWidth, settings.WorldHeight);
        return parsed is null
            ? (0.35, "warn", "Text detected, but coordinate did not parse.", null)
            : (1, "pass", "Coordinate parsed.", $"{parsed.X},{parsed.Y}");
    }

    if (fieldKind == OcrFieldKind.City)
    {
        var city = cityParser.TryParse(rawText, settings.MinCityNameLength);
        return city is null
            ? (0.5, "warn", "Text detected, but city did not match known city.", null)
            : (1, "pass", "City parsed.", city);
    }

    if (kind.StartsWith("row-", StringComparison.OrdinalIgnoreCase))
    {
        var parsed = PriceLayoutRowParser.TryParseCombinedLayoutPriceRow(
            0,
            rawText,
            "Buy",
            strictTradeGoodMatcher.Find);

        return parsed is null
            ? (0.35, "warn", "Text detected, but whole row did not parse item + price + multiplier.", null)
            : (1, "pass", "Whole row parsed.", $"{parsed.ItemName} {parsed.Price} {parsed.Multiplier}%");
    }

    return (0.75, "pass", "OCR text detected.", rawText.Trim());
}

builder.Services.AddSingleton<IValidateOptions<OcrRuntimeSettings>, OcrRuntimeSettingsValidator>();
builder.Services.AddOptions<OcrRuntimeSettings>()
    .Bind(builder.Configuration.GetSection("OcrSettings"))
    .ValidateOnStart();
builder.Services.Configure<GameWindowSettings>(builder.Configuration.GetSection("GameWindow"));

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton<OcrControlState>();
builder.Services.AddSingleton<OcrLastResultState>();
builder.Services.AddSingleton<ICoordinateParser, CoordinateParser>();
builder.Services.AddSingleton<CoordinateFarJumpConfirmationGate>();
builder.Services.AddSingleton<ICityCatalog, CityCatalog>();
builder.Services.AddSingleton<ICityParser, CityParser>();
builder.Services.AddSingleton<ITradeGoodCatalog, TradeGoodCatalog>();
builder.Services.AddSingleton<IStrictTradeGoodMatcher, StrictTradeGoodMatcher>();
builder.Services.AddSingleton<IPendingTradeGoodService, PendingTradeGoodService>();
builder.Services.AddSingleton<IPriceParser, PriceParser>();
builder.Services.AddSingleton<IScreenCaptureService, WindowsScreenCaptureService>();
builder.Services.AddSingleton<IPaddleOcrService, PaddleOcrSharpService>();
builder.Services.AddSingleton<IGameWindowLocator, GameWindowLocatorService>();
builder.Services.AddSingleton<IMapRegionCatalog, MapRegionCatalog>();
builder.Services.AddSingleton<IPriceRecentHashCacheService, PriceRecentHashCacheService>();
builder.Services.AddSingleton<IOcrImageHasher, OcrImageHasher>();
builder.Services.AddSingleton<IOcrImageTextCache, OcrImageTextCache>();
builder.Services.AddSingleton<IOcrCachedTextService, OcrCachedTextService>();
builder.Services.AddSingleton<IPriceOcrBatchService, PriceOcrBatchService>();
builder.Services.AddSingleton<IPriceLayoutRowFingerprintService, PriceLayoutRowFingerprintService>();
builder.Services.AddSingleton<IPriceLayoutRowCacheService, PriceLayoutRowCacheService>();
builder.Services.AddSingleton<IOcrDebugSnapshotService, OcrDebugSnapshotService>();
builder.Services.AddSingleton<IOcrImagePreprocessingService, OcrImagePreprocessingService>();
builder.Services.AddSingleton<IOcrTextPresenceAnalyzer, OcrTextPresenceAnalyzer>();
builder.Services.AddSingleton<IOcrLayoutService, OcrLayoutService>();
builder.Services.AddSingleton<ICoordinateOcrSettingsService, CoordinateOcrSettingsService>();
builder.Services.AddSingleton<ICoordinateTemplateOcrService, CoordinateTemplateOcrService>();
builder.Services.AddScoped<IOcrCalibrationService, OcrCalibrationService>();
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

app.MapGet("/api/system/game-window", (IGameWindowLocator windowLocator) =>
{
    var result = windowLocator.FindWindowWithSource();
    return result is null
        ? Results.NotFound(new { message = "Game window not found." })
        : Results.Ok(GameWindowResponseMapper.ToResponse(result.Window, result.SelectionSource));
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

app.MapPost("/api/system/forget-remembered-game-window", () =>
{
    GameWindowSelectionStore.ForgetRemembered();
    return Results.Ok(new { forgotten = true });
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

app.MapGet("/api/settings/coordinate-ocr", (
    ICoordinateOcrSettingsService coordinateOcrSettings) =>
    Results.Ok(coordinateOcrSettings.Get()));

app.MapPost("/api/settings/coordinate-ocr", async (
    UpdateCoordinateOcrSettingsRequest request,
    ICoordinateOcrSettingsService coordinateOcrSettings,
    CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(request.CoordinateReadMode) &&
        !CoordinateOcrModes.IsValid(request.CoordinateReadMode))
    {
        return Results.BadRequest(new
        {
            message = "CoordinateReadMode must be NormalOcr or FastTemplate."
        });
    }

    var updated = await coordinateOcrSettings.UpdateAsync(request, ct);
    return Results.Ok(updated);
});

app.MapGet("/api/settings/coordinate-ocr/status", (
    ICoordinateOcrSettingsService coordinateOcrSettings,
    ICoordinateTemplateOcrService templateOcr) =>
{
    var settings = coordinateOcrSettings.Get();
    return Results.Ok(new
    {
        settings,
        fastTemplate = templateOcr.GetStatus(),
        profile = templateOcr.GetProfileStatus(settings.CoordinateTemplateAutoProfileEnabled)
    });
});

app.MapGet("/api/coordinate-template/profile/status", (
    ICoordinateOcrSettingsService coordinateOcrSettings,
    ICoordinateTemplateOcrService templateOcr) =>
{
    var settings = coordinateOcrSettings.Get();

    return Results.Ok(
        templateOcr.GetProfileStatus(
            settings.CoordinateTemplateAutoProfileEnabled));
});

app.MapDelete("/api/coordinate-template/profile", (
    ICoordinateTemplateOcrService templateOcr) =>
{
    templateOcr.DeleteProfile();

    return Results.Ok(new
    {
        deleted = true,
        message = "Coordinate template profile deleted. Start auto build again to relearn digits."
    });
});

app.MapPost("/api/coordinate-template/profile/reset", (
    ICoordinateTemplateOcrService templateOcr) =>
{
    templateOcr.DeleteProfile();

    return Results.Ok(new
    {
        deleted = true,
        message = "Coordinate template profile reset. Start auto build again to relearn digits."
    });
});



app.MapPost("/api/coordinate-template/profile/auto/start", async (
    ICoordinateOcrSettingsService coordinateOcrSettings,
    CancellationToken ct) =>
{
    var settings = await coordinateOcrSettings.UpdateAsync(
        new UpdateCoordinateOcrSettingsRequest(
            CoordinateReadMode: CoordinateOcrModes.NormalOcr,
            CoordinateTemplateFallbackToNormalOcr: true,
            CoordinateTemplateCountFailedReadsForRecalibration: null,
            CoordinateTemplateRecalibrationFailureLimit: null,
            CoordinateTemplateRequireVisibleTextForFailure: null,
            CoordinateTemplateMinTextPixelsPercent: null,
            CoordinateTemplateMinContrast: null,
            CoordinateTemplateAutoProfileEnabled: true,
            CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode: true,
            CoordinateTemplateAutoProfileMaxSamples: 10000,
            CoordinateTemplateAutoProfileValidationMaxDigitScore: null,
            CoordinateTemplateMaxTemplatesPerDigit: 1,
            CoordinateTemplateRequirePerDigitOcrValidation: true),
        ct);

    return Results.Ok(new
    {
        message = "Auto profile calibration started. Move in-game until all digits 0-9 are learned.",
        settings
    });
});

app.MapPost("/api/coordinate-template/profile/auto/stop", async (
    ICoordinateOcrSettingsService coordinateOcrSettings,
    CancellationToken ct) =>
{
    var settings = await coordinateOcrSettings.UpdateAsync(
        new UpdateCoordinateOcrSettingsRequest(
            CoordinateReadMode: null,
            CoordinateTemplateFallbackToNormalOcr: null,
            CoordinateTemplateCountFailedReadsForRecalibration: null,
            CoordinateTemplateRecalibrationFailureLimit: null,
            CoordinateTemplateRequireVisibleTextForFailure: null,
            CoordinateTemplateMinTextPixelsPercent: null,
            CoordinateTemplateMinContrast: null,
            CoordinateTemplateAutoProfileEnabled: false,
            CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode: null,
            CoordinateTemplateAutoProfileMaxSamples: null,
            CoordinateTemplateAutoProfileValidationMaxDigitScore: null,
            CoordinateTemplateMaxTemplatesPerDigit: null,
            CoordinateTemplateRequirePerDigitOcrValidation: null),
        ct);

    return Results.Ok(new
    {
        message = "Auto profile calibration stopped.",
        settings
    });
});

app.MapPost("/api/coordinate-template/profile/use-fast", async (
    ICoordinateOcrSettingsService coordinateOcrSettings,
    ICoordinateTemplateOcrService templateOcr,
    CancellationToken ct) =>
{
    var current = coordinateOcrSettings.Get();
    var profile = templateOcr.GetProfileStatus(current.CoordinateTemplateAutoProfileEnabled);

    if (!profile.ProfileReady || profile.MissingDigitTemplates.Count > 0)
    {
        return Results.BadRequest(new
        {
            message = "Fast OCR profile is not ready. Learn all digits 0-9 first.",
            profileReady = profile.ProfileReady,
            missing = profile.MissingDigitTemplates
        });
    }

    var settings = await coordinateOcrSettings.UpdateAsync(
        new UpdateCoordinateOcrSettingsRequest(
            CoordinateReadMode: CoordinateOcrModes.FastTemplate,
            CoordinateTemplateFallbackToNormalOcr: true,
            CoordinateTemplateCountFailedReadsForRecalibration: null,
            CoordinateTemplateRecalibrationFailureLimit: null,
            CoordinateTemplateRequireVisibleTextForFailure: null,
            CoordinateTemplateMinTextPixelsPercent: null,
            CoordinateTemplateMinContrast: null,
            CoordinateTemplateAutoProfileEnabled: false,
            CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode: null,
            CoordinateTemplateAutoProfileMaxSamples: null,
            CoordinateTemplateAutoProfileValidationMaxDigitScore: null,
            CoordinateTemplateMaxTemplatesPerDigit: null,
            CoordinateTemplateRequirePerDigitOcrValidation: null),
        ct);

    return Results.Ok(new
    {
        message = "Fast OCR enabled. Auto profile learning disabled.",
        settings,
        profile
    });
});

app.MapPost("/api/coordinate-template/test-current", async (
    IOcrLayoutService layoutService,
    IScreenCaptureService capture,
    ICoordinateOcrSettingsService coordinateOcrSettings,
    ICoordinateTemplateOcrService templateOcr,
    CancellationToken ct) =>
{
    var layout = await layoutService.LoadAsync(ct);

    if (!layout.Enabled)
    {
        return Results.BadRequest(new
        {
            message = "OCR layout is disabled. Enable and save the OCR layout first."
        });
    }

    var coordinateBox = layout.Zones.Coordinate;

    if (coordinateBox is not { IsValid: true })
    {
        return Results.BadRequest(new
        {
            message = "Coordinate layout box is missing. Open coordinate calibration and save a coordinate box first."
        });
    }

    var zone = layoutService.TryGetCoordinateZone(layout);

    if (zone is null)
    {
        return Results.BadRequest(new
        {
            message = "Could not resolve coordinate box to screen coordinates. Make sure the game window is selected/found first."
        });
    }

    var settings = coordinateOcrSettings.Get();

    using var bitmap = capture.Capture(zone);
    var attempt = templateOcr.TryRead(bitmap, settings);
    var profile = templateOcr.GetProfileStatus(settings.CoordinateTemplateAutoProfileEnabled);

    return Results.Ok(new
    {
        success = attempt.Success,
        rawText = attempt.RawText,
        parsed = attempt.Parsed is null
            ? null
            : new
            {
                x = attempt.Parsed.X,
                y = attempt.Parsed.Y,
                rawText = attempt.Parsed.RawText
            },
        reason = attempt.Reason,
        needsRecalibration = attempt.NeedsRecalibration,
        settings,
        profile
    });
});



app.MapPost("/api/coordinate-template/profile", async (
    CreateCoordinateTemplateProfileRequest request,
    IOcrLayoutService layoutService,
    IScreenCaptureService capture,
    ICoordinateTemplateOcrService templateOcr,
    IOptionsMonitor<OcrRuntimeSettings> settings,
    CancellationToken ct) =>
{
    try
    {
        var layout = await layoutService.LoadAsync(ct);
        var coordinateBox = layout.Zones.Coordinate;

        if (coordinateBox is not { IsValid: true })
        {
            return Results.BadRequest(new
            {
                message = "Coordinate layout box is missing. Open coordinate calibration and save a coordinate box first."
            });
        }

        var zone = layoutService.TryGetCoordinateZone(layout);
        if (zone is null)
        {
            return Results.BadRequest(new
            {
                message = "Could not resolve coordinate box to screen coordinates. Make sure the game window is selected/found first."
            });
        }

        using var bitmap = capture.Capture(zone);
        var profile = await templateOcr.CreateProfileAsync(
            bitmap,
            coordinateBox,
            request,
            settings.CurrentValue,
            ct);

        return Results.Ok(profile);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
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

app.MapDelete("/api/prices/history/{id:int}", async (AppDbContext db, int id) =>
{
    var row = await db.PriceCaptures.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null)
        return Results.NotFound(new { message = $"Price history entry '{id}' was not found." });

    db.PriceCaptures.Remove(row);
    await db.SaveChangesAsync();

    return Results.Ok(new { deleted = 1, id });
});

app.MapDelete("/api/prices/history", async (AppDbContext db, string? city, string? item, string? tradeType) =>
{
    if (string.IsNullOrWhiteSpace(city) ||
        string.IsNullOrWhiteSpace(item) ||
        string.IsNullOrWhiteSpace(tradeType))
    {
        return Results.BadRequest(new
        {
            message = "city, item, and tradeType are required to delete matching price history entries."
        });
    }

    var rows = await db.PriceCaptures
        .Where(x => x.City == city && x.ItemName == item && x.TradeType == tradeType)
        .ToListAsync();

    if (rows.Count == 0)
        return Results.NotFound(new { message = "No matching price history entries were found." });

    db.PriceCaptures.RemoveRange(rows);
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        deleted = rows.Count,
        city,
        item,
        tradeType
    });
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
    IOcrCachedTextService ocr,
    IOcrImagePreprocessingService preprocessor,
    IOcrDebugSnapshotService debug,
    [FromServices] ICoordinateParser coordinateParser,
    [FromServices] ICityParser cityParser,
    [FromServices] IStrictTradeGoodMatcher strictTradeGoodMatcher,
    Microsoft.Extensions.Options.IOptionsMonitor<OcrRuntimeSettings> settings,
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
    var fieldKind = GetLayoutTestFieldKind(request.Kind);

    if (request.Preprocess)
    {
        var preprocessed = TryPrepareLayoutTestImage(
            preprocessor,
            bitmap,
            fieldKind,
            settings.CurrentValue);

        if (preprocessed is not null)
        {
            using (preprocessed)
            {
                var source = "layout-test-preprocessed";
                var preprocessedRaw = ocr.ReadText(
                    source,
                    preprocessed,
                    fieldKind,
                    settings.CurrentValue).Text;
                var preprocessedDebugPath = await debug.SaveAsync(
                    request.Kind,
                    source,
                    preprocessed,
                    preprocessedRaw,
                    ct);
                var score = ScoreLayoutTestBox(
                    request.Kind,
                    fieldKind,
                    preprocessedRaw,
                    settings.CurrentValue,
                    coordinateParser,
                    cityParser,
                    strictTradeGoodMatcher);

                return Results.Ok(new OcrLayoutTestBoxResponse(
                    request.Kind,
                    source,
                    preprocessedRaw,
                    score.Score,
                    score.Status,
                    score.Message,
                    score.ParsedText,
                    EncodePngDataUrl(preprocessed),
                    preprocessedDebugPath,
                    BuildOcrDebugImageUrl(preprocessedDebugPath),
                    request.Box,
                    captureZone));
            }
        }
    }

    var directSource = "layout-test";
    var raw = ocr.ReadText(
        directSource,
        bitmap,
        fieldKind,
        settings.CurrentValue).Text;
    var debugPath = await debug.SaveAsync(
        request.Kind,
        directSource,
        bitmap,
        raw,
        ct);
    var directScore = ScoreLayoutTestBox(
        request.Kind,
        fieldKind,
        raw,
        settings.CurrentValue,
        coordinateParser,
        cityParser,
        strictTradeGoodMatcher);

    return Results.Ok(new OcrLayoutTestBoxResponse(
        request.Kind,
        directSource,
        raw,
        directScore.Score,
        directScore.Status,
        directScore.Message,
        directScore.ParsedText,
        EncodePngDataUrl(bitmap),
        debugPath,
        BuildOcrDebugImageUrl(debugPath),
        request.Box,
        captureZone));
});

app.MapGet("/api/ocr-debug-image", (
    string path,
    IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest("Missing debug image path.");

    var folder = configuration.GetValue<string>("OcrSettings:DebugImageFolder");
    if (string.IsNullOrWhiteSpace(folder))
        folder = Path.Combine("Data", "debug-ocr");

    if (!Path.IsPathRooted(folder))
        folder = Path.Combine(AppContext.BaseDirectory, folder);

    var root = Path.GetFullPath(folder);
    if (!root.EndsWith(Path.DirectorySeparatorChar))
        root += Path.DirectorySeparatorChar;

    var requested = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    if (!requested.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
        !File.Exists(requested) ||
        !string.Equals(Path.GetExtension(requested), ".png", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }

    return Results.File(requested, "image/png");
});

app.MapPost("/api/ocr-layout/calibration-score", async (
    IOcrCalibrationService calibration,
    CancellationToken ct) =>
{
    var result = await calibration.ScoreAsync(ct);
    return Results.Ok(result);
});


app.Run();
