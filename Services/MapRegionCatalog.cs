using System.Text.Json;

namespace OcrTradingBackend.Services;

public sealed record MapRegionPoint(double X, double Y);

public sealed record MapRegionDefinition(
    string Id,
    string Name,
    string Type,
    string? ParentRegion,
    string Color,
    IReadOnlyList<MapRegionPoint> Points,
    bool Enabled = true);

public sealed record SaveMapRegionRequest(
    string? Id,
    string Name,
    string Type,
    string? ParentRegion,
    string? Color,
    IReadOnlyList<MapRegionPoint>? Points,
    bool Enabled = true);

public sealed record SaveMapRegionResult(
    bool Success,
    string Message,
    MapRegionDefinition? Region = null);

public interface IMapRegionCatalog
{
    IReadOnlyList<MapRegionDefinition> GetAll();
    MapRegionDefinition? GetById(string id);
    SaveMapRegionResult Upsert(SaveMapRegionRequest request, string? forcedId = null);
    SaveMapRegionResult Delete(string id);
}

public sealed class MapRegionCatalog : IMapRegionCatalog
{
    private readonly object _gate = new();
    private readonly string _path;
    private List<MapRegionDefinition> _regions;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public MapRegionCatalog(IWebHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, "Data", "map-regions.json");
        _regions = Load(_path).OrderBy(x => x.Name).ToList();
    }

    public IReadOnlyList<MapRegionDefinition> GetAll()
    {
        lock (_gate)
            return _regions.ToList();
    }

    public MapRegionDefinition? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        lock (_gate)
            return _regions.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public SaveMapRegionResult Upsert(SaveMapRegionRequest request, string? forcedId = null)
    {
        var name = (request.Name ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name))
            return new SaveMapRegionResult(false, "Region name is required.");

        var id = !string.IsNullOrWhiteSpace(forcedId)
            ? Slug(forcedId)
            : !string.IsNullOrWhiteSpace(request.Id)
                ? Slug(request.Id)
                : Slug(name);

        var points = request.Points?
            .Where(p => NumberValid(p.X) && NumberValid(p.Y))
            .ToList() ?? new List<MapRegionPoint>();

        var region = new MapRegionDefinition(
            id,
            name,
            string.IsNullOrWhiteSpace(request.Type) ? "Custom" : request.Type.Trim(),
            string.IsNullOrWhiteSpace(request.ParentRegion) ? null : request.ParentRegion.Trim(),
            string.IsNullOrWhiteSpace(request.Color) ? "#60a5fa" : request.Color.Trim(),
            points,
            request.Enabled);

        lock (_gate)
        {
            var index = _regions.FindIndex(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
                _regions[index] = region;
            else
                _regions.Add(region);

            _regions = _regions.OrderBy(x => x.Name).ToList();
            Save();

            return new SaveMapRegionResult(true, index >= 0 ? $"Updated region '{name}'." : $"Added region '{name}'.", region);
        }
    }

    public SaveMapRegionResult Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new SaveMapRegionResult(false, "Region id is required.");

        lock (_gate)
        {
            var removed = _regions.RemoveAll(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

            if (removed <= 0)
                return new SaveMapRegionResult(false, $"Region '{id}' was not found.");

            Save();
            return new SaveMapRegionResult(true, $"Deleted region '{id}'.");
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_regions, JsonOptions));
    }

    private static List<MapRegionDefinition> Load(string path)
    {
        if (!File.Exists(path))
            return new List<MapRegionDefinition>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<MapRegionDefinition>>(json, JsonOptions)
                ?? new List<MapRegionDefinition>();
        }
        catch
        {
            return new List<MapRegionDefinition>();
        }
    }

    private static bool NumberValid(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string Slug(string value)
    {
        var chars = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var text = new string(chars);

        while (text.Contains("--"))
            text = text.Replace("--", "-");

        text = text.Trim('-');

        return string.IsNullOrWhiteSpace(text)
            ? Guid.NewGuid().ToString("N")
            : text;
    }
}
