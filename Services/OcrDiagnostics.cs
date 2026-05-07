namespace OcrTradingBackend.Services;

public sealed record OcrManualReadResponse(
    string ZoneKind,
    string ZoneName,
    bool ZoneFound,
    IReadOnlyList<OcrManualReadAttempt> Attempts,
    object? BestParsed);

public sealed record OcrManualReadAttempt(
    string Source,
    string RawText,
    object? Parsed,
    string? DebugImagePath);

public sealed record OcrLastResultSnapshot(
    OcrReadSnapshot? Coordinate,
    OcrReadSnapshot? City,
    OcrReadSnapshot? Price,
    string? LastFailureReason,
    DateTime UpdatedAtUtc);

public sealed record OcrReadSnapshot(
    string Kind,
    string Source,
    string RawText,
    object? Parsed,
    int ParsedCount,
    string? DebugImagePath,
    DateTime CapturedAtUtc);

public sealed class OcrLastResultState
{
    private readonly object _gate = new();

    private OcrLastResultSnapshot _snapshot = new(
        Coordinate: null,
        City: null,
        Price: null,
        LastFailureReason: null,
        UpdatedAtUtc: DateTime.UtcNow);

    public OcrLastResultSnapshot GetSnapshot()
    {
        lock (_gate)
            return _snapshot;
    }

    public void SetCoordinate(string source, string rawText, object? parsed, string? debugImagePath)
    {
        SetRead("coordinate", source, rawText, parsed, parsed is null ? 0 : 1, debugImagePath);
    }

    public void SetCity(string source, string rawText, object? parsed, string? debugImagePath)
    {
        SetRead("city", source, rawText, parsed, parsed is null ? 0 : 1, debugImagePath);
    }

    public void SetPrice(string source, string rawText, object? parsed, int parsedCount, string? debugImagePath)
    {
        SetRead("price", source, rawText, parsed, parsedCount, debugImagePath);
    }

    public void SetFailure(string message)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastFailureReason = message,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
    }

    private void SetRead(
        string kind,
        string source,
        string rawText,
        object? parsed,
        int parsedCount,
        string? debugImagePath)
    {
        var read = new OcrReadSnapshot(
            Kind: kind,
            Source: source,
            RawText: rawText,
            Parsed: parsed,
            ParsedCount: parsedCount,
            DebugImagePath: debugImagePath,
            CapturedAtUtc: DateTime.UtcNow);

        lock (_gate)
        {
            _snapshot = kind switch
            {
                "coordinate" => _snapshot with
                {
                    Coordinate = read,
                    LastFailureReason = null,
                    UpdatedAtUtc = DateTime.UtcNow
                },

                "city" => _snapshot with
                {
                    City = read,
                    LastFailureReason = null,
                    UpdatedAtUtc = DateTime.UtcNow
                },

                "price" => _snapshot with
                {
                    Price = read,
                    LastFailureReason = null,
                    UpdatedAtUtc = DateTime.UtcNow
                },

                _ => _snapshot
            };
        }
    }
}