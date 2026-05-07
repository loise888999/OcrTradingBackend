using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class CoordinateFarJumpConfirmationGateTests
{
    [TestMethod]
    public void AcceptsNearCoordinateImmediately()
    {
        var gate = new CoordinateFarJumpConfirmationGate();

        var decision = gate.Evaluate(
            Parsed(5200, 3400),
            Previous(5000, 3000),
            Settings());

        Assert.IsTrue(decision.Accepted);
        Assert.IsFalse(decision.AcceptedAfterConfirmation);
    }

    [TestMethod]
    public void RejectsFirstFarCoordinateAsPending()
    {
        var gate = new CoordinateFarJumpConfirmationGate();

        var decision = gate.Evaluate(
            Parsed(12000, 7000),
            Previous(5000, 3000),
            Settings());

        Assert.IsFalse(decision.Accepted);
        Assert.AreEqual(1, decision.PendingCount);
        Assert.AreEqual(4, decision.RequiredCount);
    }

    [TestMethod]
    public void AcceptsFourthConsecutiveSimilarFarCoordinate()
    {
        var gate = new CoordinateFarJumpConfirmationGate();
        var previous = Previous(5000, 3000);
        var settings = Settings();

        Assert.IsFalse(gate.Evaluate(Parsed(12000, 7000), previous, settings).Accepted);
        Assert.IsFalse(gate.Evaluate(Parsed(11980, 7010), previous, settings).Accepted);
        Assert.IsFalse(gate.Evaluate(Parsed(12010, 6995), previous, settings).Accepted);

        var fourth = gate.Evaluate(Parsed(12020, 7005), previous, settings);

        Assert.IsTrue(fourth.Accepted);
        Assert.IsTrue(fourth.AcceptedAfterConfirmation);
    }

    [TestMethod]
    public void ThreeSimilarFarCoordinatesAreNotEnough()
    {
        var gate = new CoordinateFarJumpConfirmationGate();
        var previous = Previous(5000, 3000);
        var settings = Settings();

        Assert.IsFalse(gate.Evaluate(Parsed(12000, 7000), previous, settings).Accepted);
        Assert.IsFalse(gate.Evaluate(Parsed(11980, 7010), previous, settings).Accepted);
        Assert.IsFalse(gate.Evaluate(Parsed(12010, 6995), previous, settings).Accepted);
    }

    [TestMethod]
    public void DifferentFarCoordinateResetsPendingCount()
    {
        var gate = new CoordinateFarJumpConfirmationGate();
        var previous = Previous(5000, 3000);
        var settings = Settings();

        Assert.IsFalse(gate.Evaluate(Parsed(12000, 7000), previous, settings).Accepted);
        Assert.AreEqual(2, gate.Evaluate(Parsed(11980, 7010), previous, settings).PendingCount);

        var reset = gate.Evaluate(Parsed(250, 8100), previous, settings);

        Assert.IsFalse(reset.Accepted);
        Assert.AreEqual(1, reset.PendingCount);
    }

    [TestMethod]
    public void ParserStillRejectsImpossibleWorldBoundsBeforeGate()
    {
        var parser = new CoordinateParser();

        var parsed = parser.TryParse("20000,9000", 16384, 8192);

        Assert.IsNull(parsed);
    }

    private static ParsedCoordinate Parsed(int x, int y) => new(x, y, $"{x},{y}");

    private static CoordinateCapture Previous(int x, int y) => new()
    {
        X = x,
        Y = y,
        RawText = $"{x},{y}",
        CapturedAtUtc = DateTime.UtcNow
    };

    private static OcrRuntimeSettings Settings() => new()
    {
        WorldWidth = 16384,
        WorldHeight = 8192,
        MaxCoordinateJumpX = 1200,
        MaxCoordinateJumpY = 900,
        CoordinateFarJumpConfirmationEnabled = true,
        CoordinateFarJumpRequiredReads = 4,
        CoordinateFarJumpClusterToleranceX = 100,
        CoordinateFarJumpClusterToleranceY = 100
    };
}
