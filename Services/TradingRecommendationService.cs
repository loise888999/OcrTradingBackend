using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record TradingRegionFilter(
    string? MainRegion,
    string? SubRegion,
    string? SeaTradeRegion,
    string? BuyMainRegion,
    string? BuySubRegion,
    string? BuySeaTradeRegion,
    string? SellMainRegion,
    string? SellSubRegion,
    string? SellSeaTradeRegion);

public interface ITradingRecommendationService
{
    Task<IReadOnlyList<TradingRecommendation>> GetRecommendationsAsync();
    Task<IReadOnlyList<TradingRecommendation>> GetRecommendationsAsync(TradingRegionFilter filter);
}

public sealed class TradingRecommendationService : ITradingRecommendationService
{
    private readonly AppDbContext _db;
    private readonly ICityCatalog _cities;

    public TradingRecommendationService(AppDbContext db, ICityCatalog cities)
    {
        _db = db;
        _cities = cities;
    }

    public Task<IReadOnlyList<TradingRecommendation>> GetRecommendationsAsync()
    {
        return GetRecommendationsAsync(new TradingRegionFilter(null, null, null, null, null, null, null, null, null));
    }

    public async Task<IReadOnlyList<TradingRecommendation>> GetRecommendationsAsync(TradingRegionFilter filter)
    {
        var rows = await _db.PriceCaptures
            .AsNoTracking()
            .Where(x => x.TradeType == "Buy" || x.TradeType == "Sell")
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(10000)
            .ToListAsync();

        rows = rows
            .Where(x => CityMatches(x.City, filter.MainRegion, filter.SubRegion, filter.SeaTradeRegion))
            .ToList();

        var latestPerCityItemTradeType = rows
            .GroupBy(x => new { x.City, x.ItemName, x.TradeType })
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .ToList();

        var recommendations = new List<TradingRecommendation>();

        foreach (var itemGroup in latestPerCityItemTradeType.GroupBy(x => x.ItemName))
        {
            var buy = itemGroup
                .Where(x => x.TradeType == "Buy")
                .Where(x => CityMatches(x.City, filter.BuyMainRegion, filter.BuySubRegion, filter.BuySeaTradeRegion))
                .OrderBy(x => x.Price)
                .ThenBy(x => x.City)
                .FirstOrDefault();

            var sell = itemGroup
                .Where(x => x.TradeType == "Sell")
                .Where(x => CityMatches(x.City, filter.SellMainRegion, filter.SellSubRegion, filter.SellSeaTradeRegion))
                .OrderByDescending(x => x.Price)
                .ThenBy(x => x.City)
                .FirstOrDefault();

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

        return recommendations.OrderByDescending(x => x.Profit).Take(50).ToList();
    }

    private bool CityMatches(string cityName, string? mainRegion, string? subRegion, string? seaTradeRegion)
    {
        var city = _cities.FindByName(cityName);
        if (city is null) return false;
        if (!RegionMatches(city.MainRegion, mainRegion)) return false;
        if (!RegionMatches(city.SubRegion, subRegion)) return false;
        if (!RegionMatches(city.SeaTradeRegion, seaTradeRegion)) return false;
        return true;
    }

    private static bool RegionMatches(string value, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter) ||
               string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
    }
}

public static class TradingQueryService
{
    public static async Task<IReadOnlyList<TradingSearchResult>> SearchAsync(
        AppDbContext db,
        ICityCatalog cities,
        string? city,
        string? item,
        string? tradeType,
        string? mainRegion,
        string? subRegion,
        string? seaTradeRegion,
        int take)
    {
        var limit = Math.Clamp(take, 1, 2000);
        var query = db.PriceCaptures.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(city)) query = query.Where(x => x.City.Contains(city));
        if (!string.IsNullOrWhiteSpace(item)) query = query.Where(x => x.ItemName.Contains(item));
        if (!string.IsNullOrWhiteSpace(tradeType) && tradeType != "Any") query = query.Where(x => x.TradeType == tradeType);

        var rows = await query
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(Math.Clamp(limit * 30, 100, 20000))
            .ToListAsync();

        rows = rows
            .Where(x => CityMatches(cities, x.City, mainRegion, subRegion, seaTradeRegion))
            .ToList();

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

    private static bool CityMatches(ICityCatalog cities, string cityName, string? mainRegion, string? subRegion, string? seaTradeRegion)
    {
        var city = cities.FindByName(cityName);
        if (city is null) return false;
        if (!RegionMatches(city.MainRegion, mainRegion)) return false;
        if (!RegionMatches(city.SubRegion, subRegion)) return false;
        if (!RegionMatches(city.SeaTradeRegion, seaTradeRegion)) return false;
        return true;
    }

    private static bool RegionMatches(string value, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter) ||
               string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
    }
}
