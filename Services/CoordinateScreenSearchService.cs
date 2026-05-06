using System.Drawing;
using System.Drawing.Drawing2D;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public static class CoordinateScreenSearchService
{
    public static ParsedCoordinate? TryReadCoordinate(
        IScreenCaptureService capture,
        IOcrCachedTextService ocr,
        ICoordinateParser parser,
        OcrZone coordinateZone,
        CoordinateCapture? previousCoordinate,
        OcrRuntimeSettings settings)
    {
        // 1. Fast path: current fixed coordinate zone, original image.
        using (var fixedBitmap = capture.Capture(coordinateZone))
        {
            var direct = TryOcrAndParse(ocr, parser, fixedBitmap, previousCoordinate, settings, "fixed");
            if (direct is not null) return direct;

            if (settings.CoordinateTryPreprocess)
            {
                using var preprocessed = OcrImagePreprocessor.PrepareCoordinateImage(fixedBitmap, settings.CoordinateOcrUpscale);
                var preprocessedResult = TryOcrAndParse(ocr, parser, preprocessed, previousCoordinate, settings, "fixed-preprocessed");
                if (preprocessedResult is not null) return preprocessedResult;
            }
        }

        return null;
    }

    private static ParsedCoordinate? TryOcrAndParse(
        IOcrCachedTextService ocr,
        ICoordinateParser parser,
        Bitmap bitmap,
        CoordinateCapture? previousCoordinate,
        OcrRuntimeSettings settings,
        string source)
    {
        var rawText = ocr.ReadText(
            $"coordinate-search:{source}",
            bitmap,
            OcrFieldKind.Coordinate,
            settings).Text;
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        var parsed = parser.TryParse(
            rawText,
            settings.WorldWidth,
            settings.WorldHeight,
            previousCoordinate,
            new CoordinateCorrectionOptions(
                settings.EnableCoordinateCorrection,
                settings.MaxCoordinateJumpX,
                settings.MaxCoordinateJumpY));

        if (parsed is null) return null;

        return parsed with { RawText = $"{source}: {parsed.RawText}" };
    }

}

public static class OcrImagePreprocessor
{
    public static Bitmap PrepareCoordinateImage(Bitmap source, int scale)
    {
        scale = Math.Clamp(scale, 1, 5);

        var scaled = new Bitmap(source.Width * scale, source.Height * scale);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        }

        // Convert to high-contrast grayscale/threshold.
        // This is intentionally simple and CPU-light for the small coordinate region.
        for (var y = 0; y < scaled.Height; y++)
        {
            for (var x = 0; x < scaled.Width; x++)
            {
                var pixel = scaled.GetPixel(x, y);
                var gray = (pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114);

                // Keep bright UI text bright and darken the background.
                var value = gray >= 145 ? 255 : 0;
                scaled.SetPixel(x, y, Color.FromArgb(value, value, value));
            }
        }

        return scaled;
    }
}
