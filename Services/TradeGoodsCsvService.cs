using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record TradeGoodsCsvImportResult(
    int Imported,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Messages);

public static class TradeGoodsCsvService
{
    public static string Export(IReadOnlyList<TradeGoodDefinition> goods)
    {
        var writer = new StringWriter();
        writer.WriteLine("Name,Type,Aliases");

        foreach (var good in goods.OrderBy(x => x.Name))
        {
            writer.WriteLine(
                $"{Csv(good.Name)},{Csv(good.Type)},{Csv(string.Join('|', good.Aliases))}");
        }

        return writer.ToString();
    }

    public static async Task<TradeGoodsCsvImportResult> ImportAsync(
        ITradeGoodCatalog catalog,
        Stream stream,
        CancellationToken ct = default)
    {
        var imported = 0;
        var skipped = 0;
        var failed = 0;
        var messages = new List<string>();

        using var reader = new StreamReader(stream);

        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null)
        {
            return new TradeGoodsCsvImportResult(
                0,
                0,
                1,
                new[] { "CSV file is empty." });
        }

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var nameIndex = Header(headers, "name", 0);
        var typeIndex = Header(headers, "type", 1);
        var aliasesIndex = Header(headers, "aliases", 2);

        string? line;
        var lineNumber = 1;

        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var values = SplitCsvLine(line);

                var name = Value(values, nameIndex).Trim();
                var type = Value(values, typeIndex).Trim();
                var aliases = Value(values, aliasesIndex)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (string.IsNullOrWhiteSpace(name))
                {
                    failed++;
                    messages.Add($"Line {lineNumber}: missing trade good name.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(type))
                    type = "Unknown";

                var result = catalog.AddTradeGood(
                    new AddTradeGoodRequest(
                        name,
                        type,
                        aliases,
                        false));

                if (result.Added)
                {
                    imported++;
                    messages.Add($"Imported: {name}");
                }
                else
                {
                    skipped++;
                    messages.Add($"Skipped: {name} - {result.Message}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Line {lineNumber}: {ex.Message}");
            }
        }

        return new TradeGoodsCsvImportResult(imported, skipped, failed, messages);
    }

    private static int Header(Dictionary<string, int> headers, string name, int fallback)
    {
        return headers.TryGetValue(name, out var index) ? index : fallback;
    }

    private static string Value(IReadOnlyList<string> values, int index)
    {
        return index >= 0 && index < values.Count ? values[index] : string.Empty;
    }

    private static string NormalizeHeader(string value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();
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