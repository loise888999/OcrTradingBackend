using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class OcrImageTextCacheTests
{
    [TestMethod]
    public void CacheHitDoesNotRunOcrAgain()
    {
        var now = DateTime.UtcNow;
        var cache = new OcrImageTextCache(
            new OcrImageHasher(),
            () => now,
            TimeSpan.FromSeconds(10));
        using var bitmap = CreateBitmap(Color.White);
        var calls = 0;
        var options = BuildOptions(maxEntries: 10);

        var first = cache.ReadText("test", bitmap, _ =>
        {
            calls++;
            return "first";
        }, options);

        var second = cache.ReadText("test", bitmap, _ =>
        {
            calls++;
            return "second";
        }, options);

        Assert.AreEqual("first", first.Text);
        Assert.AreEqual("first", second.Text);
        Assert.IsFalse(first.WasHashHit);
        Assert.IsTrue(second.WasHashHit);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void CachePrunesOversizeEvenWhenPruneIntervalHasNotElapsed()
    {
        var now = DateTime.UtcNow;
        var cache = new OcrImageTextCache(
            new OcrImageHasher(),
            () => now,
            TimeSpan.FromMinutes(1));
        using var firstBitmap = CreateBitmap(Color.White);
        using var secondBitmap = CreateBitmap(Color.Black);
        var options = BuildOptions(maxEntries: 1);

        cache.ReadText("test", firstBitmap, _ => "first", options);
        var second = cache.ReadText("test", secondBitmap, _ => "second", options);

        Assert.IsTrue(second.EvictedCount > 0);
        Assert.IsTrue(second.CacheEntryCount <= 1);
    }

    [TestMethod]
    public void CachePrunesExpiredEntriesWhenPruneIntervalElapsed()
    {
        var now = DateTime.UtcNow;
        var cache = new OcrImageTextCache(
            new OcrImageHasher(),
            () => now,
            TimeSpan.FromSeconds(1));
        using var firstBitmap = CreateBitmap(Color.White);
        using var secondBitmap = CreateBitmap(Color.Black);
        var options = BuildOptions(maxEntries: 10);

        cache.ReadText("test", firstBitmap, _ => "first", options);
        now = now.AddSeconds(7);

        var second = cache.ReadText("test", secondBitmap, _ => "second", options);

        Assert.IsTrue(second.EvictedCount > 0);
    }

    private static OcrHashCacheOptions BuildOptions(int maxEntries)
        => new(
            Enabled: true,
            TtlMinutes: 0.1,
            MaxEntries: maxEntries,
            SettingsSignature: "test-settings",
            BenchmarkLogging: false);

    private static Bitmap CreateBitmap(Color color)
    {
        var bitmap = new Bitmap(8, 8);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }
}
