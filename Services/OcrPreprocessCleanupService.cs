using System.Drawing;
using System.Drawing.Imaging;

namespace OcrTradingBackend.Services;

public sealed record OcrPreprocessCleanupOptions(
    bool Enabled,
    bool RemoveSmallBlobsEnabled,
    int MinWhiteBlobPixels,
    bool TextShapeFilterEnabled,
    int MinTextLikeBlobWidth,
    int MinTextLikeBlobHeight,
    int MaxTextLikeBlobHeightPercent);

public static class OcrPreprocessCleanupService
{
    public static void CleanBinaryImage(Bitmap bitmap, OcrPreprocessCleanupOptions options)
    {
        if (!options.Enabled ||
            (!options.RemoveSmallBlobsEnabled && !options.TextShapeFilterEnabled) ||
            bitmap.Width <= 0 ||
            bitmap.Height <= 0)
        {
            return;
        }

        var width = bitmap.Width;
        var height = bitmap.Height;
        var white = ReadWhitePixels(bitmap);
        var visited = new bool[width, height];
        var pixelsToClear = new List<Point>();
        var minBlobPixels = Math.Max(1, options.MinWhiteBlobPixels);
        var minTextWidth = Math.Max(1, options.MinTextLikeBlobWidth);
        var minTextHeight = Math.Max(1, options.MinTextLikeBlobHeight);
        var maxTextHeight = Math.Max(1, (int)Math.Round(height * Math.Clamp(options.MaxTextLikeBlobHeightPercent, 1, 100) / 100.0));

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!white[x, y] || visited[x, y])
                    continue;

                var component = FloodFill(white, visited, x, y, width, height);
                var remove = false;

                if (options.RemoveSmallBlobsEnabled &&
                    component.Pixels.Count < minBlobPixels)
                {
                    remove = true;
                }

                if (!remove && options.TextShapeFilterEnabled)
                {
                    var componentWidth = component.MaxX - component.MinX + 1;
                    var componentHeight = component.MaxY - component.MinY + 1;

                    if (componentWidth < minTextWidth ||
                        componentHeight < minTextHeight ||
                        componentHeight > maxTextHeight)
                    {
                        remove = true;
                    }
                }

                if (remove)
                    pixelsToClear.AddRange(component.Pixels);
            }
        }

        if (pixelsToClear.Count == 0)
            return;

        foreach (var point in pixelsToClear)
            bitmap.SetPixel(point.X, point.Y, Color.Black);
    }

    private static bool[,] ReadWhitePixels(Bitmap source)
    {
        var normalized = source.PixelFormat == PixelFormat.Format32bppArgb
            ? source
            : new Bitmap(source);

        try
        {
            var white = new bool[source.Width, source.Height];

            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var pixel = normalized.GetPixel(x, y);
                    white[x, y] = pixel.R >= 128 || pixel.G >= 128 || pixel.B >= 128;
                }
            }

            return white;
        }
        finally
        {
            if (!ReferenceEquals(normalized, source))
                normalized.Dispose();
        }
    }

    private static Component FloodFill(
        bool[,] white,
        bool[,] visited,
        int startX,
        int startY,
        int width,
        int height)
    {
        var queue = new Queue<Point>();
        var pixels = new List<Point>();
        var minX = startX;
        var minY = startY;
        var maxX = startX;
        var maxY = startY;

        visited[startX, startY] = true;
        queue.Enqueue(new Point(startX, startY));

        while (queue.Count > 0)
        {
            var point = queue.Dequeue();
            pixels.Add(point);
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);

            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    var nextX = point.X + dx;
                    var nextY = point.Y + dy;

                    if (nextX < 0 ||
                        nextY < 0 ||
                        nextX >= width ||
                        nextY >= height ||
                        visited[nextX, nextY] ||
                        !white[nextX, nextY])
                    {
                        continue;
                    }

                    visited[nextX, nextY] = true;
                    queue.Enqueue(new Point(nextX, nextY));
                }
            }
        }

        return new Component(pixels, minX, minY, maxX, maxY);
    }

    private sealed record Component(
        IReadOnlyList<Point> Pixels,
        int MinX,
        int MinY,
        int MaxX,
        int MaxY);
}
