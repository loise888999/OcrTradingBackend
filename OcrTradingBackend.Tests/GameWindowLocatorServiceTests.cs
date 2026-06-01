using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class GameWindowLocatorServiceTests
{
    [TestMethod]
    public void RememberedWindowUsesTopmostExactTitleMatch()
    {
        var remembered = Remembered("Idle", "Uncharted Waters Online", width: 1930, height: 1113);
        var topSmall = Window(2, "Idle", "Uncharted Waters Online", width: 1200, height: 800);
        var lowerExactSize = Window(1, "Idle", "Uncharted Waters Online", width: 1930, height: 1113);

        var result = GameWindowLocatorService.SelectWindowFromOrderedCandidates(
            new GameWindowSettings(),
            remembered,
            new[] { topSmall, lowerExactSize });

        Assert.IsNotNull(result);
        Assert.AreEqual(topSmall.Handle, result.Window.Handle);
        Assert.AreEqual("remembered-app", result.SelectionSource);
    }

    [TestMethod]
    public void RememberedWindowFallsBackToTopmostSameProcessWhenTitleChanged()
    {
        var remembered = Remembered("Idle", "Old Title", width: 1930, height: 1113);
        var topChangedTitle = Window(3, "Idle", "New Title");
        var lowerChangedTitle = Window(4, "Idle", "Other New Title");

        var result = GameWindowLocatorService.SelectWindowFromOrderedCandidates(
            new GameWindowSettings(),
            remembered,
            new[] { topChangedTitle, lowerChangedTitle });

        Assert.IsNotNull(result);
        Assert.AreEqual(topChangedTitle.Handle, result.Window.Handle);
        Assert.AreEqual("remembered-app", result.SelectionSource);
    }

    [TestMethod]
    public void ConfiguredSearchUsesTopmostMatchingWindow()
    {
        var settings = new GameWindowSettings
        {
            ProcessName = "Idle",
            TitleContains = "Uncharted"
        };
        var top = Window(5, "Idle", "Uncharted Waters Online", width: 800, height: 600);
        var lowerLarge = Window(6, "Idle", "Uncharted Waters Online", width: 1930, height: 1113);

        var result = GameWindowLocatorService.SelectWindowFromOrderedCandidates(
            settings,
            remembered: null,
            new[] { top, lowerLarge });

        Assert.IsNotNull(result);
        Assert.AreEqual(top.Handle, result.Window.Handle);
        Assert.AreEqual("configured-search", result.SelectionSource);
    }

    [TestMethod]
    public void PreferMouseSelectedSettingDoesNotOverrideZOrder()
    {
        var settings = new GameWindowSettings
        {
            PreferMouseSelectedWindow = true
        };
        var remembered = Remembered("Idle", "Uncharted Waters Online", width: 1930, height: 1113);
        var top = Window(7, "Idle", "Uncharted Waters Online");
        var lower = Window(8, "Idle", "Uncharted Waters Online");

        var result = GameWindowLocatorService.SelectWindowFromOrderedCandidates(
            settings,
            remembered,
            new[] { top, lower });

        Assert.IsNotNull(result);
        Assert.AreEqual(top.Handle, result.Window.Handle);
    }

    private static RememberedGameWindowSelection Remembered(
        string processName,
        string title,
        int width,
        int height)
        => new(processName, title, width, height, DateTime.UtcNow);

    private static GameWindowInfo Window(
        long handle,
        string processName,
        string title,
        int width = 1600,
        int height = 900)
        => new(new IntPtr(handle), processName, title, 0, 0, width, height);
}
