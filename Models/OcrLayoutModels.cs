namespace OcrTradingBackend.Models;

public sealed class OcrLayoutSettings
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    // All layout boxes are stored as pixels relative to the selected game window,
    // not absolute desktop/screen coordinates.
    public string CoordinateMode { get; set; } = "window-relative-pixels";

    public bool UseLayoutForCity { get; set; } = true;
    public bool UseLayoutForCoordinate { get; set; } = true;
    public bool UseLayoutForPrice { get; set; } = true;

    public int? ScreenWidth { get; set; }
    public int? ScreenHeight { get; set; }

    public OcrBasicLayoutZones Zones { get; set; } = new();
    public OcrPriceLayout Price { get; set; } = new();
}

public sealed class OcrBasicLayoutZones
{
    public OcrLayoutBox? City { get; set; }
    public OcrLayoutBox? Coordinate { get; set; }
}

public sealed class OcrPriceLayout
{
    public int VisibleRows { get; set; } = 4;

    public bool UseFieldBoxes { get; set; } = false;

    public OcrLayoutBox? BuyValidationBox { get; set; }
    public OcrLayoutBox? SellValidationBox { get; set; }

    public List<OcrPriceRowLayout> Rows { get; set; } = new();
}

public sealed class OcrPriceRowLayout
{
    public int Index { get; set; }
    public bool Enabled { get; set; } = true;

    public OcrLayoutBox? Row { get; set; }
    public OcrLayoutBox? ItemName { get; set; }
    public OcrLayoutBox? Price { get; set; }
    public OcrLayoutBox? Multiplier { get; set; }
}

public sealed class OcrLayoutBox
{
    public string Name { get; set; } = "";

    // X/Y are game-window-relative pixels.
    // The backend adds the current game-window Left/Top at capture time.
    public int X { get; set; }
    public int Y { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    public bool IsValid => Width > 0 && Height > 0;

    public OcrZone ToZone(string fallbackName)
    {
        return new OcrZone
        {
            Name = string.IsNullOrWhiteSpace(Name) ? fallbackName : Name,
            TopLeftX = X,
            TopLeftY = Y,
            BottomRightX = X + Width,
            BottomRightY = Y + Height,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }
}

public sealed class SaveOcrLayoutRequest
{
    public OcrLayoutSettings Layout { get; set; } = new();
}

public sealed class OcrLayoutTestBoxRequest
{
    public OcrLayoutBox Box { get; set; } = new();
    public string Kind { get; set; } = "custom";
    public bool Preprocess { get; set; }
}

public sealed record OcrLayoutTestBoxResponse(
    string Kind,
    string Source,
    string RawText,
    string? DebugImagePath,
    string? DebugImageUrl,
    OcrLayoutBox Box,
    OcrZone CaptureZone);

public sealed record OcrCalibrationResponse(
    bool LayoutEnabled,
    double Score,
    int PassedChecks,
    int WarningChecks,
    int FailedChecks,
    int SkippedChecks,
    IReadOnlyList<OcrCalibrationCheck> Checks,
    IReadOnlyList<string> Recommendations);

public sealed record OcrCalibrationCheck(
    string Key,
    string Label,
    string Kind,
    string Status,
    double Score,
    string RawText,
    string? ParsedText,
    string Message,
    string? DebugImagePath,
    OcrLayoutBox? Box,
    OcrZone? CaptureZone);
