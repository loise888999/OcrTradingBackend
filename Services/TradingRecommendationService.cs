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
        /*
         * Keep this query simple for EF/SQLite.
         * Complex GroupBy + First + ordering can trigger EF translation issues.
         */
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
            var buy = itemGroup
                .Where(x => x.TradeType == "Buy")
                .OrderBy(x => x.Price)
                .ThenBy(x => x.City)
                .FirstOrDefault();

            var sell = itemGroup
                .Where(x => x.TradeType == "Sell")
                .OrderByDescending(x => x.Price)
                .ThenBy(x => x.City)
                .FirstOrDefault();

            if (buy is null || sell is null)
                continue;

            var profit = sell.Price - buy.Price;
            if (profit <= 0)
                continue;

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

        return recommendations
            .OrderByDescending(x => x.Profit)
            .Take(20)
            .ToList();
    }
}

public static class TradingQueryService
{
    public static async Task<IReadOnlyList<TradingSearchResult>> SearchAsync(
        AppDbContext db,
        string? city,
        string? item,
        string? tradeType,
        int take)
    {
        var limit = Math.Clamp(take, 1, 2000);

        var query = db.PriceCaptures
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(city))
        {
            query = query.Where(x => x.City.Contains(city));
        }

        if (!string.IsNullOrWhiteSpace(item))
        {
            query = query.Where(x => x.ItemName.Contains(item));
        }

        if (!string.IsNullOrWhiteSpace(tradeType) && tradeType != "Any")
        {
            query = query.Where(x => x.TradeType == tradeType);
        }

        /*
         * Important fix:
         * Do NOT perform GroupBy + First + conditional OrderBy directly in EF/SQLite.
         * It can throw:
         *   KeyNotFoundException: EmptyProjectionMember
         *
         * We materialize first, then group/sort in memory.
         */
        var rows = await query
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(Math.Clamp(limit * 20, 100, 10000))
            .ToListAsync();

        var latest = rows
            .GroupBy(x => new
            {
                x.City,
                x.ItemName,
                x.TradeType
            })
            .Select(g => g
                .OrderByDescending(x => x.CapturedAtUtc)
                .First())
            .ToList();

        IEnumerable<PriceCapture> sorted;

        if (tradeType == "Buy")
        {
            // For buying, cheapest first.
            sorted = latest
                .OrderBy(x => x.Price)
                .ThenBy(x => x.City)
                .ThenBy(x => x.ItemName);
        }
        else if (tradeType == "Sell")
        {
            // For selling, highest first.
            sorted = latest
                .OrderByDescending(x => x.Price)
                .ThenBy(x => x.City)
                .ThenBy(x => x.ItemName);
        }
        else
        {
            // Mixed Buy/Sell view.
            sorted = latest
                .OrderBy(x => x.ItemName)
                .ThenBy(x => x.TradeType)
                .ThenBy(x => x.TradeType == "Buy" ? x.Price : -x.Price)
                .ThenBy(x => x.City);
        }

        return sorted
            .Take(limit)
            .Select(x => new TradingSearchResult(
                x.City,
                x.ItemName,
                x.TradeGoodType,
                x.Price,
                x.Multiplier,
                x.TradeType,
                x.CapturedAtUtc,
                x.RawText
            ))
            .ToList();
    }
}

public static class CsvExportService
{
    public static string ExportPrices(IEnumerable<PriceCapture> rows)
    {
        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

        var lines = new List<string> { "CapturedAtUtc,City,ItemName,TradeGoodType,Price,Multiplier,TradeType,RawText" };
        lines.AddRange(rows.Select(x => string.Join(',',
            x.CapturedAtUtc.ToString("O"),
            Csv(x.City),
            Csv(x.ItemName),
            Csv(x.TradeGoodType),
            x.Price,
            x.Multiplier?.ToString() ?? "",
            Csv(x.TradeType),
            Csv(x.RawText))));

        return string.Join(Environment.NewLine, lines);
    }
}
