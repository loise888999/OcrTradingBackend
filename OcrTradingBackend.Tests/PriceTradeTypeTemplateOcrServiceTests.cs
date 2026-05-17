using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class PriceTradeTypeTemplateOcrServiceTests
{
    [TestMethod]
    public void DefaultPriceTradeTypeReadModeIsNormalOcr()
    {
        var settings = new OcrRuntimeSettings();
        Assert.AreEqual(PriceTradeTypeReadModes.NormalOcr, settings.PriceTradeTypeReadMode);
    }

    [TestMethod]
    public void FastModeMatchesBuyWholeBoxTemplate()
    {
        var service = new PriceTradeTypeTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings();
        using var bitmap = CreatePatternBitmap("Buy");

        service.AddProfileSampleFromNormalOcr(bitmap, BuildBox(), "Buy", settings, "Buy");

        var attempt = service.TryRead(bitmap, "Buy", settings);

        Assert.IsTrue(attempt.Success);
        Assert.AreEqual("Buy", attempt.TradeType);
        Assert.AreEqual(0.0, attempt.Score.GetValueOrDefault());
    }

    [TestMethod]
    public void FastModeMatchesSellWholeBoxTemplate()
    {
        var service = new PriceTradeTypeTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings();
        using var bitmap = CreatePatternBitmap("Sell");

        service.AddProfileSampleFromNormalOcr(bitmap, BuildBox(), "Sell", settings, "Sell");

        var attempt = service.TryRead(bitmap, "Sell", settings);

        Assert.IsTrue(attempt.Success);
        Assert.AreEqual("Sell", attempt.TradeType);
        Assert.AreEqual(0.0, attempt.Score.GetValueOrDefault());
    }

    [TestMethod]
    public void TemplateMismatchFailsWithHighScore()
    {
        var service = new PriceTradeTypeTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings() with
        {
            PriceTradeTypeTemplateMaxScore = 0.01
        };
        using var buy = CreatePatternBitmap("Buy");
        using var other = CreatePatternBitmap("Sell");

        service.AddProfileSampleFromNormalOcr(buy, BuildBox(), "Buy", settings, "Buy");

        var attempt = service.TryRead(other, "Buy", settings);

        Assert.IsFalse(attempt.Success);
        Assert.IsTrue(attempt.Score > settings.PriceTradeTypeTemplateMaxScore);
    }

    [TestMethod]
    public void FailureLimitMarksNeedsRecalibrationOnlyWhenCountingEnabled()
    {
        var enabledService = new PriceTradeTypeTemplateOcrService(CreateTempProfilePath());
        var enabledSettings = BuildSettings() with
        {
            PriceTradeTypeTemplateCountFailedReadsForRecalibration = true,
            PriceTradeTypeTemplateRecalibrationFailureLimit = 2
        };

        enabledService.MaybeCountFailedFastRead(enabledSettings, "visible text read failed");
        enabledService.MaybeCountFailedFastRead(enabledSettings, "visible text read failed");

        Assert.IsTrue(enabledService.GetProfileStatus().NeedsRecalibration);

        var disabledService = new PriceTradeTypeTemplateOcrService(CreateTempProfilePath());
        var disabledSettings = enabledSettings with
        {
            PriceTradeTypeTemplateCountFailedReadsForRecalibration = false
        };

        disabledService.MaybeCountFailedFastRead(disabledSettings, "visible text read failed");
        disabledService.MaybeCountFailedFastRead(disabledSettings, "visible text read failed");

        var disabledStatus = disabledService.GetProfileStatus();
        Assert.AreEqual(0, disabledStatus.FailedReadCount);
        Assert.IsFalse(disabledStatus.NeedsRecalibration);
    }

    [TestMethod]
    public void TemplateCapIsRespected()
    {
        var service = new PriceTradeTypeTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings() with
        {
            PriceTradeTypeTemplateMaxTemplatesPerType = 1
        };
        using var first = CreatePatternBitmap("Buy");
        using var second = CreateShiftedBuyBitmap();

        service.AddProfileSampleFromNormalOcr(first, BuildBox(), "Buy", settings, "Buy");
        service.AddProfileSampleFromNormalOcr(second, BuildBox(), "Buy", settings, "Buy");

        var status = service.GetProfileStatus();
        Assert.AreEqual(1, status.BuyTemplateCount);
    }

    private static PriceTradeTypeTemplateSettingsResponse BuildSettings()
        => new(
            PriceTradeTypeReadMode: PriceTradeTypeReadModes.FastTemplate,
            PriceTradeTypeTemplateFallbackToNormalOcr: true,
            PriceTradeTypeTemplateAutoProfileEnabled: true,
            PriceTradeTypeTemplateMaxTemplatesPerType: 5,
            PriceTradeTypeTemplateMaxScore: 0.18,
            PriceTradeTypeTemplateCountFailedReadsForRecalibration: true,
            PriceTradeTypeTemplateRecalibrationFailureLimit: 5,
            PriceTradeTypeTemplateProbeIntervalMs: 250);

    private static OcrLayoutBox BuildBox()
        => new()
        {
            Name = "TradeType",
            X = 10,
            Y = 20,
            Width = 40,
            Height = 18
        };

    private static Bitmap CreatePatternBitmap(string tradeType)
    {
        var bitmap = new Bitmap(40, 18);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);

        using var brush = new SolidBrush(Color.Black);

        if (tradeType == "Buy")
        {
            graphics.FillRectangle(brush, 4, 3, 5, 12);
            graphics.FillRectangle(brush, 14, 8, 14, 4);
        }
        else
        {
            graphics.FillRectangle(brush, 28, 3, 5, 12);
            graphics.FillRectangle(brush, 9, 4, 16, 4);
            graphics.FillRectangle(brush, 9, 11, 16, 4);
        }

        return bitmap;
    }

    private static Bitmap CreateShiftedBuyBitmap()
    {
        var bitmap = new Bitmap(40, 18);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using var brush = new SolidBrush(Color.Black);

        graphics.FillRectangle(brush, 6, 3, 5, 12);
        graphics.FillRectangle(brush, 16, 8, 14, 4);

        return bitmap;
    }

    private static string CreateTempProfilePath()
        => Path.Combine(
            Path.GetTempPath(),
            $"price-trade-type-template-{Guid.NewGuid():N}.json");
}
