using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class PriceLayoutRowWatcherGateTests
{
    [TestMethod]
    public void WatcherRunsWhenFastModeCityKnownTradeKnownAndDue()
    {
        var now = DateTime.UtcNow;

        var run = PriceLayoutRowWatcherGate.ShouldRun(
            Settings(),
            useFastTradeTypeTemplate: true,
            hasKnownCity: true,
            coordinateRecentlyVisible: false,
            tradeType: "Buy",
            scheduledPriceReadWillRun: false,
            lastWatcherUtc: now.AddMilliseconds(-500),
            nowUtc: now);

        Assert.IsTrue(run);
    }

    [TestMethod]
    public void WatcherSkipsWhenCityUnknown()
    {
        var run = PriceLayoutRowWatcherGate.ShouldRun(
            Settings(),
            useFastTradeTypeTemplate: true,
            hasKnownCity: false,
            coordinateRecentlyVisible: false,
            tradeType: "Buy",
            scheduledPriceReadWillRun: false,
            lastWatcherUtc: null,
            nowUtc: DateTime.UtcNow);

        Assert.IsFalse(run);
    }

    [TestMethod]
    public void WatcherSkipsWhenCoordinateRecentlyVisible()
    {
        var run = PriceLayoutRowWatcherGate.ShouldRun(
            Settings(),
            useFastTradeTypeTemplate: true,
            hasKnownCity: true,
            coordinateRecentlyVisible: true,
            tradeType: "Buy",
            scheduledPriceReadWillRun: false,
            lastWatcherUtc: null,
            nowUtc: DateTime.UtcNow);

        Assert.IsFalse(run);
    }

    [TestMethod]
    public void WatcherSkipsWhenTradeTypeUnknown()
    {
        var run = PriceLayoutRowWatcherGate.ShouldRun(
            Settings(),
            useFastTradeTypeTemplate: true,
            hasKnownCity: true,
            coordinateRecentlyVisible: false,
            tradeType: "Unknown",
            scheduledPriceReadWillRun: false,
            lastWatcherUtc: null,
            nowUtc: DateTime.UtcNow);

        Assert.IsFalse(run);
    }

    [TestMethod]
    public void WatcherSkipsWhenScheduledPriceReadWillRun()
    {
        var run = PriceLayoutRowWatcherGate.ShouldRun(
            Settings(),
            useFastTradeTypeTemplate: true,
            hasKnownCity: true,
            coordinateRecentlyVisible: false,
            tradeType: "Sell",
            scheduledPriceReadWillRun: true,
            lastWatcherUtc: null,
            nowUtc: DateTime.UtcNow);

        Assert.IsFalse(run);
    }

    [TestMethod]
    public void WatcherSkipsUntilIntervalExpires()
    {
        var now = DateTime.UtcNow;

        var run = PriceLayoutRowWatcherGate.ShouldRun(
            Settings(),
            useFastTradeTypeTemplate: true,
            hasKnownCity: true,
            coordinateRecentlyVisible: false,
            tradeType: "Buy",
            scheduledPriceReadWillRun: false,
            lastWatcherUtc: now.AddMilliseconds(-499),
            nowUtc: now);

        Assert.IsFalse(run);
    }

    private static OcrRuntimeSettings Settings()
        => new()
        {
            PriceLayoutRowWatcherEnabled = true,
            PriceLayoutRowWatcherIntervalMs = 500
        };
}
