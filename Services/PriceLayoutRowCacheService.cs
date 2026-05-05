using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record PriceLayoutRowFingerprint(ulong Low, ulong High)
{
    public int DistanceTo(PriceLayoutRowFingerprint other)
    {
        return BitOperations.PopCount(Low ^ other.Low) +
               BitOperations.PopCount(High ^ other.High);
    }
}

public interface IPriceLayoutRowFingerprintService
{
    PriceLayoutRowFingerprint Compute(Bitmap bitmap);
}

public sealed class PriceLayoutRowFingerprintService : IPriceLayoutRowFingerprintService
{
    private const int Width = 17;
    private const int Height = 8;

    public PriceLayoutRowFingerprint Compute(Bitmap bitmap)
    {
        using var scaled = new Bitmap(Width, Height);

        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.DrawImage(bitmap, 0, 0, Width, Height);
        }

        Span<byte> gray = stackalloc byte[Width * Height];

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var pixel = scaled.GetPixel(x, y);
                gray[(y * Width) + x] = (byte)((pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114));
            }
        }

        ulong low = 0;
        ulong high = 0;
        var bit = 0;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width - 1; x++)
            {
                if (gray[(y * Width) + x] > gray[(y * Width) + x + 1])
                {
                    if (bit < 64)
                        low |= 1UL << bit;
                    else
                        high |= 1UL << (bit - 64);
                }

                bit++;
            }
        }

        return new PriceLayoutRowFingerprint(low, high);
    }
}

public interface IPriceLayoutRowCacheService
{
    bool TryGet(
        string rowKey,
        string tradeType,
        PriceLayoutRowFingerprint fingerprint,
        int maxDistance,
        out ParsedPriceLine? parsed,
        out int distance);

    void Remember(
        string rowKey,
        string tradeType,
        PriceLayoutRowFingerprint fingerprint,
        ParsedPriceLine? parsed);
}

public sealed class PriceLayoutRowCacheService : IPriceLayoutRowCacheService
{
    private readonly ConcurrentDictionary<string, CachedPriceLayoutRow> _rows = new(StringComparer.Ordinal);

    public bool TryGet(
        string rowKey,
        string tradeType,
        PriceLayoutRowFingerprint fingerprint,
        int maxDistance,
        out ParsedPriceLine? parsed,
        out int distance)
    {
        if (_rows.TryGetValue(rowKey, out var cached))
        {
            distance = cached.Fingerprint.DistanceTo(fingerprint);
            if (distance <= maxDistance)
            {
                parsed = cached.Parsed;
                return true;
            }
        }

        CachedPriceLayoutRow? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in _rows.Values)
        {
            if (!string.Equals(candidate.TradeType, tradeType, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidateDistance = candidate.Fingerprint.DistanceTo(fingerprint);
            if (candidateDistance < bestDistance)
            {
                best = candidate;
                bestDistance = candidateDistance;
            }
        }

        if (best is not null && bestDistance <= maxDistance)
        {
            parsed = best.Parsed;
            distance = bestDistance;
            return true;
        }

        parsed = null;
        distance = bestDistance == int.MaxValue ? -1 : bestDistance;
        return false;
    }

    public void Remember(
        string rowKey,
        string tradeType,
        PriceLayoutRowFingerprint fingerprint,
        ParsedPriceLine? parsed)
    {
        _rows[rowKey] = new CachedPriceLayoutRow(tradeType, fingerprint, parsed);
    }

    private sealed record CachedPriceLayoutRow(
        string TradeType,
        PriceLayoutRowFingerprint Fingerprint,
        ParsedPriceLine? Parsed);
}
