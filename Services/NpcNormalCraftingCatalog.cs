using System.Text;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface INpcNormalCraftingCatalog
{
    IReadOnlyList<NpcNormalCraftingItem> Search(
        string? product,
        string? category,
        string? npc,
        string? skill,
        string? material,
        string? location,
        int take);
}

public sealed class NpcNormalCraftingCatalog : INpcNormalCraftingCatalog
{
    private readonly string _path;
    private readonly object _lock = new();
    private IReadOnlyList<NpcNormalCraftingItem>? _items;

    public NpcNormalCraftingCatalog(IWebHostEnvironment environment)
    {
        _path = Path.Combine(environment.ContentRootPath, "Data", "uwo_npc_normal_crafting_list_v1.csv");
    }

    public IReadOnlyList<NpcNormalCraftingItem> Search(
        string? product,
        string? category,
        string? npc,
        string? skill,
        string? material,
        string? location,
        int take)
    {
        var rows = GetAll();
        var limit = Math.Clamp(take, 1, 5000);

        return rows
            .Where(row => Matches(row, product, category, npc, skill, material, location))
            .Take(limit)
            .ToList();
    }

    private IReadOnlyList<NpcNormalCraftingItem> GetAll()
    {
        lock (_lock)
        {
            _items ??= Load();
            return _items;
        }
    }

    private IReadOnlyList<NpcNormalCraftingItem> Load()
    {
        if (!File.Exists(_path))
            return Array.Empty<NpcNormalCraftingItem>();

        using var reader = new StreamReader(
            _path,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        var headerLine = reader.ReadLine();
        if (headerLine is null)
            return Array.Empty<NpcNormalCraftingItem>();

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var rows = new List<NpcNormalCraftingItem>();

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var values = SplitCsvLine(line);

            rows.Add(new NpcNormalCraftingItem(
                Get("Category"),
                Get("NPC/Facility"),
                Get("Location(s)"),
                Get("Recipe / Method"),
                Get("Product"),
                Get("Required Skill(s)"),
                Get("Materials"),
                Get("Item Type"),
                Get("Scope"),
                Get("Notes"),
                Get("Data Status"),
                Get("Source URL")));

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
        NpcNormalCraftingItem row,
        string? product,
        string? category,
        string? npc,
        string? skill,
        string? material,
        string? location)
    {
        return Contains(row.Product, product) &&
               Contains(row.Category, category) &&
               Contains(row.NpcOrFacility, npc) &&
               Contains(row.RequiredSkills, skill) &&
               Contains(row.Materials, material) &&
               Contains(row.Locations, location);
    }

    private static bool Contains(string source, string? query)
        => string.IsNullOrWhiteSpace(query) ||
           source.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHeader(string value)
        => value.Trim().TrimStart('\uFEFF').Replace(" ", "").Replace("_", "").Replace("/", "").Replace("(", "").Replace(")", "").ToLowerInvariant();

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
