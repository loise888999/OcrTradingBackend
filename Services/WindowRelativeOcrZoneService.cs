using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record RelativeOcrZoneData(
    double X,
    double Y,
    double Width,
    double Height,
    string ProcessName,
    DateTime UpdatedAtUtc);

public interface IWindowRelativeOcrZoneService
{
    Task<OcrZone> SaveZoneAsync(AppDbContext db, OcrZone absoluteZone, CancellationToken ct = default);
    Task<OcrZone?> ResolveZoneAsync(AppDbContext db, OcrZone? storedZone, CancellationToken ct = default);
    GameWindowInfo? FindWindow();
}

public sealed class WindowRelativeOcrZoneService : IWindowRelativeOcrZoneService
{
    private readonly IGameWindowLocator _windowLocator;
    private readonly ILogger<WindowRelativeOcrZoneService> _logger;

    public WindowRelativeOcrZoneService(IGameWindowLocator windowLocator, ILogger<WindowRelativeOcrZoneService> logger)
    {
        _windowLocator = windowLocator;
        _logger = logger;
    }

    public GameWindowInfo? FindWindow() => _windowLocator.FindWindow();

    public async Task<OcrZone> SaveZoneAsync(AppDbContext db, OcrZone absoluteZone, CancellationToken ct = default)
    {
        absoluteZone.Name = absoluteZone.Name.Trim();
        absoluteZone.UpdatedAtUtc = DateTime.UtcNow;

        var existing = await db.OcrZones.FirstOrDefaultAsync(x => x.Name == absoluteZone.Name, ct);
        if (existing is null)
        {
            db.OcrZones.Add(absoluteZone);
            existing = absoluteZone;
        }
        else
        {
            existing.TopLeftX = absoluteZone.TopLeftX;
            existing.TopLeftY = absoluteZone.TopLeftY;
            existing.BottomRightX = absoluteZone.BottomRightX;
            existing.BottomRightY = absoluteZone.BottomRightY;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }

        var window = _windowLocator.FindWindow();
        if (window is not null)
        {
            var relative = ConvertAbsoluteToRelative(existing, window);
            await SaveRelativeDataAsync(db, existing.Name, relative, ct);
        }
        else
        {
            _logger.LogWarning("Game window was not found while saving OCR zone {ZoneName}. Absolute zone was saved, but relative data was not updated.", existing.Name);
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<OcrZone?> ResolveZoneAsync(AppDbContext db, OcrZone? storedZone, CancellationToken ct = default)
    {
        if (storedZone is null)
            return null;

        var window = _windowLocator.FindWindow();
        if (window is null)
            return storedZone;

        var relative = await GetRelativeDataAsync(db, storedZone.Name, ct);
        if (relative is null)
            return storedZone;

        return ConvertRelativeToAbsolute(storedZone.Name, relative, window);
    }

    private static RelativeOcrZoneData ConvertAbsoluteToRelative(OcrZone zone, GameWindowInfo window)
    {
        var left = Math.Min(zone.TopLeftX, zone.BottomRightX);
        var top = Math.Min(zone.TopLeftY, zone.BottomRightY);
        var right = Math.Max(zone.TopLeftX, zone.BottomRightX);
        var bottom = Math.Max(zone.TopLeftY, zone.BottomRightY);

        var x = Clamp01((double)(left - window.Left) / window.Width);
        var y = Clamp01((double)(top - window.Top) / window.Height);
        var width = Clamp01((double)(right - left) / window.Width);
        var height = Clamp01((double)(bottom - top) / window.Height);

        return new RelativeOcrZoneData(x, y, width, height, window.ProcessName, DateTime.UtcNow);
    }

    private static OcrZone ConvertRelativeToAbsolute(string name, RelativeOcrZoneData relative, GameWindowInfo window)
    {
        var left = window.Left + (int)Math.Round(relative.X * window.Width);
        var top = window.Top + (int)Math.Round(relative.Y * window.Height);
        var width = Math.Max(1, (int)Math.Round(relative.Width * window.Width));
        var height = Math.Max(1, (int)Math.Round(relative.Height * window.Height));

        return new OcrZone
        {
            Name = name,
            TopLeftX = left,
            TopLeftY = top,
            BottomRightX = left + width,
            BottomRightY = top + height,
            UpdatedAtUtc = relative.UpdatedAtUtc
        };
    }

    private static async Task SaveRelativeDataAsync(AppDbContext db, string zoneName, RelativeOcrZoneData data, CancellationToken ct)
    {
        var key = RelativeKey(zoneName);
        var json = JsonSerializer.Serialize(data, JsonOptions());
        var existing = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, ct);

        if (existing is null)
        {
            db.AppSettings.Add(new AppSetting
            {
                Key = key,
                Value = json,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            existing.Value = json;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static async Task<RelativeOcrZoneData?> GetRelativeDataAsync(AppDbContext db, string zoneName, CancellationToken ct)
    {
        var key = RelativeKey(zoneName);
        var setting = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value)) return null;

        try
        {
            return JsonSerializer.Deserialize<RelativeOcrZoneData>(setting.Value, JsonOptions());
        }
        catch
        {
            return null;
        }
    }

    private static string RelativeKey(string zoneName) => $"OcrZoneRelative:{zoneName}";

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        return Math.Max(0, Math.Min(1, value));
    }
}
