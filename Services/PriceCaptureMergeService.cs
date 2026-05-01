using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public enum PriceCaptureMergeAction
{
    Added,
    UpdatedExisting,
    Skipped
}

public sealed record PriceCaptureMergeResult(PriceCaptureMergeAction Action, string Message);

public static class PriceCaptureMergeService
{
    public static bool IsKnownCity(string? city)
    {
        return !string.IsNullOrWhiteSpace(city) &&
               !string.Equals(city.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsKnownTradeType(string? tradeType)
    {
        return string.Equals(tradeType, "Buy", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tradeType, "Sell", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<PriceCaptureMergeResult> AddOrUpdateAsync(
        AppDbContext db,
        PriceCapture capture,
        CancellationToken ct = default)
    {
        if (!IsKnownCity(capture.City))
            return new PriceCaptureMergeResult(PriceCaptureMergeAction.Skipped, "Skipped because city is unknown.");

        if (!IsKnownTradeType(capture.TradeType))
            return new PriceCaptureMergeResult(PriceCaptureMergeAction.Skipped, "Skipped because trade type is unknown.");

        capture.TradeType = NormalizeTradeType(capture.TradeType);
        capture.CapturedAtUtc = EnsureUtc(capture.CapturedAtUtc);

        var latest = await db.PriceCaptures
            .Where(x => x.City == capture.City &&
                        x.ItemName == capture.ItemName &&
                        x.TradeType == capture.TradeType)
            .OrderByDescending(x => x.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (latest is not null && SamePriceState(latest, capture))
        {
            // Same city/item/trade state. Do not create duplicate rows a few seconds apart.
            // Keep one row current by moving its timestamp forward and refreshing metadata.
            latest.CapturedAtUtc = capture.CapturedAtUtc > latest.CapturedAtUtc ? capture.CapturedAtUtc : DateTime.UtcNow;
            latest.RawText = capture.RawText;
            latest.TradeGoodType = capture.TradeGoodType;
            latest.Multiplier = capture.Multiplier;
            latest.Price = capture.Price;

            return new PriceCaptureMergeResult(PriceCaptureMergeAction.UpdatedExisting, "Updated existing latest price state.");
        }

        db.PriceCaptures.Add(capture);
        return new PriceCaptureMergeResult(PriceCaptureMergeAction.Added, "Added new price state.");
    }

    private static bool SamePriceState(PriceCapture current, PriceCapture incoming)
    {
        return current.Price == incoming.Price &&
               NullableDecimalEqual(current.Multiplier, incoming.Multiplier) &&
               string.Equals(current.TradeGoodType ?? string.Empty, incoming.TradeGoodType ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool NullableDecimalEqual(decimal? a, decimal? b)
    {
        if (!a.HasValue && !b.HasValue) return true;
        if (!a.HasValue || !b.HasValue) return false;
        return a.Value == b.Value;
    }

    private static string NormalizeTradeType(string tradeType)
    {
        return string.Equals(tradeType, "Buy", StringComparison.OrdinalIgnoreCase) ? "Buy" : "Sell";
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc) return value;
        if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
