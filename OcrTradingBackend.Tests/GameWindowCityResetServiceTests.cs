using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class GameWindowCityResetServiceTests
{
    [TestMethod]
    public async Task AddsUnknownCityWhenWinningWindowChangesAndLatestCityIsKnown()
    {
        await using var scope = await DbScope.CreateAsync();
        scope.Db.CityCaptures.Add(new CityCapture
        {
            City = "Seville",
            RawText = "Seville",
            CapturedAtUtc = DateTime.UtcNow.AddSeconds(-10)
        });
        scope.Db.CoordinateCaptures.Add(new CoordinateCapture
        {
            X = 10,
            Y = 20,
            RawText = "10,20",
            CapturedAtUtc = DateTime.UtcNow.AddSeconds(-5)
        });
        scope.Db.PriceCaptures.Add(new PriceCapture
        {
            City = "Seville",
            ItemName = "Wine",
            TradeGoodType = "Alcohol",
            Price = 100,
            TradeType = "Buy",
            RawText = "Wine 100",
            CapturedAtUtc = DateTime.UtcNow.AddSeconds(-4)
        });
        await scope.Db.SaveChangesAsync();

        var service = Service();
        var cache = new PriceRecentHashCacheService();

        Assert.IsFalse(await service.ResetLatestCityIfWindowChangedAsync(
            scope.Db,
            Window(1),
            cache));

        Assert.IsTrue(await service.ResetLatestCityIfWindowChangedAsync(
            scope.Db,
            Window(2),
            cache));

        var latestCity = await scope.Db.CityCaptures
            .OrderByDescending(x => x.CapturedAtUtc)
            .FirstAsync();

        Assert.AreEqual("Unknown", latestCity.City);
        Assert.AreEqual(GameWindowCityResetService.ResetRawText, latestCity.RawText);
        Assert.AreEqual(2, await scope.Db.CityCaptures.CountAsync());
        Assert.AreEqual(1, await scope.Db.CoordinateCaptures.CountAsync());
        Assert.AreEqual(1, await scope.Db.PriceCaptures.CountAsync());
    }

    [TestMethod]
    public async Task DoesNotAddDuplicateUnknownWhenLatestCityAlreadyUnknown()
    {
        await using var scope = await DbScope.CreateAsync();
        scope.Db.CityCaptures.Add(new CityCapture
        {
            City = "Unknown",
            RawText = "Already unknown",
            CapturedAtUtc = DateTime.UtcNow.AddSeconds(-10)
        });
        await scope.Db.SaveChangesAsync();

        var service = Service();
        var cache = new PriceRecentHashCacheService();

        await service.ResetLatestCityIfWindowChangedAsync(scope.Db, Window(1), cache);
        var changed = await service.ResetLatestCityIfWindowChangedAsync(scope.Db, Window(2), cache);

        Assert.IsFalse(changed);
        Assert.AreEqual(1, await scope.Db.CityCaptures.CountAsync());
    }

    [TestMethod]
    public void TrackerReportsOnlyActualWindowChanges()
    {
        var tracker = new GameWindowChangeTracker();

        Assert.IsFalse(tracker.MarkWindow(Window(1)));
        Assert.IsFalse(tracker.MarkWindow(Window(1)));
        Assert.IsTrue(tracker.MarkWindow(Window(2)));
        Assert.IsFalse(tracker.MarkWindow(null));
    }

    private static GameWindowCityResetService Service()
        => new(new GameWindowChangeTracker());

    private static GameWindowInfo Window(long handle)
        => new(new IntPtr(handle), "Idle", "Uncharted Waters Online", 0, 0, 1600, 900);

    private sealed class DbScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private DbScope(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<DbScope> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            return new DbScope(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
