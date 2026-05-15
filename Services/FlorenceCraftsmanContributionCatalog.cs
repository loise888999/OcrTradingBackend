using System.Globalization;
using System.Text;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface IFlorenceCraftsmanContributionCatalog
{
    IReadOnlyList<FlorenceCraftsmanContributionItem> Search(
        string? good,
        string? type,
        string? skill,
        string? confidence,
        string? source,
        int take);
}

public sealed class FlorenceCraftsmanContributionCatalog : IFlorenceCraftsmanContributionCatalog
{
    private readonly string _path;
    private readonly object _lock = new();
    private IReadOnlyList<FlorenceCraftsmanContributionItem>? _items;

    public FlorenceCraftsmanContributionCatalog(IWebHostEnvironment environment)
    {
        _path = Path.Combine(environment.ContentRootPath, "Data", "uwo_florence_craftsman_contribution.csv");
    }

    public IReadOnlyList<FlorenceCraftsmanContributionItem> Search(
        string? good,
        string? type,
        string? skill,
        string? confidence,
        string? source,
        int take)
    {
        var rows = GetAll();
        var limit = Math.Clamp(take, 1, 5000);

        return rows
            .Where(row => Matches(row, good, type, skill, confidence, source))
            .OrderByDescending(row => row.ScoreAvg ?? decimal.MinValue)
            .ThenBy(row => row.TradeGood)
            .Take(limit)
            .ToList();
    }

    private IReadOnlyList<FlorenceCraftsmanContributionItem> GetAll()
    {
        lock (_lock)
        {
            _items ??= Load();
            return _items;
        }
    }

    private IReadOnlyList<FlorenceCraftsmanContributionItem> Load()
    {
        if (!File.Exists(_path))
            return Array.Empty<FlorenceCraftsmanContributionItem>();

        using var reader = new StreamReader(
            _path,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        var headerLine = reader.ReadLine();
        if (headerLine is null)
            return Array.Empty<FlorenceCraftsmanContributionItem>();

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var rows = new List<FlorenceCraftsmanContributionItem>();

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var values = SplitCsvLine(line);

            AddSkillRows("Cooking", "cooking_min", "cooking_max");
            AddSkillRows("Sewing", "sewing_min", "sewing_max");
            AddSkillRows("Casting", "casting_min", "casting_max");
            AddSkillRows("Storage", "storage_min", "storage_max");
            AddSkillRows("Handicrafts", "handicrafts_min", "handicrafts_max");

            void AddSkillRows(string skill, string minHeader, string maxHeader)
            {
                var min = GetDecimal(minHeader);
                var max = GetDecimal(maxHeader);
                if (min is null && max is null)
                    return;

                min ??= max;
                max ??= min;
                decimal? avg = min is not null && max is not null
                    ? (min.Value + max.Value) / 2m
                    : null;

                rows.Add(new FlorenceCraftsmanContributionItem(
                    $"{Get("record_id")}-{skill}",
                    Get("po_category"),
                    Get("trade_good_type"),
                    Get("trade_good"),
                    skill,
                    min,
                    max,
                    avg,
                    Get("uncertain"),
                    Get("confidence"),
                    $"{Get("trade_good")} ({skill} {FormatScore(min)}-{FormatScore(max)})",
                    Get("notes"),
                    Get("source_table"),
                    Get("source_url"),
                    Get("google_sheet_url"),
                    Get("app_note")));
            }

            string Get(string header)
            {
                var key = NormalizeHeader(header);
                if (!headers.TryGetValue(key, out var index) || index < 0 || index >= values.Count)
                    return string.Empty;

                return values[index].Trim();
            }

            decimal? GetDecimal(string header)
            {
                var text = Get(header);
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : null;
            }
        }

        return rows;
    }

    private static string FormatScore(decimal? value)
        => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";

    private static bool Matches(
        FlorenceCraftsmanContributionItem row,
        string? good,
        string? type,
        string? skill,
        string? confidence,
        string? source)
    {
        return Contains($"{row.TradeGood} {row.DisplayLabel}", good) &&
               Contains($"{row.TradeGoodType} {row.PoCategory}", type) &&
               Contains(row.ContributionSkill, skill) &&
               Contains($"{row.Confidence} {row.Uncertain}", confidence) &&
               Contains($"{row.SourceTable} {row.SourceUrl} {row.GoogleSheetUrl}", source);
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
