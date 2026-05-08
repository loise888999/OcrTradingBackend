using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Models;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class CoordinateParserTests
{
    [TestMethod]
    public void ParsesLabeledCoordinate()
    {
        var parsed = Parser.TryParse("X: 1234 Y: 5678", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(1234, parsed.X);
        Assert.AreEqual(5678, parsed.Y);
    }

    [TestMethod]
    public void ParsesCommaCoordinate()
    {
        var parsed = Parser.TryParse("1234,5678", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(1234, parsed.X);
        Assert.AreEqual(5678, parsed.Y);

    }
    [TestMethod]
    public void ParsesExtraLineCoordinate()
    {
        var parsed = Parser.TryParse("1234,3\n5678", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(1234, parsed.X);
        Assert.AreEqual(5678, parsed.Y);

        parsed = Parser.TryParse("1\n1234,5678", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(1234, parsed.X);
        Assert.AreEqual(5678, parsed.Y);


        parsed = Parser.TryParse("5628,1\n4962", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(5628, parsed.X);
        Assert.AreEqual(4962, parsed.Y);

        parsed = Parser.TryParse("5727.\n4955", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(5727, parsed.X);
        Assert.AreEqual(4955, parsed.Y);

    }
    [TestMethod]
    public void ParsesDoubleReadCoordinate()
    {
        var parsed = Parser.TryParse("2087,\n,5919", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(2087, parsed.X);
        Assert.AreEqual(5919, parsed.Y);

        parsed = Parser.TryParse("5618,4\n,4963", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(5618, parsed.X);
        Assert.AreEqual(4963, parsed.Y);


    }
    [TestMethod]
    public void ParsesDotReadCoordinate()
    {
        var parsed = Parser.TryParse("5655.\n4960", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(5655, parsed.X);
        Assert.AreEqual(4960, parsed.Y);


    }
    [TestMethod]
    public void ParsesNoSeparatorCoordinate()
    {
        var parsed = Parser.TryParse("616\n5867", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(616, parsed.X);
        Assert.AreEqual(5867, parsed.Y);

        parsed = Parser.TryParse("6165867", 16384, 8192);

        Assert.IsNull(parsed);


    }

    [TestMethod]
    public void AllowsWorldBoundaryValues()
    {
        var parsed = Parser.TryParse("16384,8192", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(16384, parsed.X);
        Assert.AreEqual(8192, parsed.Y);
    }

    [TestMethod]
    public void RejectsCoordinateOutsideWorldBounds()
    {
        Assert.IsNull(Parser.TryParse("16385,8192", 16384, 8192));
        Assert.IsNull(Parser.TryParse("16384,8193", 16384, 8192));
    }

    [TestMethod]
    public void RejectsFarJumpWhenPreviousAndCorrectionEnabled()
    {
        var parsed = Parser.TryParse(
            "12000,7000",
            16384,
            8192,
            Previous(5000, 3000),
            new CoordinateCorrectionOptions(true, 1200, 900));

        Assert.IsNull(parsed);
    }

    [TestMethod]
    public void DirectParseCanRecoverValidFarCoordinateForConfirmationGate()
    {
        var parsed = Parser.TryParse("12000,7000", 16384, 8192);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(12000, parsed.X);
        Assert.AreEqual(7000, parsed.Y);
    }

    private static readonly CoordinateParser Parser = new();

    private static CoordinateCapture Previous(int x, int y) => new()
    {
        X = x,
        Y = y,
        RawText = $"{x},{y}",
        CapturedAtUtc = DateTime.UtcNow
    };
}
