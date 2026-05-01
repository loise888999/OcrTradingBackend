using System.Drawing;
using System.Drawing.Drawing2D;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public static class CoordinateScreenSearchService
{
    public static ParsedCoordinate? TryReadCoordinate(
        IScreenCaptureService capture,
        IPaddleOcrService ocr,
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

        if (!settings.CoordinateSearchEnabled)
            return null;

        // 2. Fallback path: screen-only padded search area around the fixed zone.
        // This helps if the coordinate text moved slightly due to UI scaling/layout.
        var searchZone = BuildPaddedSearchZone(coordinateZone, settings.CoordinateSearchPadding);

        using (var searchBitmap = capture.Capture(searchZone))
        {
            var searchResult = TryOcrAndParse(ocr, parser, searchBitmap, previousCoordinate, settings, "search");
            if (searchResult is not null) return searchResult;

            if (settings.CoordinateTryPreprocess)
            {
                using var preprocessed = OcrImagePreprocessor.PrepareCoordinateImage(searchBitmap, settings.CoordinateOcrUpscale);
                var preprocessedSearchResult = TryOcrAndParse(ocr, parser, preprocessed, previousCoordinate, settings, "search-preprocessed");
                if (preprocessedSearchResult is not null) return preprocessedSearchResult;
            }
        }

        return null;
    }

    private static ParsedCoordinate? TryOcrAndParse(
        IPaddleOcrService ocr,
        ICoordinateParser parser,
        Bitmap bitmap,
        CoordinateCapture? previousCoordinate,
        OcrRuntimeSettings settings,
        string source)
    {
        var rawText = ocr.DetectText(bitmap);
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

    private static OcrZone BuildPaddedSearchZone(OcrZone zone, int padding)
    {
        var left = Math.Min(zone.TopLeftX, zone.BottomRightX);
        var top = Math.Min(zone.TopLeftY, zone.BottomRightY);
        var right = Math.Max(zone.TopLeftX, zone.BottomRightX);
        var bottom = Math.Max(zone.TopLeftY, zone.BottomRightY);

        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;

        left = Math.Max(virtualScreen.Left, left - padding);
        top = Math.Max(virtualScreen.Top, top - padding);
        right = Math.Min(virtualScreen.Right, right + padding);
        bottom = Math.Min(virtualScreen.Bottom, bottom + padding);

        return new OcrZone
        {
            Name = "CoordinateSearch",
            TopLeftX = left,
            TopLeftY = top,
            BottomRightX = right,
            BottomRightY = bottom,
            UpdatedAtUtc = DateTime.UtcNow
        };
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
