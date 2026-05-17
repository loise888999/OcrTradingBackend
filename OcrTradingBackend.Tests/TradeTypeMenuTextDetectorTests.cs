using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class TradeTypeMenuTextDetectorTests
{
    [DataTestMethod]
    [DataRow("Items for Sale")]
    [DataRow("Items\nfor\nSale")]
    [DataRow("Items\r\nfor\tSale")]
    [DataRow("Items-for-Sale")]
    public void DetectsBuyFromItemsForSaleDespiteOcrWhitespace(string raw)
    {
        Assert.AreEqual("Buy", TradeTypeMenuTextDetector.Detect(raw));
        Assert.IsTrue(TradeTypeMenuTextDetector.LooksLikeBuy(raw));
    }

    [DataTestMethod]
    [DataRow("Sell All")]
    [DataRow("Inventory")]
    [DataRow("nventory")]
    public void DetectsSellMenuText(string raw)
    {
        Assert.AreEqual("Sell", TradeTypeMenuTextDetector.Detect(raw));
        Assert.IsTrue(TradeTypeMenuTextDetector.LooksLikeSell(raw));
    }
}
