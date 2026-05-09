using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class CoordinateTemplateOcrServiceTests
{
    [TestMethod]
    public void DefaultCoordinateReadModeIsNormalOcr()
    {
        var settings = new OcrRuntimeSettings();

        Assert.AreEqual(CoordinateOcrModes.NormalOcr, settings.CoordinateReadMode);
    }

    [TestMethod]
    public void VisibleUnreadableImageIncrementsFailureCountWhenEnabled()
    {
        var service = new CoordinateTemplateOcrService();
        using var bitmap = CreateVisibleCoordinateLikeBitmap();

        var attempt = service.TryRead(bitmap, BuildSettings(countFailures: true, limit: 2));
        var status = service.GetStatus();

        Assert.IsFalse(attempt.Success);
        Assert.AreEqual(1, status.FailedReadCount);
        Assert.IsFalse(status.NeedsRecalibration);
    }

    [TestMethod]
    public void VisibleUnreadableImageDoesNotIncrementFailureCountWhenDisabled()
    {
        var service = new CoordinateTemplateOcrService();
        using var bitmap = CreateVisibleCoordinateLikeBitmap();

        var attempt = service.TryRead(bitmap, BuildSettings(countFailures: false, limit: 1));
        var status = service.GetStatus();

        Assert.IsFalse(attempt.Success);
        Assert.AreEqual(0, status.FailedReadCount);
        Assert.IsFalse(status.NeedsRecalibration);
        StringAssert.Contains(status.LastFailureReason, "failure counting disabled");
    }

    [TestMethod]
    public void EmptyImageDoesNotIncrementFailureCount()
    {
        var service = new CoordinateTemplateOcrService();
        using var bitmap = new Bitmap(20, 8);

        var attempt = service.TryRead(bitmap, BuildSettings(countFailures: true, limit: 1));
        var status = service.GetStatus();

        Assert.IsFalse(attempt.Success);
        Assert.AreEqual(0, status.FailedReadCount);
        Assert.IsFalse(status.NeedsRecalibration);
        Assert.AreEqual("coordinate text not visible", attempt.Reason);
    }

    [TestMethod]
    public void FailureLimitMarksNeedsRecalibrationOnlyWhenCountingEnabled()
    {
        var enabledService = new CoordinateTemplateOcrService();
        using var bitmap = CreateVisibleCoordinateLikeBitmap();
        var enabledSettings = BuildSettings(countFailures: true, limit: 2);

        enabledService.TryRead(bitmap, enabledSettings);
        enabledService.TryRead(bitmap, enabledSettings);

        Assert.IsTrue(enabledService.GetStatus().NeedsRecalibration);

        var disabledService = new CoordinateTemplateOcrService();
        var disabledSettings = BuildSettings(countFailures: false, limit: 1);

        disabledService.TryRead(bitmap, disabledSettings);
        disabledService.TryRead(bitmap, disabledSettings);

        Assert.IsFalse(disabledService.GetStatus().NeedsRecalibration);
    }

    [TestMethod]
    public void ResetFailuresClearsRecalibrationState()
    {
        var service = new CoordinateTemplateOcrService();
        using var bitmap = CreateVisibleCoordinateLikeBitmap();

        service.TryRead(bitmap, BuildSettings(countFailures: true, limit: 1));
        Assert.IsTrue(service.GetStatus().NeedsRecalibration);

        service.ResetFailures();
        var status = service.GetStatus();

        Assert.AreEqual(0, status.FailedReadCount);
        Assert.IsFalse(status.NeedsRecalibration);
        Assert.IsNull(status.LastFailureReason);
    }

    [TestMethod]
    public async Task CreateProfileRejectsInvalidCoordinate()
    {
        var service = new CoordinateTemplateOcrService(CreateTempProfilePath());
        using var bitmap = CreateVisibleCoordinateLikeBitmap();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            service.CreateProfileAsync(
                bitmap,
                new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = 20, Height = 8 },
                new CreateCoordinateTemplateProfileRequest("99999,9999"),
                new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 },
                CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateProfileStoresTemplatesAndMissingDigits()
    {
        var path = CreateTempProfilePath();
        var service = new CoordinateTemplateOcrService(path);
        using var bitmap = CreateSegmentedCoordinateBitmap(characterCount: 10);

        var status = await service.CreateProfileAsync(
            bitmap,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = bitmap.Width, Height = bitmap.Height },
            new CreateCoordinateTemplateProfileRequest("12345,6789"),
            new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 },
            CancellationToken.None);

        Assert.IsTrue(status.ProfileReady);
        Assert.AreEqual(9, status.TemplateCount);
        CollectionAssert.AreEqual(new[] { "0" }, status.MissingDigitTemplates.ToArray());
        StringAssert.Contains(status.LastCalibrationMessage, "12345,6789");

        var reloaded = service.GetProfileStatus();
        Assert.IsTrue(reloaded.ProfileReady);
        Assert.AreEqual(9, reloaded.TemplateCount);
    }

    [TestMethod]
    public void AutoProfileSamplesMergeAndFillMissingDigits()
    {
        var service = new CoordinateTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings(countFailures: true, limit: 2) with
        {
            CoordinateTemplateAutoProfileEnabled = true
        };
        var runtimeSettings = new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 };

        using var first = CreateSegmentedCoordinateBitmap(characterCount: 10);
        var firstStatus = service.AddProfileSampleFromNormalOcr(
            first,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = first.Width, Height = first.Height },
            new ParsedCoordinate(12345, 6789, "12345,6789"),
            settings,
            runtimeSettings);

        Assert.IsTrue(firstStatus.ProfileReady);
        CollectionAssert.AreEqual(new[] { "0" }, firstStatus.MissingDigitTemplates.ToArray());
        Assert.AreEqual(1, firstStatus.SampleCount);

        using var second = CreateSegmentedCoordinateBitmap(characterCount: 9);
        var secondStatus = service.AddProfileSampleFromNormalOcr(
            second,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = second.Width, Height = second.Height },
            new ParsedCoordinate(1020, 3000, "1020,3000"),
            settings,
            runtimeSettings);

        CollectionAssert.AreEqual(Array.Empty<string>(), secondStatus.MissingDigitTemplates.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" },
            secondStatus.LearnedDigits.ToArray());
        Assert.AreEqual(2, secondStatus.SampleCount);
        StringAssert.Contains(secondStatus.LastAutoSampleMessage, "1020,3000");
    }

    [TestMethod]
    public void AutoProfileValidatesKnownDigitsBeforeLearningMissingDigits()
    {
        var service = new CoordinateTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings(countFailures: true, limit: 2) with
        {
            CoordinateTemplateAutoProfileEnabled = true
        };
        var runtimeSettings = new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 };

        using var first = CreatePatternCoordinateBitmap("1212,5444");
        service.AddProfileSampleFromNormalOcr(
            first,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = first.Width, Height = first.Height },
            new ParsedCoordinate(1212, 5444, "1212,5444"),
            settings,
            runtimeSettings);

        using var second = CreatePatternCoordinateBitmap("1289,5670");
        var status = service.AddProfileSampleFromNormalOcr(
            second,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = second.Width, Height = second.Height },
            new ParsedCoordinate(1289, 5670, "1289,5670"),
            settings,
            runtimeSettings);

        CollectionAssert.AreEquivalent(new[] { "1", "2", "5" }, status.LastValidatedDigits.ToArray());
        CollectionAssert.AreEquivalent(new[] { "0", "6", "7", "8", "9" }, status.LastLearnedDigits.ToArray());
        CollectionAssert.AreEqual(new[] { "3" }, status.MissingDigitTemplates.ToArray());
        Assert.IsTrue(status.LastSampleAccepted);

        using var third = CreatePatternCoordinateBitmap("1233,3333");
        var fullStatus = service.AddProfileSampleFromNormalOcr(
            third,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = third.Width, Height = third.Height },
            new ParsedCoordinate(1233, 3333, "1233,3333"),
            settings,
            runtimeSettings);

        CollectionAssert.AreEqual(Array.Empty<string>(), fullStatus.MissingDigitTemplates.ToArray());
        Assert.IsTrue(fullStatus.LastOcrComparisonMatched);
        Assert.AreEqual("1233,3333", fullStatus.LastOcrComparisonText);
    }

    [TestMethod]
    public void AutoProfileRejectsSampleWhenKnownDigitValidationFails()
    {
        var service = new CoordinateTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings(countFailures: true, limit: 2) with
        {
            CoordinateTemplateAutoProfileEnabled = true,
            CoordinateTemplateAutoProfileValidationMaxDigitScore = -0.01
        };
        var runtimeSettings = new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 };

        using var first = CreatePatternCoordinateBitmap("1212,5444");
        service.AddProfileSampleFromNormalOcr(
            first,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = first.Width, Height = first.Height },
            new ParsedCoordinate(1212, 5444, "1212,5444"),
            settings,
            runtimeSettings);

        using var second = CreatePatternCoordinateBitmap(
            "1289,5670",
            new Dictionary<char, int> { ['1'] = 0 });
        var status = service.AddProfileSampleFromNormalOcr(
            second,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = second.Width, Height = second.Height },
            new ParsedCoordinate(1289, 5670, "1289,5670"),
            settings,
            runtimeSettings);

        Assert.IsFalse(status.LastSampleAccepted);
        CollectionAssert.Contains(status.LastRejectedDigits.ToArray(), "1");
        CollectionAssert.AreEquivalent(new[] { "0", "3", "6", "7", "8", "9" }, status.MissingDigitTemplates.ToArray());
    }

    [TestMethod]
    public void AutoProfileRespectsTemplateVariantCap()
    {
        var service = new CoordinateTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings(countFailures: true, limit: 2) with
        {
            CoordinateTemplateAutoProfileEnabled = true,
            CoordinateTemplateMaxTemplatesPerDigit = 2
        };
        var runtimeSettings = new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 };

        for (var i = 0; i < 5; i++)
        {
            using var bitmap = CreatePatternCoordinateBitmap("1111,1111");
            service.AddProfileSampleFromNormalOcr(
                bitmap,
                new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = bitmap.Width, Height = bitmap.Height },
                new ParsedCoordinate(1111, 1111, "1111,1111"),
                settings,
                runtimeSettings);
        }

        Assert.AreEqual(2, service.GetProfileStatus().TemplateCount);
    }

    [TestMethod]
    public void AutoProfileStopsAtSampleLimit()
    {
        var service = new CoordinateTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings(countFailures: true, limit: 2) with
        {
            CoordinateTemplateAutoProfileEnabled = true,
            CoordinateTemplateAutoProfileMaxSamples = 1
        };
        var runtimeSettings = new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 };
        using var bitmap = CreateSegmentedCoordinateBitmap(characterCount: 10);

        service.AddProfileSampleFromNormalOcr(
            bitmap,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = bitmap.Width, Height = bitmap.Height },
            new ParsedCoordinate(12345, 6789, "12345,6789"),
            settings,
            runtimeSettings);

        var status = service.AddProfileSampleFromNormalOcr(
            bitmap,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = bitmap.Width, Height = bitmap.Height },
            new ParsedCoordinate(1020, 3000, "1020,3000"),
            settings,
            runtimeSettings);

        Assert.AreEqual(1, status.SampleCount);
        StringAssert.Contains(status.LastAutoSampleMessage, "sample limit");
    }

    [TestMethod]
    public void AutoProfileUsesSeparatorCenteredSegmentationWhenCommaIsFound()
    {
        var service = new CoordinateTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings(countFailures: true, limit: 2) with
        {
            CoordinateTemplateAutoProfileEnabled = true
        };
        using var bitmap = CreatePatternCoordinateBitmap("1212,5444");

        var status = service.AddProfileSampleFromNormalOcr(
            bitmap,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = bitmap.Width, Height = bitmap.Height },
            new ParsedCoordinate(1212, 5444, "1212,5444"),
            settings,
            new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 });

        Assert.AreEqual("SeparatorCentered", status.LastSegmentationMode);
    }

    [TestMethod]
    public void AutoProfileFallsBackWhenSeparatorIsNotFound()
    {
        var service = new CoordinateTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings(countFailures: true, limit: 2) with
        {
            CoordinateTemplateAutoProfileEnabled = true
        };
        using var bitmap = CreatePatternCoordinateBitmap("1212,5444", skipCommaInk: true);

        var status = service.AddProfileSampleFromNormalOcr(
            bitmap,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = bitmap.Width, Height = bitmap.Height },
            new ParsedCoordinate(1212, 5444, "1212,5444"),
            settings,
            new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 });

        Assert.AreEqual("Fallback", status.LastSegmentationMode);
        CollectionAssert.AreEquivalent(
            new[] { "1", "2", "4", "5" },
            status.LastLowQualityDigits.ToArray());
    }

    [TestMethod]
    public void AutoProfileMarksEdgeDigitsLowerQualityWhenTheyTouchCropEdge()
    {
        var service = new CoordinateTemplateOcrService(CreateTempProfilePath());
        var settings = BuildSettings(countFailures: true, limit: 2) with
        {
            CoordinateTemplateAutoProfileEnabled = true
        };
        using var bitmap = CreatePatternCoordinateBitmap("1212,5444", touchEdges: true);

        var status = service.AddProfileSampleFromNormalOcr(
            bitmap,
            new OcrLayoutBox { Name = "Coordinate", X = 1, Y = 2, Width = bitmap.Width, Height = bitmap.Height },
            new ParsedCoordinate(1212, 5444, "1212,5444"),
            settings,
            new OcrRuntimeSettings { WorldWidth = 16384, WorldHeight = 8192 });

        CollectionAssert.IsSubsetOf(new[] { "1", "4" }, status.LastLowQualityDigits.ToArray());
    }

    private static CoordinateOcrSettingsResponse BuildSettings(bool countFailures, int limit)
        => new(
            CoordinateReadMode: CoordinateOcrModes.FastTemplate,
            CoordinateTemplateFallbackToNormalOcr: false,
            CoordinateTemplateCountFailedReadsForRecalibration: countFailures,
            CoordinateTemplateRecalibrationFailureLimit: limit,
            CoordinateTemplateRequireVisibleTextForFailure: true,
            CoordinateTemplateMinTextPixelsPercent: 0.35,
            CoordinateTemplateMinContrast: 18,
            CoordinateTemplateAutoProfileEnabled: false,
            CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode: true,
            CoordinateTemplateAutoProfileMaxSamples: 200,
            CoordinateTemplateAutoProfileValidationMaxDigitScore: 0.18,
            CoordinateTemplateMaxTemplatesPerDigit: 5);

    private static Bitmap CreateVisibleCoordinateLikeBitmap()
    {
        var bitmap = new Bitmap(20, 8);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);

        using var brush = new SolidBrush(Color.White);
        graphics.FillRectangle(brush, 2, 1, 2, 6);
        graphics.FillRectangle(brush, 7, 1, 2, 6);
        graphics.FillRectangle(brush, 12, 4, 2, 3);

        return bitmap;
    }

    private static Bitmap CreateSegmentedCoordinateBitmap(int characterCount)
    {
        var bitmap = new Bitmap(characterCount * 4, 8);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);

        using var brush = new SolidBrush(Color.White);

        for (var i = 0; i < characterCount; i++)
            graphics.FillRectangle(brush, i * 4 + 1, 1, 2, 6);

        return bitmap;
    }

    private static Bitmap CreatePatternCoordinateBitmap(
        string coordinate,
        IReadOnlyDictionary<char, int>? patternOverrides = null,
        bool skipCommaInk = false,
        bool touchEdges = false)
    {
        var bitmap = new Bitmap(coordinate.Length * 5, 8);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);

        using var brush = new SolidBrush(Color.White);

        for (var i = 0; i < coordinate.Length; i++)
        {
            var c = coordinate[i];
            var pattern = patternOverrides is not null && patternOverrides.TryGetValue(c, out var overridePattern)
                ? overridePattern
                : c == ',' ? -1 : c - '0';
            var x = i * 5;

            if (pattern == -1)
            {
                if (!skipCommaInk)
                    graphics.FillRectangle(brush, x + 1, 5, 1, 2);
                continue;
            }

            DrawDigitPattern(graphics, brush, x, pattern);
        }

        if (touchEdges)
        {
            graphics.FillRectangle(brush, 0, 1, 1, 6);
            graphics.FillRectangle(brush, bitmap.Width - 1, 1, 1, 6);
        }

        return bitmap;
    }

    private static void DrawDigitPattern(Graphics graphics, Brush brush, int x, int pattern)
    {
        if ((pattern & 1) != 0) graphics.FillRectangle(brush, x + 1, 1, 1, 6);
        if ((pattern & 2) != 0) graphics.FillRectangle(brush, x + 2, 1, 1, 6);
        if ((pattern & 4) != 0) graphics.FillRectangle(brush, x + 1, 1, 2, 1);
        if ((pattern & 8) != 0) graphics.FillRectangle(brush, x + 1, 6, 2, 1);

        if (pattern == 0)
            graphics.FillRectangle(brush, x + 1, 3, 2, 2);
    }

    private static string CreateTempProfilePath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OcrTradingBackendTests");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{Guid.NewGuid():N}.json");
    }
}
