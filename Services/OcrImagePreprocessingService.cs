using OcrTradingBackend.Models;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OcrTradingBackend.Services;

public interface IOcrImagePreprocessingService
{
    Bitmap? TryPrepareCoordinateImage(Bitmap source, OcrRuntimeSettings settings);
    Bitmap? TryPrepareCityImage(Bitmap source, OcrRuntimeSettings settings);
    Bitmap? TryPreparePriceImage(Bitmap source, OcrRuntimeSettings settings);
}

public sealed class OcrImagePreprocessingService : IOcrImagePreprocessingService
{
    public Bitmap? TryPrepareCoordinateImage(Bitmap source, OcrRuntimeSettings settings)
    {
        if (!settings.CoordinateTryPreprocess)
            return null;

        return OcrImagePreprocessor.PrepareCoordinateImage(
            source,
            settings.CoordinateOcrUpscale);
    }

    public Bitmap? TryPrepareCityImage(Bitmap source, OcrRuntimeSettings settings)
    {
        if (!settings.CityTryPreprocess)
            return null;

        return PrepareTextImage(
            source,
            settings.CityOcrUpscale,
            settings.CityOcrThreshold,
            settings.CityOcrInvert);
    }

    public Bitmap? TryPreparePriceImage(Bitmap source, OcrRuntimeSettings settings)
    {
        if (!settings.PriceTryPreprocess)
            return null;

        return PrepareTextImage(
            source,
            settings.PriceOcrUpscale,
            settings.PriceOcrThreshold,
            settings.PriceOcrInvert);
    }

    private static Bitmap PrepareTextImage(
        Bitmap source,
        int scale,
        int threshold,
        bool invert)
    {
        scale = Math.Clamp(scale, 1, 5);
        threshold = Math.Clamp(threshold, 0, 255);

        var scaled = new Bitmap(
            source.Width * scale,
            source.Height * scale,
            PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        }

        var rect = new Rectangle(0, 0, scaled.Width, scaled.Height);
        var data = scaled.LockBits(
            rect,
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * scaled.Height];

            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            for (var y = 0; y < scaled.Height; y++)
            {
                var rowOffset = y * stride;

                for (var x = 0; x < scaled.Width; x++)
                {
                    var offset = rowOffset + (x * 4);
                    var blue = bytes[offset];
                    var green = bytes[offset + 1];
                    var red = bytes[offset + 2];

                    var gray = (red * 0.299) + (green * 0.587) + (blue * 0.114);

                    var value = gray >= threshold ? (byte)255 : (byte)0;
                    if (invert)
                        value = (byte)(255 - value);

                    bytes[offset] = value;
                    bytes[offset + 1] = value;
                    bytes[offset + 2] = value;
                    bytes[offset + 3] = 255;
                }
            }

            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            scaled.UnlockBits(data);
        }

        return scaled;
    }
}
