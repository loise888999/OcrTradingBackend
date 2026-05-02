using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record TradingAdvancedSearchResult(
    string City,
    string ItemName,
    string TradeGoodType,
    int Price,
    int? Multiplier,
    string TradeType,
    DateTime CapturedAtUtc,
    string RawText,
    string MainRegion,
    string SubRegion,
    string SeaTradeRegion);

public sealed record TradeGoodLookupResult(
    string ItemName,
    string TradeGoodType,
    int LowestBuyPrice,
    string LowestBuyCity,
    string LowestBuyMainRegion,
    string LowestBuySubRegion,
    string LowestBuySeaTradeRegion,
    int OfferCount,
    DateTime LastSeenUtc);

public sealed record MultiGoodRouteResult(
    string BuyCity,
    string SellCity,
    string BuyMainRegion,
    string BuySubRegion,
    string BuySeaTradeRegion,
    string SellMainRegion,
    string SellSubRegion,
    string SellSeaTradeRegion,
    int TotalProfit,
    int ItemCount,
    IReadOnlyList<MultiGoodRouteItem> Items);

public sealed record MultiGoodRouteItem(
    string ItemName,
    string TradeGoodType,
    int BuyPrice,
    int SellPrice,
    int Profit);

public interface ITradingAdvancedService
{
    Task<IReadOnlyList<TradeGoodLookupResult>> LookupBuyGoodsAsync(string? item, string? type, string? mainRegion, string? subRegion, int take);
    Task<IReadOnlyList<TradingAdvancedSearchResult>> GetKnownPricesAsync(string? item, string? type, string? tradeType, string? mainRegion, string? subRegion, string? seaTradeRegion, int take);
    Task<IReadOnlyList<TradingRecommendation>> GetAdvancedRoutesAsync(string? item, string? type, IReadOnlyList<string> buyRegions, IReadOnlyList<string> sellRegions, int minProfit, int routesPerItem, int take);
    Task<IReadOnlyList<MultiGoodRouteResult>> GetMultiGoodRoutesAsync(string? type, IReadOnlyList<string> buyRegions, IReadOnlyList<string> sellRegions, int minProfitPerGood, int minTotalProfit, int minItems, int take);
}

public sealed class TradingAdvancedService : ITradingAdvancedService
{
    private readonly AppDbContext _db;
    private readonly ICityCatalog _cities;

    public TradingAdvancedService(AppDbContext db, ICityCatalog cities)
    {
        _db = db;
        _cities = cities;
    }

    public async Task<IReadOnlyList<TradeGoodLookupResult>> LookupBuyGoodsAsync(string? item, string? type, string? mainRegion, string? subRegion, int take)
    {
        var limit = Math.Clamp(take, 1, 1000);
        var rows = await LatestRowsAsync(item, type, "Buy", mainRegion, subRegion, null, limit * 20);

        return rows
            .GroupBy(x => x.ItemName)
            .Select(g =>
            {
                var best = g.OrderBy(x => x.Price).ThenBy(x => x.City).First();
                var city = _cities.FindByName(best.City);

                return new TradeGoodLookupResult(
                    best.ItemName,
                    best.TradeGoodType,
                    PriceToInt(best.Price),
                    best.City,
                    city?.MainRegion ?? string.Empty,
                    city?.SubRegion ?? string.Empty,
                    city?.SeaTradeRegion ?? string.Empty,
                    g.Count(),
                    g.Max(x => x.CapturedAtUtc));
            })
            .OrderBy(x => x.ItemName)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<TradingAdvancedSearchResult>> GetKnownPricesAsync(string? item, string? type, string? tradeType, string? mainRegion, string? subRegion, string? seaTradeRegion, int take)
    {
        var rows = await LatestRowsAsync(item, type, tradeType, mainRegion, subRegion, seaTradeRegion, take);
        return rows.Select(ToAdvancedResult).ToList();
    }

    public async Task<IReadOnlyList<TradingRecommendation>> GetAdvancedRoutesAsync(
        string? item,
        string? type,
        IReadOnlyList<string> buyRegions,
        IReadOnlyList<string> sellRegions,
        int minProfit,
        int routesPerItem,
        int take)
    {
        var rows = await LatestRowsAsync(item, type, null, null, null, null, 20000);
        var buyRegionSet = ToRegionSet(buyRegions);
        var sellRegionSet = ToRegionSet(sellRegions);
        var min = Math.Max(1, minProfit);
        var perItem = Math.Clamp(routesPerItem, 1, 100);
        var limit = Math.Clamp(take, 1, 500);

        var results = new List<TradingRecommendation>();

        foreach (var itemGroup in rows.GroupBy(x => x.ItemName))
        {
            var buys = itemGroup
                .Where(x => x.TradeType == "Buy")
                .Where(x => CityInAnyRegion(x.City, buyRegionSet))
                .OrderBy(x => x.Price)
                .ToList();

            var sells = itemGroup
                .Where(x => x.TradeType == "Sell")
                .Where(x => CityInAnyRegion(x.City, sellRegionSet))
                .OrderByDescending(x => x.Price)
                .ToList();

            var routes = new List<TradingRecommendation>();

            foreach (var buy in buys)
            foreach (var sell in sells)
            {
                if (string.Equals(buy.City, sell.City, StringComparison.OrdinalIgnoreCase))
                    continue;

                var buyPrice = PriceToInt(buy.Price);
                var sellPrice = PriceToInt(sell.Price);
                var profit = sellPrice - buyPrice;

                if (profit < min)
                    continue;

                routes.Add(new TradingRecommendation(
                    buy.ItemName,
                    string.IsNullOrWhiteSpace(buy.TradeGoodType) ? sell.TradeGoodType : buy.TradeGoodType,
                    buy.City,
                    buyPrice,
                    sell.City,
                    sellPrice,
                    profit,
                    buy.Multiplier,
                    sell.Multiplier));
            }

            results.AddRange(routes.OrderByDescending(x => x.Profit).Take(perItem));
        }

        return results
            .OrderByDescending(x => x.Profit)
            .ThenBy(x => x.ItemName)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<MultiGoodRouteResult>> GetMultiGoodRoutesAsync(
        string? type,
        IReadOnlyList<string> buyRegions,
        IReadOnlyList<string> sellRegions,
        int minProfitPerGood,
        int minTotalProfit,
        int minItems,
        int take)
    {
        var rows = await LatestRowsAsync(null, type, null, null, null, null, 20000);
        var buyRegionSet = ToRegionSet(buyRegions);
        var sellRegionSet = ToRegionSet(sellRegions);
        var perGoodMin = Math.Max(1, minProfitPerGood);
        var totalMin = Math.Max(1, minTotalProfit);
        var itemMinimum = Math.Clamp(minItems, 2, 50);
        var limit = Math.Clamp(take, 1, 200);

        var buyRows = rows
            .Where(x => x.TradeType == "Buy" && CityInAnyRegion(x.City, buyRegionSet))
            .ToList();

        var sellRows = rows
            .Where(x => x.TradeType == "Sell" && CityInAnyRegion(x.City, sellRegionSet))
            .ToList();

        var results = new List<MultiGoodRouteResult>();

        foreach (var buyCityGroup in buyRows.GroupBy(x => x.City))
        foreach (var sellCityGroup in sellRows.GroupBy(x => x.City))
        {
            if (string.Equals(buyCityGroup.Key, sellCityGroup.Key, StringComparison.OrdinalIgnoreCase))
                continue;

            var sellByItem = sellCityGroup
                .GroupBy(x => x.ItemName)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Price).First(), StringComparer.OrdinalIgnoreCase);

            var items = new List<MultiGoodRouteItem>();

            foreach (var buy in buyCityGroup.GroupBy(x => x.ItemName).Select(g => g.OrderBy(x => x.Price).First()))
            {
                if (!sellByItem.TryGetValue(buy.ItemName, out var sell))
                    continue;

                var buyPrice = PriceToInt(buy.Price);
                var sellPrice = PriceToInt(sell.Price);
                var profit = sellPrice - buyPrice;

                if (profit < perGoodMin)
                    continue;

                items.Add(new MultiGoodRouteItem(buy.ItemName, buy.TradeGoodType, buyPrice, sellPrice, profit));
            }

            if (items.Count < itemMinimum)
                continue;

            var totalProfit = items.Sum(x => x.Profit);
            if (totalProfit < totalMin)
                continue;

            var buyCity = _cities.FindByName(buyCityGroup.Key);
            var sellCity = _cities.FindByName(sellCityGroup.Key);

            results.Add(new MultiGoodRouteResult(
                buyCityGroup.Key,
                sellCityGroup.Key,
                buyCity?.MainRegion ?? string.Empty,
                buyCity?.SubRegion ?? string.Empty,
                buyCity?.SeaTradeRegion ?? string.Empty,
                sellCity?.MainRegion ?? string.Empty,
                sellCity?.SubRegion ?? string.Empty,
                sellCity?.SeaTradeRegion ?? string.Empty,
                totalProfit,
                items.Count,
                items.OrderByDescending(x => x.Profit).ToList()));
        }

        return results
            .OrderByDescending(x => x.TotalProfit)
            .ThenByDescending(x => x.ItemCount)
            .Take(limit)
            .ToList();
    }

    private async Task<List<PriceCapture>> LatestRowsAsync(string? item, string? type, string? tradeType, string? mainRegion, string? subRegion, string? seaTradeRegion, int take)
    {
        var query = _db.PriceCaptures.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(item))
            query = query.Where(x => x.ItemName.Contains(item));

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(x => x.TradeGoodType.Contains(type));

        if (!string.IsNullOrWhiteSpace(tradeType) && tradeType != "Any")
            query = query.Where(x => x.TradeType == tradeType);

        var rows = await query
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(Math.Clamp(take * 30, 100, 30000))
            .ToListAsync();

        rows = rows
            .Where(x => CityMatches(x.City, mainRegion, subRegion, seaTradeRegion))
            .ToList();

        return rows
            .GroupBy(x => new { x.City, x.ItemName, x.TradeType })
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .Take(Math.Clamp(take, 1, 30000))
            .ToList();
    }

    private TradingAdvancedSearchResult ToAdvancedResult(PriceCapture row)
    {
        var city = _cities.FindByName(row.City);

        return new TradingAdvancedSearchResult(
            row.City,
            row.ItemName,
            row.TradeGoodType,
            PriceToInt(row.Price),
            NullableDecimalToInt(row.Multiplier),
            row.TradeType,
            row.CapturedAtUtc,
            row.RawText,
            city?.MainRegion ?? string.Empty,
            city?.SubRegion ?? string.Empty,
            city?.SeaTradeRegion ?? string.Empty);
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

    private bool CityInAnyRegion(string cityName, HashSet<string> regions)
    {
        if (regions.Count == 0) return true;

        var city = _cities.FindByName(cityName);
        if (city is null) return false;

        return regions.Contains(city.MainRegion) ||
               regions.Contains(city.SubRegion) ||
               regions.Contains(city.SeaTradeRegion);
    }

    private static HashSet<string> ToRegionSet(IReadOnlyList<string> regions)
    {
        return regions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool RegionMatches(string value, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter) ||
               string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static int PriceToInt(decimal price)
    {
        // Prices are stored as decimal in the model/database, but the game prices are whole numbers.
        // This keeps the frontend/API simple and fixes decimal-to-int compile errors.
        return decimal.ToInt32(decimal.Truncate(price));
    }

    private static int? NullableDecimalToInt(decimal? value)
    {
        return value.HasValue ? decimal.ToInt32(decimal.Truncate(value.Value)) : null;
    }
}
