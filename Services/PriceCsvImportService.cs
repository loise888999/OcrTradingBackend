using OcrTradingBackend.Data;
using OcrTradingBackend.Models;
using System.Globalization;
using System.Text;

namespace OcrTradingBackend.Services;

public sealed record PriceCsvImportResult(int Imported, int Skipped, IReadOnlyList<string> Messages);

public static class PriceCsvImportService
{
    public static async Task<PriceCsvImportResult> ImportAsync(AppDbContext db, Stream csvStream, CancellationToken ct = default)
    {
        var imported = 0;
        var skipped = 0;
        var updated = 0;
        var messages = new List<string>();

        using var reader = new StreamReader(csvStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var records = await ReadCsvRecordsAsync(reader, ct);

        if (records.Count == 0)
            return new PriceCsvImportResult(0, 0, new[] { "CSV file is empty." });

        var headers = SplitCsvRecord(records[0])
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.OrdinalIgnoreCase);

        for (var recordIndex = 1; recordIndex < records.Count; recordIndex++)
        {
            var lineNumber = recordIndex + 1;
            var record = records[recordIndex];
            if (string.IsNullOrWhiteSpace(record)) continue;

            try
            {
                var values = SplitCsvRecord(record);

                string Get(params string[] names)
                {
                    foreach (var name in names)
                    {
                        if (headers.TryGetValue(NormalizeHeader(name), out var index) && index < values.Count)
                            return values[index].Trim();
                    }
                    return string.Empty;
                }

                var capturedText = Get("CapturedAtUtc", "Captured", "Captured At UTC", "Time", "Date");
                var city = Get("City");
                var itemName = Get("ItemName", "Item", "Good", "TradeGood");
                var tradeGoodType = Get("TradeGoodType", "GoodType", "ItemType", "Type");
                var priceText = Get("Price");
                var multiplierText = Get("Multiplier", "PriceMultiplier", "Percent");
                var tradeType = NormalizeTradeType(Get("TradeType", "Trade", "Offer", "BuySell", "Action"));
                var rawText = Get("RawText", "Raw", "OCR", "OcrRawText");

                if (!IsValidTradeType(tradeType) && IsValidTradeType(multiplierText))
                {
                    var temp = tradeType;
                    tradeType = NormalizeTradeType(multiplierText);
                    multiplierText = temp;
                }

                if (!PriceCaptureMergeService.IsKnownCity(city))
                {
                    skipped++;
                    messages.Add($"Line {lineNumber}: skipped because city is unknown.");
                    continue;
                }

                if (!PriceCaptureMergeService.IsKnownTradeType(tradeType))
                {
                    skipped++;
                    messages.Add($"Line {lineNumber}: skipped because trade type is unknown.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(priceText))
                {
                    skipped++;
                    messages.Add($"Line {lineNumber}: skipped because ItemName or Price is missing.");
                    continue;
                }

                if (!TryParseDecimalFlexible(priceText, out var price))
                {
                    skipped++;
                    messages.Add($"Line {lineNumber}: skipped because price '{priceText}' is invalid.");
                    continue;
                }

                decimal? multiplier = null;
                if (!string.IsNullOrWhiteSpace(multiplierText) && TryParseDecimalFlexible(multiplierText.Replace("%", ""), out var parsedMultiplier))
                    multiplier = parsedMultiplier;

                var capture = new PriceCapture
                {
                    City = city,
                    ItemName = itemName,
                    TradeGoodType = tradeGoodType,
                    Price = DecimalToInt(price),
                    Multiplier = multiplier,
                    TradeType = NormalizeTradeType(tradeType),
                    RawText = rawText,
                    CapturedAtUtc = ParseDateAsUtc(capturedText)
                };

                var mergeResult = await PriceCaptureMergeService.AddOrUpdateAsync(db, capture, ct);

                if (mergeResult.Action == PriceCaptureMergeAction.Added) imported++;
                else if (mergeResult.Action == PriceCaptureMergeAction.UpdatedExisting) updated++;
                else skipped++;
            }
            catch (Exception ex)
            {
                skipped++;
                messages.Add($"Line {lineNumber}: skipped because {ex.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        messages.Insert(0, $"Imported {imported} new row(s). Updated {updated} existing row(s). Skipped {skipped} row(s).");
        return new PriceCsvImportResult(imported, skipped, messages.Take(50).ToList());
    }

    private static DateTime ParseDateAsUtc(string capturedText)
    {
        if (string.IsNullOrWhiteSpace(capturedText)) return DateTime.UtcNow;

        if (DateTimeOffset.TryParse(capturedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.UtcDateTime;

        if (DateTime.TryParse(capturedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);

        return DateTime.UtcNow;
    }

    private static string NormalizeTradeType(string value)
    {
        var v = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(v)) return string.Empty;
        var lower = v.ToLowerInvariant();
        return lower switch
        {
            "buy" or "buying" or "items for sale" or "for sale" => "Buy",
            "sell" or "selling" or "inventory" => "Sell",
            "unknown" => "Unknown",
            _ => v
        };
    }

    private static bool IsValidTradeType(string value)
    {
        var normalized = NormalizeTradeType(value);
        return normalized is "Buy" or "Sell" or "Unknown";
    }

    private static bool TryParseDecimalFlexible(string text, out decimal value)
    {
        text = (text ?? string.Empty).Trim().Replace(" ", "");

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return true;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)) return true;

        if (text.Count(c => c == ',') == 1 && !text.Contains('.'))
            return decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

        value = 0;
        return false;
    }

    private static string NormalizeHeader(string header)
    {
        return new string((header ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static async Task<List<string>> ReadCsvRecordsAsync(StreamReader reader, CancellationToken ct)
    {
        var records = new List<string>();
        var current = new StringBuilder();
        string? line;

        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (current.Length > 0) current.Append('\n');
            current.Append(line);

            if (HasBalancedQuotes(current.ToString()))
            {
                records.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) records.Add(current.ToString());
        return records;
    }

    private static bool HasBalancedQuotes(string text)
    {
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"') continue;
            if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }
            inQuotes = !inQuotes;
        }
        return !inQuotes;
    }

    private static List<string> SplitCsvRecord(string record)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < record.Length; i++)
        {
            var c = record[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < record.Length && record[i + 1] == '"')
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

    private static int DecimalToInt(decimal value)
    {
        return decimal.ToInt32(decimal.Truncate(value));
    }
}
