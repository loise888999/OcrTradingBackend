using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class OcrPreprocessCleanupTests
{
    [TestMethod]
    public void RemovesIsolatedOnePixelWhiteDots()
    {
        using var bitmap = NewBinaryBitmap(8, 8);
        SetWhite(bitmap, 1, 1);
        DrawBlob(bitmap, 4, 1, 2, 3);

        OcrPreprocessCleanupService.CleanBinaryImage(bitmap, Options(minBlobPixels: 3));

        AssertBlack(bitmap, 1, 1);
        AssertWhite(bitmap, 4, 1);
        AssertWhite(bitmap, 5, 3);
    }

    [TestMethod]
    public void RemovesTwoPixelBlobWhenMinimumIsThree()
    {
        using var bitmap = NewBinaryBitmap(8, 8);
        SetWhite(bitmap, 1, 1);
        SetWhite(bitmap, 2, 1);
        DrawBlob(bitmap, 4, 1, 2, 2);

        OcrPreprocessCleanupService.CleanBinaryImage(bitmap, Options(minBlobPixels: 3));

        AssertBlack(bitmap, 1, 1);
        AssertBlack(bitmap, 2, 1);
        AssertWhite(bitmap, 4, 1);
        AssertWhite(bitmap, 5, 2);
    }

    [TestMethod]
    public void KeepsLargerDigitLikeBlob()
    {
        using var bitmap = NewBinaryBitmap(8, 8);
        DrawBlob(bitmap, 2, 1, 2, 5);

        OcrPreprocessCleanupService.CleanBinaryImage(bitmap, Options(minBlobPixels: 3));

        AssertWhite(bitmap, 2, 1);
        AssertWhite(bitmap, 3, 5);
    }

    [TestMethod]
    public void TextShapeFilterRemovesTinyNonTextBlob()
    {
        using var bitmap = NewBinaryBitmap(10, 10);
        DrawBlob(bitmap, 1, 1, 5, 1);
        DrawBlob(bitmap, 7, 1, 2, 5);

        OcrPreprocessCleanupService.CleanBinaryImage(
            bitmap,
            Options(
                minBlobPixels: 1,
                textShapeEnabled: true,
                minTextWidth: 2,
                minTextHeight: 4));

        AssertBlack(bitmap, 1, 1);
        AssertBlack(bitmap, 5, 1);
        AssertWhite(bitmap, 7, 1);
        AssertWhite(bitmap, 8, 5);
    }

    [TestMethod]
    public void DisabledCleanupPreservesOriginalPixels()
    {
        using var bitmap = NewBinaryBitmap(8, 8);
        SetWhite(bitmap, 1, 1);

        OcrPreprocessCleanupService.CleanBinaryImage(
            bitmap,
            Options(enabled: false));

        AssertWhite(bitmap, 1, 1);
    }

    private static OcrPreprocessCleanupOptions Options(
        bool enabled = true,
        int minBlobPixels = 3,
        bool textShapeEnabled = false,
        int minTextWidth = 2,
        int minTextHeight = 4)
    {
        return new OcrPreprocessCleanupOptions(
            Enabled: enabled,
            RemoveSmallBlobsEnabled: true,
            MinWhiteBlobPixels: minBlobPixels,
            TextShapeFilterEnabled: textShapeEnabled,
            MinTextLikeBlobWidth: minTextWidth,
            MinTextLikeBlobHeight: minTextHeight,
            MaxTextLikeBlobHeightPercent: 90);
    }

    private static Bitmap NewBinaryBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        return bitmap;
    }

    private static void DrawBlob(Bitmap bitmap, int x, int y, int width, int height)
    {
        for (var yy = y; yy < y + height; yy++)
        {
            for (var xx = x; xx < x + width; xx++)
                SetWhite(bitmap, xx, yy);
        }
    }

    private static void SetWhite(Bitmap bitmap, int x, int y)
        => bitmap.SetPixel(x, y, Color.White);

    private static void AssertWhite(Bitmap bitmap, int x, int y)
        => Assert.IsTrue(IsWhite(bitmap.GetPixel(x, y)), $"Expected white pixel at {x},{y}.");

    private static void AssertBlack(Bitmap bitmap, int x, int y)
        => Assert.IsFalse(IsWhite(bitmap.GetPixel(x, y)), $"Expected black pixel at {x},{y}.");

    private static bool IsWhite(Color color)
        => color.R >= 128 || color.G >= 128 || color.B >= 128;
}
