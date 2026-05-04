using OcrTradingBackend.Models;
using System.Drawing.Drawing2D;

namespace OcrTradingBackend.Services;

public interface IOcrImagePreprocessingService
{
    Bitmap? TryPrepareCoordinateImage(Bitmap source, OcrRuntimeSettings settings);
    Bitmap? TryPrepareCityImage(Bitmap source);
    Bitmap? TryPreparePriceImage(Bitmap source);
}

public sealed class OcrImagePreprocessingService : IOcrImagePreprocessingService
{
    private readonly IConfiguration _configuration;

    public OcrImagePreprocessingService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Bitmap? TryPrepareCoordinateImage(Bitmap source, OcrRuntimeSettings settings)
    {
        if (!settings.CoordinateTryPreprocess)
            return null;

        return OcrImagePreprocessor.PrepareCoordinateImage(
            source,
            settings.CoordinateOcrUpscale);
    }

    public Bitmap? TryPrepareCityImage(Bitmap source)
    {
        var enabled = _configuration.GetValue("OcrSettings:CityTryPreprocess", true);
        if (!enabled)
            return null;

        var scale = _configuration.GetValue("OcrSettings:CityOcrUpscale", 2);
        var threshold = _configuration.GetValue("OcrSettings:CityOcrThreshold", 145);
        var invert = _configuration.GetValue("OcrSettings:CityOcrInvert", false);

        return PrepareTextImage(source, scale, threshold, invert);
    }

    public Bitmap? TryPreparePriceImage(Bitmap source)
    {
        var enabled = _configuration.GetValue("OcrSettings:PriceTryPreprocess", true);
        if (!enabled)
            return null;

        var scale = _configuration.GetValue("OcrSettings:PriceOcrUpscale", 2);
        var threshold = _configuration.GetValue("OcrSettings:PriceOcrThreshold", 145);
        var invert = _configuration.GetValue("OcrSettings:PriceOcrInvert", false);

        return PrepareTextImage(source, scale, threshold, invert);
    }

    private static Bitmap PrepareTextImage(
        Bitmap source,
        int scale,
        int threshold,
        bool invert)
    {
        scale = Math.Clamp(scale, 1, 5);
        threshold = Math.Clamp(threshold, 0, 255);

        var scaled = new Bitmap(source.Width * scale, source.Height * scale);

        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        }

        for (var y = 0; y < scaled.Height; y++)
        {
            for (var x = 0; x < scaled.Width; x++)
            {
                var pixel = scaled.GetPixel(x, y);
                var gray = (pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114);

                var value = gray >= threshold ? 255 : 0;
                if (invert)
                    value = 255 - value;

                scaled.SetPixel(x, y, Color.FromArgb(value, value, value));
            }
        }

        return scaled;
    }
}