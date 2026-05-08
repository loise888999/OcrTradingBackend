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
        AssertParsed("Euca\nlyptus\n190 £(116%)", "Eucalyptus", 190, 116);
        AssertParsed("Euca\r\nlyptus\r\n190 £(116%)", "Eucalyptus", 190, 116);
        AssertParsed("E\nuca\r\nly\nptus 190 £(116%)", "Eucalyptus", 190, 116);
        AssertParsed("Kangaroo\r\nMeat\r\n134\r\nE\r\n86%)", "Kangaroo Meat", 134, 86);
        AssertParsed("Kangaroo Meat\r\n134\r\nE\r\n86%)", "Kangaroo Meat", 134, 86);
        AssertParsed("Kangaroo Meat 134\r\nE\r\n86%)", "Kangaroo Meat", 134, 86);
        AssertParsed("Kangaroo Meat 134 E\r\n86%)", "Kangaroo Meat", 134, 86);
        AssertParsed("Kangaroo Meat 134 E 86%)", "Kangaroo Meat", 134, 86);
    }

    [TestMethod]
    public void ParsesWholeRowsWithLineBreakNoise()
    {
        AssertParsed("Sand\n1\nE(100%)", "Sand", 1, 100);
        AssertParsed("Fine Sand\n7 E(98%)", "Fine Sand", 7, 98);
        AssertParsed("Satin\n4.760\nE(164%)", "Satin", 4760, 164);
        AssertParsed("Nutmeg\n4280\n112%", "Nutmeg", 4280, 112);
    }
    [TestMethod]
    public void ParsesItemNamesSplitAcrossLines()
    {
        AssertParsed("Leather 1\nE(100%)", "Leather", 1, 100);
        AssertParsed("Leatherwork\n722 E(98%)", "Leatherwork", 722, 98);
        AssertParsed("Leather\nwork\n722 E(98%)", "Leatherwork", 722, 98);
        AssertParsed("Leather\n Cord\n4.760\nE(164%)", "Leather Cord", 4760, 164);
    }

    [TestMethod]
    public void ParsesWholeRowsWithPriceSeparatorNoise()
    {
        AssertParsed("Nutmeg 4,280 112%", "Nutmeg", 4280, 112);
        AssertParsed("Satin 4.760 E(164%)", "Satin", 4760, 164);
        AssertParsed("Fine Sand 7 E(98 %)", "Fine Sand", 7, 98);
        AssertParsed("Sand 1 E100%", "Sand", 1, 100);
    }

    [TestMethod]
    public void ParsedWholeRowKeepsTradeTypeAndRawSummary()
    {
        var parsed = PriceLayoutRowParser.TryParseCombinedLayoutPriceRow(
            3,
            "Nutmeg 4,280 112%",
            "Sell",
            StrictMatcher);

        Assert.IsNotNull(parsed);
        Assert.AreEqual("Nutmeg", parsed.ItemName);
        Assert.AreEqual("Spices", parsed.TradeGoodType);
        Assert.AreEqual(4280, parsed.Price);
        Assert.AreEqual(112, parsed.Multiplier);
        Assert.AreEqual("Sell", parsed.TradeType);
        Assert.AreEqual("Row 3: Nutmeg | 4280 | 112 | Sell", parsed.RawText);
    }

    [TestMethod]
    public void RejectsRowsWithoutPriceBeforePercentMultiplier()
    {
        AssertRejected("Sand 100% 1");
        AssertRejected("Sand 100%");
        AssertRejected("Sand E(100%) 1");
        AssertRejected("Sand 9 8 E(100%)");
        AssertRejected("Sand");
        AssertRejected("");
    }

    [TestMethod]
    public void RejectsMalformedWholeRows()
    {
        AssertRejected("1 E(100%)");
        AssertRejected("Sand E(100%)");
        AssertRejected("Sand2 1 E(100%)");
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
        AssertNoPrice("Sand 100% 1");
        AssertNoPrice("Sand 100%");
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
            "eucalyptus" => new StrictTradeGoodMatch("Eucalyptus", "Medicine", itemText),
            "kangaroo meat" => new StrictTradeGoodMatch("Kangaroo Meat", "Foodstuffs", itemText),
            "leather" => new StrictTradeGoodMatch("Leather", "Textiles", itemText),
            "leatherwork" => new StrictTradeGoodMatch("Leatherwork", "Textiles", itemText),
            "leather cord" => new StrictTradeGoodMatch("Leather Cord", "Textiles", itemText),
            _ => null
        };
    }
}



