using System.Globalization;
using System.Text;
using OcrTradingBackend.Data;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record PriceCsvImportResult(int Imported, int Skipped, IReadOnlyList<string> Messages);

public static class PriceCsvImportService
{
    public static async Task<PriceCsvImportResult> ImportAsync(AppDbContext db, Stream csvStream, CancellationToken ct = default)
    {
        var imported = 0;
        var skipped = 0;
        var messages = new List<string>();

        using var reader = new StreamReader(csvStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var headerLine = await reader.ReadLineAsync(ct);

        if (string.IsNullOrWhiteSpace(headerLine))
            return new PriceCsvImportResult(0, 0, new[] { "CSV file is empty." });

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = name.Trim(), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

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

                string Get(string name)
                {
                    return headers.TryGetValue(name, out var index) && index < values.Count
                        ? values[index].Trim()
                        : string.Empty;
                }

                var city = Get("City");
                var itemName = Get("ItemName");
                var tradeType = Get("TradeType");
                var priceText = Get("Price");
                var multiplierText = Get("Multiplier");
                var capturedText = Get("CapturedAtUtc");
                var tradeGoodType = Get("TradeGoodType");
                var rawText = Get("RawText");

                if (string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(priceText))
                {
                    skipped++;
                    messages.Add($"Line {lineNumber}: skipped because City, ItemName, or Price is missing.");
                    continue;
                }

                if (!decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                {
                    skipped++;
                    messages.Add($"Line {lineNumber}: skipped because price '{priceText}' is invalid.");
                    continue;
                }

                decimal? multiplier = null;
                if (!string.IsNullOrWhiteSpace(multiplierText) && decimal.TryParse(multiplierText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedMultiplier))
                    multiplier = parsedMultiplier;

                var capturedAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(capturedText) && DateTime.TryParse(capturedText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedDate))
                    capturedAt = parsedDate.ToUniversalTime();

                if (string.IsNullOrWhiteSpace(tradeType))
                    tradeType = "Unknown";

                var exists = db.PriceCaptures.Any(x =>
                    x.City == city &&
                    x.ItemName == itemName &&
                    x.TradeType == tradeType &&
                    x.Price == price &&
                    x.CapturedAtUtc == capturedAt);

                if (exists)
                {
                    skipped++;
                    continue;
                }

                db.PriceCaptures.Add(new PriceCapture
                {
                    City = city,
                    ItemName = itemName,
                    TradeGoodType = tradeGoodType,
                    Price = price,
                    Multiplier = multiplier,
                    TradeType = tradeType,
                    RawText = rawText,
                    CapturedAtUtc = capturedAt
                });

                imported++;
            }
            catch (Exception ex)
            {
                skipped++;
                messages.Add($"Line {lineNumber}: skipped because {ex.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        messages.Insert(0, $"Imported {imported} row(s). Skipped {skipped} row(s).");
        return new PriceCsvImportResult(imported, skipped, messages.Take(25).ToList());
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
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
