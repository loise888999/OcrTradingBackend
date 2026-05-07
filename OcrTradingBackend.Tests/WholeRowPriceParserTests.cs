using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class WholeRowPriceParserTests
{
    [TestMethod]
    public void ParsesExpectedWholeRows()
    {
        AssertParsed("Sand\n1 E(100%)", "Sand", 1, 100);
        AssertParsed("Sand 12 E(100%)", "Sand", 12, 100);
        AssertParsed("Sand 1 100%", "Sand", 1, 100);
        AssertParsed("Sand 1 E(100 %)", "Sand", 1, 100);
        AssertParsed("Sand 1 E100%", "Sand", 1, 100);
        AssertParsed("Sand 1 E(100%) noise 999", "Sand", 1, 100);
        AssertParsed("Nutmeg 4280 112%", "Nutmeg", 4280, 112);
        AssertParsed("Fine Sand 7 E(98%)", "Fine Sand", 7, 98);
        AssertParsed("Satin\n4.760 E(164%)", "Satin", 4760, 164);
    }

    [TestMethod]
    public void RejectsRowsWithoutPriceBeforePercentMultiplier()
    {
        AssertRejected("Sand 100% 1");
        AssertRejected("Sand 100%");
        AssertRejected("Sand E(100%) 1");
        AssertRejected("Sand 9 8 E(100%)");
        AssertRejected("Sand 1 100");
        AssertRejected("Sand 1 E(100)");
        AssertRejected("Sand");
        AssertRejected("");
    }

    [TestMethod]
    public void ExtractsItemTextBeforePriceAndMultiplier()
    {
        Assert.AreEqual("sand", PriceLayoutRowParser.ExtractLayoutRowItemText("Sand\n1 E(100%)"));
        Assert.AreEqual("sand", PriceLayoutRowParser.ExtractLayoutRowItemText("Sand 12 E(100%)"));
        Assert.AreEqual("nutmeg", PriceLayoutRowParser.ExtractLayoutRowItemText("Nutmeg 4280 112%"));
        Assert.AreEqual("fine sand", PriceLayoutRowParser.ExtractLayoutRowItemText("Fine Sand 7 E(98%)"));
        Assert.AreEqual("satin", PriceLayoutRowParser.ExtractLayoutRowItemText("Satin\n4.760 E(164%)"));
        Assert.AreEqual("sand", PriceLayoutRowParser.ExtractLayoutRowItemText("Sand 1 E(100%) 999"));
        Assert.AreEqual("satin", PriceLayoutRowParser.ExtractLayoutRowItemText("Satin\n4.760 E(164%)"));
    }

    [TestMethod]
    public void ParsesPriceAndMultiplierOnlyFromExpectedPositions()
    {
        AssertPrice("Sand\n1 E(100%)", 1, 100);
        AssertPrice("Sand 12 E(100%)", 12, 100);
        AssertPrice("Sand 1 100%", 1, 100);
        AssertPrice("Nutmeg 4280 112%", 4280, 112);
        AssertPrice("Satin\n4.760 E(164%)", 4760, 164);
        AssertPrice("Satin\n4.760 E(164%)", 4760, 164);
        AssertNoPrice("Sand 100% 1");
        AssertNoPrice("Sand 100%");
        AssertNoPrice("Sand 1 100");
    }

    private static void AssertParsed(
        string rawText,
        string expectedItem,
        decimal expectedPrice,
        decimal expectedMultiplier)
    {
        var parsed = PriceLayoutRowParser.TryParseCombinedLayoutPriceRow(
            1,
            rawText,
            "Buy",
            StrictMatcher);

        Assert.IsNotNull(parsed, $"Expected parse, got null. Raw={rawText}");
        Assert.AreEqual(expectedItem, parsed.ItemName, $"item for {rawText}");
        Assert.AreEqual(expectedPrice, parsed.Price, $"price for {rawText}");
        Assert.AreEqual(expectedMultiplier, parsed.Multiplier, $"multiplier for {rawText}");
    }

    private static void AssertRejected(string rawText)
    {
        var parsed = PriceLayoutRowParser.TryParseCombinedLayoutPriceRow(
            1,
            rawText,
            "Buy",
            StrictMatcher);

        Assert.IsNull(parsed, $"Expected reject, got parse. Raw={rawText}; Parsed={parsed}");
    }

    private static void AssertPrice(
        string rawText,
        decimal expectedPrice,
        decimal expectedMultiplier)
    {
        var parsed = PriceLayoutRowParser.TryParseLayoutRowPrice(rawText, out var price, out var multiplier);

        Assert.IsTrue(parsed, $"Expected price parse, got false. Raw={rawText}");
        Assert.AreEqual(expectedPrice, price, $"price for {rawText}");
        Assert.AreEqual(expectedMultiplier, multiplier, $"multiplier for {rawText}");
    }

    private static void AssertNoPrice(string rawText)
    {
        var parsed = PriceLayoutRowParser.TryParseLayoutRowPrice(rawText, out var price, out var multiplier);

        Assert.IsFalse(parsed, $"Expected no price parse. Raw={rawText}; Price={price}; Multiplier={multiplier}");
    }

    private static StrictTradeGoodMatch? StrictMatcher(string itemText)
    {
        return itemText.ToLowerInvariant() switch
        {
            "sand" => new StrictTradeGoodMatch("Sand", "Wares", itemText),
            "nutmeg" => new StrictTradeGoodMatch("Nutmeg", "Spices", itemText),
            "fine sand" => new StrictTradeGoodMatch("Fine Sand", "Wares", itemText),
            "satin" => new StrictTradeGoodMatch("Satin", "Fabrics", itemText),
            _ => null
        };
    }
}
