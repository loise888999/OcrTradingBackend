using System.Text.Json;

namespace OcrTradingBackend.Services;

public sealed record RejectedOcrPriceRowLogEntry(
    DateTime TimeUtc,
    string Source,
    string? City,
    string Reason,
    string RawText,
    string? ParserItemName,
    string? DebugImagePath);

internal static class OcrRejectedRowLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static async Task LogPriceRowAsync(
        string source,
        string? city,
        string reason,
        string rawText,
        string? parserItemName,
        string? debugImagePath,
        CancellationToken ct)
    {
        // SaveDebugImages controls this indirectly.
        // IOcrDebugSnapshotService returns null when SaveDebugImages is false.
        if (string.IsNullOrWhiteSpace(debugImagePath))
            return;

        var folder = Path.Combine(AppContext.BaseDirectory, "Data", "rejected-ocr");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, "price-rejected.jsonl");

        var entry = new RejectedOcrPriceRowLogEntry(
            TimeUtc: DateTime.UtcNow,
            Source: source,
            City: city,
            Reason: reason,
            RawText: rawText,
            ParserItemName: parserItemName,
            DebugImagePath: debugImagePath);

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, ct);
    }
}