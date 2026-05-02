namespace OcrTradingBackend.Services;

public sealed record CityDefinition(
    string Name,
    IReadOnlyList<string> Aliases,
    string MainRegion,
    string SubRegion,
    string SeaTradeRegion);

public interface ICityCatalog
{
    IReadOnlyList<CityDefinition> GetAll();
    CityDefinition? FindByName(string name);
    IReadOnlyList<string> GetMainRegions();
    IReadOnlyList<string> GetSubRegions(string? mainRegion = null);
    IReadOnlyList<string> GetSeaTradeRegions(string? mainRegion = null, string? subRegion = null);
}

public sealed class CityCatalog : ICityCatalog
{
    private readonly List<CityDefinition> _cities;
    private readonly Dictionary<string, CityDefinition> _lookup;

    public CityCatalog(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "Data", "cities.csv");
        _cities = Load(path).OrderBy(x => x.Name).ToList();

        _lookup = new Dictionary<string, CityDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var city in _cities)
        {
            AddLookup(city.Name, city);
            foreach (var alias in city.Aliases)
                AddLookup(alias, city);
        }
    }

    public IReadOnlyList<CityDefinition> GetAll() => _cities;

    public CityDefinition? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = Normalize(name);
        return _lookup.TryGetValue(normalized, out var city) ? city : null;
    }

    public IReadOnlyList<string> GetMainRegions()
    {
        return _cities
            .Select(x => x.MainRegion)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    public IReadOnlyList<string> GetSubRegions(string? mainRegion = null)
    {
        return _cities
            .Where(x => RegionMatches(x.MainRegion, mainRegion))
            .Select(x => x.SubRegion)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    public IReadOnlyList<string> GetSeaTradeRegions(string? mainRegion = null, string? subRegion = null)
    {
        return _cities
            .Where(x => RegionMatches(x.MainRegion, mainRegion))
            .Where(x => RegionMatches(x.SubRegion, subRegion))
            .Select(x => x.SeaTradeRegion)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    private void AddLookup(string key, CityDefinition city)
    {
        key = Normalize(key);
        if (!string.IsNullOrWhiteSpace(key))
            _lookup[key] = city;
    }

    private static bool RegionMatches(string value, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter) ||
               string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<CityDefinition> Load(string path)
    {
        if (!File.Exists(path)) yield break;

        using var reader = new StreamReader(path);
        var headerLine = reader.ReadLine();
        if (headerLine is null) yield break;

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var nameIndex = GetHeader(headers, "name", 0);
        var aliasIndex = GetHeader(headers, "aliases", 1);
        var mainRegionIndex = GetHeader(headers, "mainregion", -1);
        var subRegionIndex = GetHeader(headers, "subregion", -1);
        var seaTradeRegionIndex = GetHeader(headers, "seatraderegion", -1);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = SplitCsvLine(line);
            var name = GetValue(values, nameIndex).Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var aliases = GetValue(values, aliasIndex)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var mainRegion = GetValue(values, mainRegionIndex).Trim();
            var subRegion = GetValue(values, subRegionIndex).Trim();
            var seaTradeRegion = GetValue(values, seaTradeRegionIndex).Trim();

            yield return new CityDefinition(
                name,
                aliases,
                string.IsNullOrWhiteSpace(mainRegion) ? "Unassigned" : mainRegion,
                string.IsNullOrWhiteSpace(subRegion) ? "Unassigned" : subRegion,
                string.IsNullOrWhiteSpace(seaTradeRegion) ? "Unassigned" : seaTradeRegion);
        }
    }

    private static int GetHeader(Dictionary<string, int> headers, string name, int fallback)
    {
        return headers.TryGetValue(name, out var index) ? index : fallback;
    }

    private static string GetValue(IReadOnlyList<string> values, int index)
    {
        return index >= 0 && index < values.Count ? values[index] : string.Empty;
    }

    private static string NormalizeHeader(string value)
    {
        return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static string Normalize(string value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        result.Add(current.ToString());
        return result;
    }
}
