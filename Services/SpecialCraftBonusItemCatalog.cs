using System.Text;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface ISpecialCraftBonusItemCatalog
{
    IReadOnlyList<SpecialCraftBonusItem> Search(
        string? item,
        string? type,
        string? bonus,
        string? material,
        string? location,
        int take);
}

public sealed class SpecialCraftBonusItemCatalog : ISpecialCraftBonusItemCatalog
{
    private readonly string _path;
    private readonly object _lock = new();
    private IReadOnlyList<SpecialCraftBonusItem>? _items;

    public SpecialCraftBonusItemCatalog(IWebHostEnvironment environment)
    {
        _path = Path.Combine(environment.ContentRootPath, "Data", "uwo_special_craft_bonus_items.csv");
    }

    public IReadOnlyList<SpecialCraftBonusItem> Search(
        string? item,
        string? type,
        string? bonus,
        string? material,
        string? location,
        int take)
    {
        var rows = GetAll();
        var limit = Math.Clamp(take, 1, 5000);

        return rows
            .Where(row => Matches(row, item, type, bonus, material, location))
            .Take(limit)
            .ToList();
    }

    private IReadOnlyList<SpecialCraftBonusItem> GetAll()
    {
        lock (_lock)
        {
            _items ??= Load();
            return _items;
        }
    }

    private IReadOnlyList<SpecialCraftBonusItem> Load()
    {
        if (!File.Exists(_path))
            return Array.Empty<SpecialCraftBonusItem>();

        using var reader = new StreamReader(
            _path,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        var headerLine = reader.ReadLine();
        if (headerLine is null)
            return Array.Empty<SpecialCraftBonusItem>();

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var rows = new List<SpecialCraftBonusItem>();

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var values = SplitCsvLine(line);

            rows.Add(new SpecialCraftBonusItem(
                Get("ItemName"),
                Get("ItemName_Japanese"),
                Get("ItemType"),
                Get("Bonus_Stats"),
                Get("Craft_Location"),
                Get("NPC_or_Facility"),
                Get("Materials"),
                Get("Skill_Rank"),
                Get("Contribution_Cost"),
                Get("Unlock_Conditions"),
                Get("Tradable_Bound"),
                Get("Notes"),
                Get("Data_Status"),
                Get("Source_URLs")));

            string Get(string header)
            {
                var key = NormalizeHeader(header);
                if (!headers.TryGetValue(key, out var index) || index < 0 || index >= values.Count)
                    return string.Empty;

                return values[index].Trim();
            }
        }

        return rows;
    }

    private static bool Matches(
        SpecialCraftBonusItem row,
        string? item,
        string? type,
        string? bonus,
        string? material,
        string? location)
    {
        return Contains(row.ItemName, item) &&
               Contains(row.ItemType, type) &&
               Contains(row.BonusStats, bonus) &&
               Contains(row.Materials, material) &&
               Contains($"{row.CraftLocation} {row.NpcOrFacility}", location);
    }

    private static bool Contains(string source, string? query)
        => string.IsNullOrWhiteSpace(query) ||
           source.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

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
