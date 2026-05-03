namespace OcrTradingBackend.Services;

public sealed record CityDefinition(
    string Name,
    IReadOnlyList<string> Aliases,
    string MainRegion,
    string SubRegion,
    string SeaTradeRegion,
    int? MapPixelX = null,
    int? MapPixelY = null,
    int? WorldX = null,
    int? WorldY = null);

public sealed record SaveCityRequest(
    string Name,
    IReadOnlyList<string>? Aliases,
    string MainRegion,
    string SubRegion,
    string SeaTradeRegion,
    int? MapPixelX,
    int? MapPixelY,
    int? WorldX,
    int? WorldY);

public sealed record SaveCityResult(
    bool Success,
    string Message,
    CityDefinition? City = null);

public sealed record CityCsvImportResult(
    int Imported,
    int Updated,
    int Failed,
    IReadOnlyList<string> Messages);

public interface ICityCatalog
{
    IReadOnlyList<CityDefinition> GetAll();
    CityDefinition? FindByName(string name);
    IReadOnlyList<string> GetMainRegions();
    IReadOnlyList<string> GetSubRegions(string? mainRegion = null);
    IReadOnlyList<string> GetSeaTradeRegions(string? mainRegion = null, string? subRegion = null);

    SaveCityResult AddCity(SaveCityRequest request);
    SaveCityResult UpdateCity(string name, SaveCityRequest request);
    SaveCityResult DeleteCity(string name);

    string ExportCsv();
    Task<CityCsvImportResult> ImportCsvAsync(Stream stream, CancellationToken ct = default);
}

public sealed class CityCatalog : ICityCatalog
{
    private readonly object _gate = new();
    private readonly string _path;
    private List<CityDefinition> _cities;
    private Dictionary<string, CityDefinition> _lookup;

    public CityCatalog(IWebHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, "Data", "cities.csv");
        _cities = Load(_path).OrderBy(x => x.Name).ToList();
        _lookup = BuildLookup(_cities);
    }

    public IReadOnlyList<CityDefinition> GetAll()
    {
        lock (_gate)
            return _cities.ToList();
    }

    public CityDefinition? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = Normalize(name);

        lock (_gate)
            return _lookup.TryGetValue(normalized, out var city) ? city : null;
    }

    public IReadOnlyList<string> GetMainRegions()
    {
        lock (_gate)
        {
            return _cities
                .Select(x => x.MainRegion)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();
        }
    }

    public IReadOnlyList<string> GetSubRegions(string? mainRegion = null)
    {
        lock (_gate)
        {
            return _cities
                .Where(x => RegionMatches(x.MainRegion, mainRegion))
                .Select(x => x.SubRegion)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();
        }
    }

    public IReadOnlyList<string> GetSeaTradeRegions(string? mainRegion = null, string? subRegion = null)
    {
        lock (_gate)
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
    }

    public SaveCityResult AddCity(SaveCityRequest request)
    {
        var city = NormalizeRequest(request);

        if (string.IsNullOrWhiteSpace(city.Name))
            return new SaveCityResult(false, "City name is required.");

        lock (_gate)
        {
            if (_lookup.ContainsKey(Normalize(city.Name)))
                return new SaveCityResult(false, $"City '{city.Name}' already exists.");

            _cities.Add(city);
            RebuildAndSave();

            return new SaveCityResult(true, $"Added city '{city.Name}'.", city);
        }
    }

    public SaveCityResult UpdateCity(string name, SaveCityRequest request)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new SaveCityResult(false, "City name is required.");

        var city = NormalizeRequest(request);

        if (string.IsNullOrWhiteSpace(city.Name))
            return new SaveCityResult(false, "City name is required.");

        lock (_gate)
        {
            var index = _cities.FindIndex(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                return new SaveCityResult(false, $"City '{name}' was not found.");

            var newKey = Normalize(city.Name);
            var oldKey = Normalize(name);

            if (!string.Equals(newKey, oldKey, StringComparison.OrdinalIgnoreCase) &&
                _lookup.ContainsKey(newKey))
                return new SaveCityResult(false, $"City '{city.Name}' already exists.");

            _cities[index] = city;
            RebuildAndSave();

            return new SaveCityResult(true, $"Updated city '{city.Name}'.", city);
        }
    }

    public SaveCityResult DeleteCity(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new SaveCityResult(false, "City name is required.");

        lock (_gate)
        {
            var removed = _cities.RemoveAll(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (removed <= 0)
                return new SaveCityResult(false, $"City '{name}' was not found.");

            RebuildAndSave();

            return new SaveCityResult(true, $"Deleted city '{name}'.");
        }
    }

    public string ExportCsv()
    {
        lock (_gate)
            return ToCsv(_cities);
    }

    public async Task<CityCsvImportResult> ImportCsvAsync(Stream stream, CancellationToken ct = default)
    {
        var imported = 0;
        var updated = 0;
        var failed = 0;
        var messages = new List<string>();

        using var reader = new StreamReader(stream);

        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null)
        {
            return new CityCsvImportResult(0, 0, 1, new[] { "CSV file is empty." });
        }

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var nameIndex = GetHeader(headers, "name", 0);
        var aliasIndex = GetHeader(headers, "aliases", 1);
        var mainRegionIndex = GetHeader(headers, "mainregion", -1);
        var subRegionIndex = GetHeader(headers, "subregion", -1);
        var seaTradeRegionIndex = GetHeader(headers, "seatraderegion", -1);
        var mapPixelXIndex = GetHeader(headers, "mappixelx", -1);
        var mapPixelYIndex = GetHeader(headers, "mappixely", -1);
        var worldXIndex = GetHeader(headers, "worldx", -1);
        var worldYIndex = GetHeader(headers, "worldy", -1);

        string? line;
        var lineNumber = 1;

        lock (_gate)
        {
            // The lock is intentionally kept while importing because this service writes one shared CSV file.
            // The import is small and local.
        }

        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var values = SplitCsvLine(line);
                var name = GetValue(values, nameIndex).Trim();

                if (string.IsNullOrWhiteSpace(name))
                {
                    failed++;
                    messages.Add($"Line {lineNumber}: missing city name.");
                    continue;
                }

                var aliases = GetValue(values, aliasIndex)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var city = new CityDefinition(
                    name,
                    aliases,
                    Fallback(GetValue(values, mainRegionIndex), "Unassigned"),
                    Fallback(GetValue(values, subRegionIndex), "Unassigned"),
                    Fallback(GetValue(values, seaTradeRegionIndex), "Unassigned"),
                    ParseNullableInt(GetValue(values, mapPixelXIndex)),
                    ParseNullableInt(GetValue(values, mapPixelYIndex)),
                    ParseNullableInt(GetValue(values, worldXIndex)),
                    ParseNullableInt(GetValue(values, worldYIndex)));

                lock (_gate)
                {
                    var existingIndex = _cities.FindIndex(x =>
                        string.Equals(x.Name, city.Name, StringComparison.OrdinalIgnoreCase));

                    if (existingIndex >= 0)
                    {
                        _cities[existingIndex] = city;
                        updated++;
                        messages.Add($"Updated: {city.Name}");
                    }
                    else
                    {
                        _cities.Add(city);
                        imported++;
                        messages.Add($"Imported: {city.Name}");
                    }

                    RebuildAndSave();
                }
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Line {lineNumber}: {ex.Message}");
            }
        }

        return new CityCsvImportResult(imported, updated, failed, messages);
    }

    private void RebuildAndSave()
    {
        _cities = _cities.OrderBy(x => x.Name).ToList();
        _lookup = BuildLookup(_cities);
        Save(_path, _cities);
    }

    private static CityDefinition NormalizeRequest(SaveCityRequest request)
    {
        var name = (request.Name ?? string.Empty).Trim();

        var aliases = request.Aliases?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        var mapPixelX = request.MapPixelX;
        var mapPixelY = request.MapPixelY;
        var worldX = request.WorldX ?? (mapPixelX.HasValue ? mapPixelX.Value * 4 : null);
        var worldY = request.WorldY ?? (mapPixelY.HasValue ? mapPixelY.Value * 4 : null);

        return new CityDefinition(
            name,
            aliases,
            Fallback(request.MainRegion, "Unassigned"),
            Fallback(request.SubRegion, "Unassigned"),
            Fallback(request.SeaTradeRegion, "Unassigned"),
            mapPixelX,
            mapPixelY,
            worldX,
            worldY);
    }

    private static Dictionary<string, CityDefinition> BuildLookup(IEnumerable<CityDefinition> cities)
    {
        var lookup = new Dictionary<string, CityDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var city in cities)
        {
            AddLookup(city.Name, city);

            foreach (var alias in city.Aliases)
                AddLookup(alias, city);
        }

        return lookup;

        void AddLookup(string key, CityDefinition city)
        {
            key = Normalize(key);

            if (!string.IsNullOrWhiteSpace(key))
                lookup[key] = city;
        }
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
        var mapPixelXIndex = GetHeader(headers, "mappixelx", -1);
        var mapPixelYIndex = GetHeader(headers, "mappixely", -1);
        var worldXIndex = GetHeader(headers, "worldx", -1);
        var worldYIndex = GetHeader(headers, "worldy", -1);

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

            yield return new CityDefinition(
                name,
                aliases,
                Fallback(GetValue(values, mainRegionIndex), "Unassigned"),
                Fallback(GetValue(values, subRegionIndex), "Unassigned"),
                Fallback(GetValue(values, seaTradeRegionIndex), "Unassigned"),
                ParseNullableInt(GetValue(values, mapPixelXIndex)),
                ParseNullableInt(GetValue(values, mapPixelYIndex)),
                ParseNullableInt(GetValue(values, worldXIndex)),
                ParseNullableInt(GetValue(values, worldYIndex)));
        }
    }

    private static void Save(string path, IEnumerable<CityDefinition> cities)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToCsv(cities));
    }

    private static string ToCsv(IEnumerable<CityDefinition> cities)
    {
        using var writer = new StringWriter();
        writer.WriteLine("Name,Aliases,MainRegion,SubRegion,SeaTradeRegion,MapPixelX,MapPixelY,WorldX,WorldY");

        foreach (var city in cities.OrderBy(x => x.Name))
        {
            writer.WriteLine(string.Join(",", new[]
            {
                Csv(city.Name),
                Csv(string.Join('|', city.Aliases)),
                Csv(city.MainRegion),
                Csv(city.SubRegion),
                Csv(city.SeaTradeRegion),
                Csv(city.MapPixelX?.ToString() ?? string.Empty),
                Csv(city.MapPixelY?.ToString() ?? string.Empty),
                Csv(city.WorldX?.ToString() ?? string.Empty),
                Csv(city.WorldY?.ToString() ?? string.Empty)
            }));
        }

        return writer.ToString();
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

    private static string Fallback(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int? ParseNullableInt(string value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string Csv(string value)
    {
        return $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
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
