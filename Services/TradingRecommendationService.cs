using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface ITradingRecommendationService
{
    Task<IReadOnlyList<TradingRecommendation>> GetRecommendationsAsync();
}

public sealed class TradingRecommendationService : ITradingRecommendationService
{
    private readonly AppDbContext _db;
    public TradingRecommendationService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<TradingRecommendation>> GetRecommendationsAsync()
    {
        var rows = await _db.PriceCaptures
            .AsNoTracking()
            .Where(x => x.TradeType == "Buy" || x.TradeType == "Sell")
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(10000)
            .ToListAsync();

        var latestPerCityItemTradeType = rows
            .GroupBy(x => new { x.City, x.ItemName, x.TradeType })
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .ToList();

        var recommendations = new List<TradingRecommendation>();

        foreach (var itemGroup in latestPerCityItemTradeType.GroupBy(x => x.ItemName))
        {
            var buy = itemGroup.Where(x => x.TradeType == "Buy").OrderBy(x => x.Price).ThenBy(x => x.City).FirstOrDefault();
            var sell = itemGroup.Where(x => x.TradeType == "Sell").OrderByDescending(x => x.Price).ThenBy(x => x.City).FirstOrDefault();
            if (buy is null || sell is null) continue;
            var profit = sell.Price - buy.Price;
            if (profit <= 0) continue;

            recommendations.Add(new TradingRecommendation(
                buy.ItemName,
                string.IsNullOrWhiteSpace(buy.TradeGoodType) ? sell.TradeGoodType : buy.TradeGoodType,
                buy.City,
                buy.Price,
                sell.City,
                sell.Price,
                profit,
                buy.Multiplier,
                sell.Multiplier));
        }

        return recommendations.OrderByDescending(x => x.Profit).Take(20).ToList();
    }
}

public static class TradingQueryService
{
    public static async Task<IReadOnlyList<TradingSearchResult>> SearchAsync(AppDbContext db, string? city, string? item, string? tradeType, int take)
    {
        var limit = Math.Clamp(take, 1, 2000);
        var query = db.PriceCaptures.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(city)) query = query.Where(x => x.City.Contains(city));
        if (!string.IsNullOrWhiteSpace(item)) query = query.Where(x => x.ItemName.Contains(item));
        if (!string.IsNullOrWhiteSpace(tradeType) && tradeType != "Any") query = query.Where(x => x.TradeType == tradeType);

        var rows = await query
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(Math.Clamp(limit * 20, 100, 10000))
            .ToListAsync();

        var latest = rows
            .GroupBy(x => new { x.City, x.ItemName, x.TradeType })
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .ToList();

        IEnumerable<PriceCapture> sorted = tradeType switch
        {
            "Buy" => latest.OrderBy(x => x.Price).ThenBy(x => x.City).ThenBy(x => x.ItemName),
            "Sell" => latest.OrderByDescending(x => x.Price).ThenBy(x => x.City).ThenBy(x => x.ItemName),
            _ => latest.OrderBy(x => x.ItemName).ThenBy(x => x.TradeType).ThenBy(x => x.TradeType == "Buy" ? x.Price : -x.Price).ThenBy(x => x.City)
        };

        return sorted
            .Take(limit)
            .Select(x => new TradingSearchResult(x.City, x.ItemName, x.TradeGoodType, x.Price, x.Multiplier, x.TradeType, x.CapturedAtUtc, x.RawText))
            .ToList();
    }
}

public static class CsvExportService
{
    public static string ExportPrices(IEnumerable<PriceCapture> rows)
    {
        static string Csv(string value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
        static string DecimalText(decimal value) => value.ToString(CultureInfo.InvariantCulture);
        static string NullableDecimalText(decimal? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

        // Export latest known state only: one row per City + Item + Buy/Sell.
        var latestRows = rows
            .Where(x => PriceCaptureMergeService.IsKnownCity(x.City) && PriceCaptureMergeService.IsKnownTradeType(x.TradeType))
            .GroupBy(x => new { x.City, x.ItemName, x.TradeType })
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .OrderBy(x => x.City)
            .ThenBy(x => x.ItemName)
            .ThenBy(x => x.TradeType)
            .ToList();

        var lines = new List<string> { "CapturedAtUtc,City,ItemName,TradeGoodType,Price,Multiplier,TradeType,RawText" };

        lines.AddRange(latestRows.Select(x => string.Join(',',
            x.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Csv(x.City),
            Csv(x.ItemName),
            Csv(x.TradeGoodType),
            DecimalText(x.Price),
            NullableDecimalText(x.Multiplier),
            Csv(x.TradeType),
            Csv(x.RawText))));

        return string.Join(Environment.NewLine, lines);
    }
}
