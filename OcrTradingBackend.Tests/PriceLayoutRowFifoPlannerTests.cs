using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class PriceLayoutRowFifoPlannerTests
{
    [TestMethod]
    public void OrderRowsStartsAtCursorAndWraps()
    {
        var ordered = PriceLayoutRowFifoPlanner.OrderRows(
            new[] { 0, 1, 2, 3 },
            2);

        CollectionAssert.AreEqual(
            new[] { 2, 3, 0, 1 },
            ordered.ToArray());
    }

    [TestMethod]
    public void AdvanceNextIndexWrapsAfterInspectedRows()
    {
        var next = PriceLayoutRowFifoPlanner.AdvanceNextIndex(
            nextIndex: 3,
            inspectedCount: 2,
            rowCount: 4);

        Assert.AreEqual(1, next);
    }

    [TestMethod]
    public void NormalizeNextIndexHandlesNegativeCursor()
    {
        Assert.AreEqual(
            3,
            PriceLayoutRowFifoPlanner.NormalizeNextIndex(-1, 4));
    }

    [TestMethod]
    public void RowCacheReturnsLatestParsedRowForPreservedFifoOutput()
    {
        var cache = new PriceLayoutRowCacheService();
        var parsed = new ParsedPriceLine(
            ItemName: "Sand",
            TradeGoodType: "Wares",
            Price: 100,
            Multiplier: 95,
            TradeType: "Buy",
            RawText: "Row 1: Sand | 100 | 95 | Buy");

        cache.Remember(
            "price-layout-row:1:Buy",
            "Buy",
            new PriceLayoutRowFingerprint(1, 2),
            parsed);

        var found = cache.TryGetLatest(
            "price-layout-row:1:Buy",
            "Buy",
            out var cached);

        Assert.IsTrue(found);
        Assert.AreEqual(parsed, cached);
    }

    [TestMethod]
    public void RowCacheDoesNotReturnNullParsedRowsForPreservedFifoOutput()
    {
        var cache = new PriceLayoutRowCacheService();

        cache.Remember(
            "price-layout-row:1:Buy",
            "Buy",
            new PriceLayoutRowFingerprint(1, 2),
            null);

        var found = cache.TryGetLatest(
            "price-layout-row:1:Buy",
            "Buy",
            out var cached);

        Assert.IsFalse(found);
        Assert.IsNull(cached);
    }
}
