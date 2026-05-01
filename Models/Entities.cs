namespace OcrTradingBackend.Models;

public sealed class OcrZone
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int TopLeftX { get; set; }
    public int TopLeftY { get; set; }
    public int BottomRightX { get; set; }
    public int BottomRightY { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CoordinateCapture
{
    public int Id { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string RawText { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CityCapture
{
    public int Id { get; set; }
    public string City { get; set; } = "";
    public string RawText { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PriceCapture
{
    public int Id { get; set; }
    public string City { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string TradeGoodType { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? Multiplier { get; set; }
    public string TradeType { get; set; } = "Unknown";
    public string RawText { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppSetting
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class OcrRuntimeSettings
{
    public bool Enabled { get; set; }
    public int DefaultIntervalSeconds { get; set; } = 2;
    public int CityIntervalSeconds { get; set; } = 8;
    public int PriceIntervalSeconds { get; set; } = 6;
    public int ActivePriceIntervalSeconds { get; set; } = 1;
    public int PriceFastModeSeconds { get; set; } = 20;

    public int WorldWidth { get; set; } = 16500;
    public int WorldHeight { get; set; } = 7200;
    public int XZeroVisualOffset { get; set; } = 8250;
    public int CoordinateRecentlyVisibleSeconds { get; set; } = 10;
    public int MinCityNameLength { get; set; } = 5;

    // Coordinate OCR correction settings.
    public bool EnableCoordinateCorrection { get; set; } = true;

    // X is wrap-around, so jump distance is circular. Example with width 16500:
    // X 1 to X 16250 is only 251 away, not 16249.
    public int MaxCoordinateJumpX { get; set; } = 1200;

    // Y does not wrap, so this is a normal absolute jump limit.
    public int MaxCoordinateJumpY { get; set; } = 900;

    public string CoordinateOcrZoneName { get; set; } = "Coordinate";
    public string CityOcrZoneName { get; set; } = "City";
    public string PriceOcrZoneName { get; set; } = "Price";
}

public sealed class OcrControlState
{
    public bool Enabled { get; set; }
    public DateTime? LastCoordinateReadUtc { get; set; }
    public DateTime? LastCityReadUtc { get; set; }
    public DateTime? LastPriceReadUtc { get; set; }
    public DateTime? LastPriceAttemptUtc { get; set; }
    public DateTime? LastPriceStateChangeUtc { get; set; }
    public DateTime? PriceFastModeUntilUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed record ParsedCoordinate(int X, int Y, string RawText);
public sealed record ParsedPriceLine(string ItemName, string TradeGoodType, decimal Price, decimal? Multiplier, string TradeType, string RawText);
public sealed record TradingRecommendation(string ItemName, string TradeGoodType, string BuyCity, decimal BuyPrice, string SellCity, decimal SellPrice, decimal Profit, decimal? BuyMultiplier, decimal? SellMultiplier);
public sealed record TradingSearchResult(string City, string ItemName, string TradeGoodType, decimal Price, decimal? Multiplier, string TradeType, DateTime CapturedAtUtc, string RawText);
