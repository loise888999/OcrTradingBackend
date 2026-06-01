using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class CoordinateSpeedServiceTests
{
    [TestMethod]
    public void FirstCoordinateReturnsZeroSpeed()
    {
        var service = new CoordinateSpeedService();

        var speed = service.AddCoordinate(
            Coordinate(1, 100, 200, DateTime.UtcNow),
            Settings());

        Assert.AreEqual(0, speed.SpeedWorldUnitsPerSecond);
        Assert.AreEqual(0, speed.SpeedKnots);
        Assert.IsTrue(speed.SpeedReset);
        Assert.AreEqual("first-coordinate", speed.SpeedResetReason);
    }

    [TestMethod]
    public void MovementUsesEuclideanDistanceOverElapsedTime()
    {
        var service = new CoordinateSpeedService();
        var now = DateTime.UtcNow;
        var settings = Settings();

        service.AddCoordinate(Coordinate(1, 0, 0, now), settings);
        var speed = service.AddCoordinate(Coordinate(2, 3, 4, now.AddSeconds(1)), settings);

        Assert.AreEqual(5, speed.SpeedDistance, 0.000001);
        Assert.AreEqual(5, speed.SpeedWorldUnitsPerSecond, 0.000001);
        Assert.AreEqual(5 * CoordinateSpeedService.KnotFactor(settings.WorldWidth), speed.SpeedKnots, 0.000001);
        Assert.IsFalse(speed.SpeedReset);
    }

    [TestMethod]
    public void MovementUsesShortestWrappedXDistance()
    {
        var service = new CoordinateSpeedService();
        var now = DateTime.UtcNow;
        var settings = Settings();

        service.AddCoordinate(Coordinate(1, 16380, 0, now), settings);
        var speed = service.AddCoordinate(Coordinate(2, 4, 0, now.AddSeconds(1)), settings);

        Assert.AreEqual(8, speed.SpeedDistance, 0.000001);
        Assert.AreEqual(8, speed.SpeedWorldUnitsPerSecond, 0.000001);
    }

    [TestMethod]
    public void LongCoordinateGapResetsSpeed()
    {
        var service = new CoordinateSpeedService();
        var now = DateTime.UtcNow;
        var settings = Settings();

        service.AddCoordinate(Coordinate(1, 0, 0, now), settings);
        var speed = service.AddCoordinate(Coordinate(2, 100, 0, now.AddMilliseconds(5001)), settings);

        Assert.AreEqual(0, speed.SpeedWorldUnitsPerSecond);
        Assert.IsTrue(speed.SpeedReset);
        Assert.AreEqual("coordinate-gap", speed.SpeedResetReason);
    }

    [TestMethod]
    public void RollingWindowRemovesOldVelocitySamples()
    {
        var service = new CoordinateSpeedService();
        var now = DateTime.UtcNow;
        var settings = Settings();
        settings.CoordinateSpeedResetAfterMilliseconds = 10000;
        settings.CoordinateSpeedWindowMilliseconds = 5000;

        service.AddCoordinate(Coordinate(1, 0, 0, now), settings);
        service.AddCoordinate(Coordinate(2, 10, 0, now.AddSeconds(1)), settings);
        service.AddCoordinate(Coordinate(3, 20, 0, now.AddSeconds(2)), settings);
        var speed = service.AddCoordinate(Coordinate(4, 70, 0, now.AddMilliseconds(7001)), settings);

        Assert.AreEqual(1, speed.SpeedSampleCount);
        Assert.AreEqual(10, speed.SpeedWorldUnitsPerSecond, 0.000001);
    }

    [TestMethod]
    public void FastestRecentAverageIsSelected()
    {
        var service = new CoordinateSpeedService();
        var now = DateTime.UtcNow;
        var settings = Settings();

        service.AddCoordinate(Coordinate(1, 0, 0, now), settings);
        service.AddCoordinate(Coordinate(2, 10, 0, now.AddSeconds(1)), settings);
        var speed = service.AddCoordinate(Coordinate(3, 12, 0, now.AddSeconds(2)), settings);

        Assert.AreEqual(10, speed.SpeedWorldUnitsPerSecond, 0.000001);
        Assert.AreEqual(2, speed.SpeedSampleCount);
    }

    [TestMethod]
    public void TimelineReturnsCoordinateRowsWithSpeedFields()
    {
        var service = new CoordinateSpeedService();
        var now = DateTime.UtcNow;

        var timeline = service.BuildTimeline(
            new[]
            {
                Coordinate(1, 0, 0, now),
                Coordinate(2, 0, 6, now.AddSeconds(2))
            },
            Settings());

        Assert.AreEqual(2, timeline.Count);
        Assert.AreEqual(0, timeline[0].SpeedKnots);
        Assert.AreEqual(3, timeline[1].SpeedWorldUnitsPerSecond, 0.000001);
        Assert.IsFalse(timeline[1].SpeedReset);
    }

    private static OcrRuntimeSettings Settings()
        => new()
        {
            WorldWidth = 16384,
            CoordinateSpeedEnabled = true,
            CoordinateSpeedResetAfterMilliseconds = 5000,
            CoordinateSpeedWindowMilliseconds = 5000,
            CoordinateSpeedRecentAverageCount = 3
        };

    private static CoordinateCapture Coordinate(
        int id,
        int x,
        int y,
        DateTime capturedAtUtc)
        => new()
        {
            Id = id,
            X = x,
            Y = y,
            RawText = $"{x},{y}",
            CapturedAtUtc = capturedAtUtc
        };
}
