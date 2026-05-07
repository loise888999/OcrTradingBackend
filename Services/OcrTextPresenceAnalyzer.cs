using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record OcrTextPresenceResult(
    bool MayContainText,
    int Contrast,
    double EdgePixelsPercent,
    int SampledPixels);

public interface IOcrTextPresenceAnalyzer
{
    OcrTextPresenceResult Analyze(Bitmap bitmap, OcrRuntimeSettings settings);
}

public sealed class OcrTextPresenceAnalyzer : IOcrTextPresenceAnalyzer
{
    public OcrTextPresenceResult Analyze(Bitmap bitmap, OcrRuntimeSettings settings)
    {
        var step = Math.Clamp(settings.OcrTextPresenceSampleStep, 1, 32);
        var minContrast = Math.Clamp(settings.OcrTextPresenceMinContrast, 0, 255);
        var minEdgePercent = Math.Clamp(settings.OcrTextPresenceMinEdgePixelsPercent, 0.0, 100.0);

        using var normalized = CreateNormalizedBitmap(bitmap);
        var rect = new Rectangle(0, 0, normalized.Width, normalized.Height);
        var data = normalized.LockBits(
            rect,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var minGray = 255;
            var maxGray = 0;
            var sampled = 0;
            var edgeCount = 0;
            var edgeComparisons = 0;
            var previousRow = new int[(normalized.Width + step - 1) / step];

            Array.Fill(previousRow, -1);

            for (var y = 0; y < normalized.Height; y += step)
            {
                var rowOffset = y * data.Stride;
                var previousGray = -1;
                var sampleX = 0;

                for (var x = 0; x < normalized.Width; x += step)
                {
                    var offset = rowOffset + (x * 4);
                    var blue = Marshal.ReadByte(data.Scan0, offset);
                    var green = Marshal.ReadByte(data.Scan0, offset + 1);
                    var red = Marshal.ReadByte(data.Scan0, offset + 2);
                    var gray = (int)((red * 0.299) + (green * 0.587) + (blue * 0.114));

                    minGray = Math.Min(minGray, gray);
                    maxGray = Math.Max(maxGray, gray);
                    sampled++;

                    if (previousGray >= 0)
                    {
                        edgeComparisons++;
                        if (Math.Abs(gray - previousGray) >= minContrast)
                            edgeCount++;
                    }

                    if (previousRow[sampleX] >= 0)
                    {
                        edgeComparisons++;
                        if (Math.Abs(gray - previousRow[sampleX]) >= minContrast)
                            edgeCount++;
                    }

                    previousRow[sampleX] = gray;
                    previousGray = gray;
                    sampleX++;
                }
            }

            var contrast = maxGray - minGray;
            var edgePercent = edgeComparisons == 0
                ? 0
                : edgeCount * 100.0 / edgeComparisons;

            return new OcrTextPresenceResult(
                MayContainText: contrast >= minContrast && edgePercent >= minEdgePercent,
                Contrast: contrast,
                EdgePixelsPercent: edgePercent,
                SampledPixels: sampled);
        }
        finally
        {
            normalized.UnlockBits(data);
        }
    }

    private static Bitmap CreateNormalizedBitmap(Bitmap source)
    {
        var normalized = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(normalized);
        graphics.DrawImage(source, 0, 0, source.Width, source.Height);

        return normalized;
    }
}
