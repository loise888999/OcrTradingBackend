using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface IGameWindowChangeTracker
{
    bool MarkWindow(GameWindowInfo? window);
}

public sealed class GameWindowChangeTracker : IGameWindowChangeTracker
{
    private readonly object _sync = new();
    private long? _lastWindowHandle;

    public bool MarkWindow(GameWindowInfo? window)
    {
        if (window is null)
            return false;

        var handle = window.Handle.ToInt64();

        lock (_sync)
        {
            if (_lastWindowHandle is null)
            {
                _lastWindowHandle = handle;
                return false;
            }

            if (_lastWindowHandle.Value == handle)
                return false;

            _lastWindowHandle = handle;
            return true;
        }
    }
}

public interface IGameWindowCityResetService
{
    Task<bool> ResetLatestCityIfWindowChangedAsync(
        AppDbContext db,
        GameWindowInfo? window,
        IPriceRecentHashCacheService priceRecentHashCache,
        CancellationToken ct = default);
}

public sealed class GameWindowCityResetService : IGameWindowCityResetService
{
    public const string ResetRawText = "Game window changed; city reset to Unknown.";

    private readonly IGameWindowChangeTracker _changeTracker;

    public GameWindowCityResetService(IGameWindowChangeTracker changeTracker)
    {
        _changeTracker = changeTracker;
    }

    public async Task<bool> ResetLatestCityIfWindowChangedAsync(
        AppDbContext db,
        GameWindowInfo? window,
        IPriceRecentHashCacheService priceRecentHashCache,
        CancellationToken ct = default)
    {
        if (!_changeTracker.MarkWindow(window))
            return false;

        var latestCity = await db.CityCaptures
            .OrderByDescending(x => x.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (latestCity is null ||
            !PriceCaptureMergeService.IsKnownCity(latestCity.City))
        {
            return false;
        }

        db.CityCaptures.Add(new CityCapture
        {
            City = "Unknown",
            RawText = ResetRawText,
            CapturedAtUtc = DateTime.UtcNow
        });

        priceRecentHashCache.NotifyCityStatus("Unknown");
        await db.SaveChangesAsync(ct);
        return true;
    }
}
