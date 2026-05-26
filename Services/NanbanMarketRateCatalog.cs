using System.Text;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface INanbanMarketRateCatalog
{
    IReadOnlyList<NanbanMarketRateItem> Search(
        string? sourceMarket,
        string? tradeGood,
        string? category,
        string? sellArea,
        string? marketSignal,
        int? minPrice,
        int? maxPrice,
        int take);
}

public sealed class NanbanMarketRateCatalog : INanbanMarketRateCatalog
{
    private readonly string _path;
    private readonly object _lock = new();
    private IReadOnlyList<NanbanMarketRateItem>? _items;

    public NanbanMarketRateCatalog(IWebHostEnvironment environment)
    {
        _path = Path.Combine(environment.ContentRootPath, "Data", "uwo_nanban_market_rates_japan.csv");
    }

    public IReadOnlyList<NanbanMarketRateItem> Search(
        string? sourceMarket,
        string? tradeGood,
        string? category,
        string? sellArea,
        string? marketSignal,
        int? minPrice,
        int? maxPrice,
        int take)
    {
        var rows = GetAll();
        var limit = Math.Clamp(take, 1, 5000);

        return rows
            .Where(row => Matches(row, sourceMarket, tradeGood, category, sellArea, marketSignal, minPrice, maxPrice))
            .OrderByDescending(row => row.Price)
            .ThenBy(row => row.TradeGood)
            .ThenBy(row => row.SellArea)
            .Take(limit)
            .ToList();
    }

    private IReadOnlyList<NanbanMarketRateItem> GetAll()
    {
        lock (_lock)
        {
            _items ??= Load();
            return _items;
        }
    }

    private IReadOnlyList<NanbanMarketRateItem> Load()
    {
        if (!File.Exists(_path))
            return Array.Empty<NanbanMarketRateItem>();

        using var reader = new StreamReader(
            _path,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        var headerLine = reader.ReadLine();
        if (headerLine is null)
            return Array.Empty<NanbanMarketRateItem>();

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var rows = new List<NanbanMarketRateItem>();

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var values = SplitCsvLine(line);

            rows.Add(new NanbanMarketRateItem(
                Get("SourceMarket"),
                Get("TradeGood"),
                Get("Category"),
                Get("SellArea"),
                GetInt("Price"),
                Get("MarketSignal"),
                Get("SourceUrl")));

            string Get(string header)
            {
                var key = NormalizeHeader(header);
                if (!headers.TryGetValue(key, out var index) || index < 0 || index >= values.Count)
                    return string.Empty;

                return values[index].Trim();
            }

            int GetInt(string header)
                => int.TryParse(Get(header), out var value) ? value : 0;
        }

        return rows;
    }

    private static bool Matches(
        NanbanMarketRateItem row,
        string? sourceMarket,
        string? tradeGood,
        string? category,
        string? sellArea,
        string? marketSignal,
        int? minPrice,
        int? maxPrice)
    {
        return Contains(row.SourceMarket, sourceMarket) &&
               Contains(row.TradeGood, tradeGood) &&
               Contains(row.Category, category) &&
               Contains(row.SellArea, sellArea) &&
               MatchesMarketSignal(row.MarketSignal, marketSignal) &&
               (minPrice is null || row.Price >= minPrice.Value) &&
               (maxPrice is null || row.Price <= maxPrice.Value);
    }

    private static bool Contains(string source, string? query)
        => string.IsNullOrWhiteSpace(query) ||
           source.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesMarketSignal(string source, string? query)
        => string.IsNullOrWhiteSpace(query) ||
           string.Equals(source, query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHeader(string value)
        => value.Trim().TrimStart('\uFEFF').Replace(" ", "").Replace("_", "").ToLowerInvariant();

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i += 1)
        {
            var ch = line[i];

            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i += 1;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        values.Add(builder.ToString());
        return values;
    }
}
