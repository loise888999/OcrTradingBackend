using OcrTradingBackend.Models;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OcrTradingBackend.Services;

public sealed record TradeGoodDefinition(string Name, string Type, IReadOnlyList<string> Aliases);
public sealed record CityDefinition(string Name, IReadOnlyList<string> Aliases);

public interface ITradeGoodCatalog
{
    TradeGoodDefinition? FindByName(string ocrName);
    IReadOnlyList<TradeGoodDefinition> GetAll();
    IReadOnlyList<TradeGoodSuggestion> SuggestSimilar(string name, int take = 8);
    AddTradeGoodResult AddTradeGood(AddTradeGoodRequest request);
}

public interface ICityCatalog
{
    CityDefinition? FindByName(string ocrName);
    IReadOnlyList<CityDefinition> GetAll();
}

public abstract class CsvCatalogBase
{
    protected static string Normalize(string value)
    {
        value = RemoveDiacritics(value ?? string.Empty).ToLowerInvariant();
        value = Regex.Replace(value, @"[^a-z0-9]+", " ");
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    protected static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    protected static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
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
        return result.ToArray();
    }

    protected static string CsvEscape(string value)
    {
        value ??= string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    protected static int LevenshteinDistance(string a, string b)
    {
        a = Normalize(a);
        b = Normalize(b);

        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var matrix = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) matrix[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) matrix[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[a.Length, b.Length];
    }

    protected static double SimilarityScore(string a, string b)
    {
        var normalizedA = Normalize(a);
        var normalizedB = Normalize(b);
        var max = Math.Max(normalizedA.Length, normalizedB.Length);
        if (max == 0) return 1;
        var distance = LevenshteinDistance(normalizedA, normalizedB);
        return Math.Max(0, 1.0 - (double)distance / max);
    }
}

public sealed class TradeGoodCatalog : CsvCatalogBase, ITradeGoodCatalog
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TradeGoodDefinition> _by = new();
    private readonly List<TradeGoodDefinition> _all = new();
    private readonly string _path;

    public TradeGoodCatalog(IWebHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, "Data", "trade-goods.csv");
        LoadFromFile();
    }

    private void LoadFromFile()
    {
        lock (_lock)
        {
            _by.Clear();
            _all.Clear();

            if (!File.Exists(_path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, "Name,Type,Aliases" + Environment.NewLine, Encoding.UTF8);
            }

            foreach (var line in File.ReadLines(_path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var p = SplitCsvLine(line);
                if (p.Length < 2) continue;

                var name = p[0].Trim();
                var type = p[1].Trim();
                var aliases = p.Length >= 3
                    ? p[2].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : Array.Empty<string>();

                if (string.IsNullOrWhiteSpace(name)) continue;
                AddToMemory(new TradeGoodDefinition(name, type, aliases));
            }

            Console.WriteLine($"Loaded {_all.Count} trade goods.");
        }
    }

    private void AddToMemory(TradeGoodDefinition definition)
    {
        _all.Add(definition);
        _by[Normalize(definition.Name)] = definition;
        foreach (var alias in definition.Aliases)
            _by[Normalize(alias)] = definition;
    }

    public TradeGoodDefinition? FindByName(string ocrName)
    {
        lock (_lock)
        {
            return _by.TryGetValue(Normalize(ocrName), out var d) ? d : null;
        }
    }

    public IReadOnlyList<TradeGoodDefinition> GetAll()
    {
        lock (_lock)
        {
            return _all.OrderBy(x => x.Name).ToList();
        }
    }

    public IReadOnlyList<TradeGoodSuggestion> SuggestSimilar(string name, int take = 8)
    {
        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<TradeGoodSuggestion>();

        lock (_lock)
        {
            return _all
                .Select(g => new TradeGoodSuggestion(
                    g.Name,
                    g.Type,
                    Math.Round(SimilarityScore(name, g.Name), 3),
                    g.Aliases))
                .Where(x => x.Score >= 0.55 || Normalize(x.Name).Contains(Normalize(name)) || Normalize(name).Contains(Normalize(x.Name)))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Name)
                .Take(Math.Clamp(take, 1, 25))
                .ToList();
        }
    }

    public AddTradeGoodResult AddTradeGood(AddTradeGoodRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var type = request.Type?.Trim() ?? string.Empty;
        var aliases = request.Aliases ?? Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(name))
            return new AddTradeGoodResult(false, "Name is required.", null, Array.Empty<TradeGoodSuggestion>());

        if (string.IsNullOrWhiteSpace(type))
            return new AddTradeGoodResult(false, "Type is required.", null, SuggestSimilar(name));

        lock (_lock)
        {
            var normalized = Normalize(name);
            if (_by.TryGetValue(normalized, out var existing))
            {
                return new AddTradeGoodResult(
                    false,
                    $"Trade good already exists as '{existing.Name}'.",
                    existing,
                    SuggestSimilar(name));
            }

            var similar = SuggestSimilar(name);
            if (!request.Force && similar.Any(x => x.Score >= 0.82))
            {
                return new AddTradeGoodResult(
                    false,
                    "A similar trade good already exists. Review the suggestions, then submit again with force=true if you still want to add it.",
                    null,
                    similar);
            }

            var cleanAliases = aliases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var definition = new TradeGoodDefinition(name, type, cleanAliases);
            var line = string.Join(',',
                CsvEscape(definition.Name),
                CsvEscape(definition.Type),
                CsvEscape(string.Join(';', definition.Aliases)));


            EnsureFileEndsWithNewLine(_path);
            File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            AddToMemory(definition);


            return new AddTradeGoodResult(true, $"Trade good '{definition.Name}' was added.", definition, Array.Empty<TradeGoodSuggestion>());
        }
    }

    private static void EnsureFileEndsWithNewLine(string path)
    {
        if (!File.Exists(path))
            return;

        var info = new FileInfo(path);

        if (info.Length == 0)
            return;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Seek(-1, SeekOrigin.End);

        var lastByte = stream.ReadByte();

        if (lastByte != '\n')
        {
            stream.Seek(0, SeekOrigin.End);
            var newlineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);
            stream.Write(newlineBytes, 0, newlineBytes.Length);
        }
    }


}

public sealed class CityCatalog : CsvCatalogBase, ICityCatalog
{
    private readonly Dictionary<string, CityDefinition> _by = new();
    private readonly List<CityDefinition> _all = new();

    public CityCatalog(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "Data", "cities.csv");
        if (!File.Exists(path))
        {
            Console.WriteLine($"Cities file not found: {path}");
            return;
        }

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = SplitCsvLine(line);
            if (p.Length < 1) continue;

            var name = p[0].Trim();
            var aliases = p.Length >= 2
                ? p[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(name)) continue;

            var d = new CityDefinition(name, aliases);
            _all.Add(d);
            _by[Normalize(name)] = d;
            foreach (var a in aliases) _by[Normalize(a)] = d;
        }

        Console.WriteLine($"Loaded {_all.Count} cities.");
    }

    public CityDefinition? FindByName(string ocrName) => _by.TryGetValue(Normalize(ocrName), out var d) ? d : null;
    public IReadOnlyList<CityDefinition> GetAll() => _all.OrderBy(x => x.Name).ToList();
}
