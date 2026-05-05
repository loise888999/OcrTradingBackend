using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record TradeGoodDefinition(string Name, string Type, IReadOnlyList<string> Aliases);
public sealed record AddTradeGoodResult(bool Added, string Message, TradeGoodDefinition? TradeGood = null);

public interface ITradeGoodCatalog
{
    IReadOnlyList<TradeGoodDefinition> GetAll();
    TradeGoodDefinition? FindByName(string name);
    IReadOnlyList<TradeGoodSuggestion> SuggestSimilar(string name, int take = 8);
    AddTradeGoodResult AddTradeGood(AddTradeGoodRequest request);
}

public sealed class TradeGoodCatalog : ITradeGoodCatalog
{
    private readonly object _gate = new();
    private readonly string _path;
    private List<TradeGoodDefinition> _goods;
    private Dictionary<string, TradeGoodDefinition> _lookup;

    public TradeGoodCatalog(IWebHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, "Data", "trade-goods.csv");
        _goods = Load(_path).OrderBy(x => x.Name).ToList();
        _lookup = BuildLookup(_goods);
    }

    public IReadOnlyList<TradeGoodDefinition> GetAll()
    {
        lock (_gate)
            return _goods.ToList();
    }

    public TradeGoodDefinition? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Normalize(name);

        lock (_gate)
        {
            if (_lookup.TryGetValue(key, out var exact))
                return exact;

            return _goods
                .Select(g => new { Good = g, Score = SimilarityScore(key, Normalize(g.Name)) })
                .Where(x => x.Score >= 0.82)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Good)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<TradeGoodSuggestion> SuggestSimilar(string name, int take = 8)
    {
        var key = Normalize(name);
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<TradeGoodSuggestion>();

        lock (_gate)
        {
            var suggestions = new List<TradeGoodSuggestion>();

            foreach (var candidate in _goods
                .Select(g => new { Good = g, Score = SimilarityScore(key, Normalize(g.Name)) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Good.Name)
                .Take(Math.Clamp(take, 1, 50)))
            {
                var suggestion = CreateTradeGoodSuggestion(candidate.Good, candidate.Score);
                if (suggestion is not null)
                    suggestions.Add(suggestion);
            }

            return suggestions;
        }
    }

    public AddTradeGoodResult AddTradeGood(AddTradeGoodRequest request)
    {
        var name = (request.Name ?? string.Empty).Trim();
        var type = (request.Type ?? string.Empty).Trim();
        var aliases = request.Aliases?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (string.IsNullOrWhiteSpace(name))
            return new AddTradeGoodResult(false, "Trade good name is required.");

        if (string.IsNullOrWhiteSpace(type))
            type = "Unknown";

        lock (_gate)
        {
            if (ExactTradeGoodKeyExistsLocked(name))
            {
                return new AddTradeGoodResult(
                    false,
                    $"Trade good '{name}' already exists.");
            }

            foreach (var alias in aliases)
            {
                if (ExactTradeGoodKeyExistsLocked(alias))
                {
                    return new AddTradeGoodResult(
                        false,
                        $"Trade good alias '{alias}' already exists as a trade good name or alias.");
                }
            }

            var good = new TradeGoodDefinition(name, type, aliases);

            _goods.Add(good);
            _goods = _goods.OrderBy(x => x.Name).ToList();
            _lookup = BuildLookup(_goods);

            Save(_path, _goods);

            return new AddTradeGoodResult(
                true,
                $"Added trade good '{name}'.",
                good);
        }
    }

    private bool ExactTradeGoodKeyExistsLocked(string value)
    {
        var key = Normalize(value);

        return !string.IsNullOrWhiteSpace(key) &&
               _lookup.ContainsKey(key);
    }

    private static TradeGoodSuggestion? CreateTradeGoodSuggestion(TradeGoodDefinition good, double score)
    {
        // Uses reflection so it can adapt to your existing TradeGoodSuggestion constructor shape.
        var t = typeof(TradeGoodSuggestion);

        foreach (var args in new object?[][]
        {
            new object?[] { good.Name, good.Type, score },
            new object?[] { good.Name, good.Type, score, good.Aliases },
            new object?[] { good.Name, good.Type },
            new object?[] { good.Name, score },
            new object?[] { good.Name }
        })
        {
            try
            {
                if (Activator.CreateInstance(t, args) is TradeGoodSuggestion suggestion)
                    return suggestion;
            }
            catch
            {
                // Try next known constructor shape.
            }
        }

        return null;
    }

    private static Dictionary<string, TradeGoodDefinition> BuildLookup(IEnumerable<TradeGoodDefinition> goods)
    {
        var lookup = new Dictionary<string, TradeGoodDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var good in goods)
        {
            Add(good.Name, good);
            foreach (var alias in good.Aliases)
                Add(alias, good);
        }

        return lookup;

        // IMPORTANT: This cannot be static because it captures the local variable `lookup`.
        void Add(string key, TradeGoodDefinition good)
        {
            key = Normalize(key);
            if (!string.IsNullOrWhiteSpace(key))
                lookup[key] = good;
        }
    }

    private static IEnumerable<TradeGoodDefinition> Load(string path)
    {
        if (!File.Exists(path)) yield break;

        using var reader = new StreamReader(path);
        var headerLine = reader.ReadLine();
        if (headerLine is null) yield break;

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var nameIndex = Header(headers, "name", 0);
        var typeIndex = Header(headers, "type", 1);
        var aliasIndex = Header(headers, "aliases", 2);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = SplitCsvLine(line);
            var name = Value(values, nameIndex).Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var type = Value(values, typeIndex).Trim();
            var aliases = Value(values, aliasIndex)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            yield return new TradeGoodDefinition(name, string.IsNullOrWhiteSpace(type) ? "Unknown" : type, aliases);
        }
    }

    private static void Save(string path, IEnumerable<TradeGoodDefinition> goods)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path);
        writer.WriteLine("Name,Type,Aliases");

        foreach (var good in goods.OrderBy(x => x.Name))
            writer.WriteLine($"{Csv(good.Name)},{Csv(good.Type)},{Csv(string.Join('|', good.Aliases))}");
    }

    private static int Header(Dictionary<string, int> headers, string name, int fallback)
        => headers.TryGetValue(name, out var i) ? i : fallback;

    private static string Value(IReadOnlyList<string> values, int index)
        => index >= 0 && index < values.Count ? values[index] : string.Empty;

    private static string NormalizeHeader(string value)
        => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Normalize(string value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

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

    private static double SimilarityScore(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (b.Contains(a) || a.Contains(b)) return 0.9;
        var distance = Levenshtein(a, b);
        return 1.0 - ((double)distance / Math.Max(a.Length, b.Length));
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }

        return dp[a.Length, b.Length];
    }
}
