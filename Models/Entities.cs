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
    public int Price { get; set; }
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
    public bool UseEnglishModels { get; set; }
    public bool FallbackToBundledModel { get; set; } = true;
    public string DetectionModelPath { get; set; } = "";
    public string ClassifierModelPath { get; set; } = "";
    public string RecognitionModelPath { get; set; } = "";
    public string DictionaryPath { get; set; } = "";

    // Fallback OCR loop delay when DefaultIntervalMilliseconds is not configured.
    public int DefaultIntervalSeconds { get; set; } = 1;

    // City OCR is attempted no more often than this interval while not at sea.
    public int CityIntervalSeconds { get; set; } = 8;

    // Normal Buy/Sell price OCR interval when fast mode is not active.
    public int PriceIntervalSeconds { get; set; } = 6;

    // Faster Buy/Sell price OCR interval while price fast mode is active.
    public int ActivePriceIntervalSeconds { get; set; } = 1;

    // How long fast mode stays active after a new price state is found.
    public int PriceFastModeSeconds { get; set; } = 20;

    public int WorldWidth { get; set; } = 16500;
    public int WorldHeight { get; set; } = 7200;
    public int XZeroVisualOffset { get; set; } = 8250;
    public string CoordinateReadMode { get; set; } = "NormalOcr";
    public bool CoordinateTemplateFallbackToNormalOcr { get; set; }
    public bool CoordinateTemplateCountFailedReadsForRecalibration { get; set; } = true;
    public int CoordinateTemplateRecalibrationFailureLimit { get; set; } = 5;
    public bool CoordinateTemplateRequireVisibleTextForFailure { get; set; } = true;
    public double CoordinateTemplateMinTextPixelsPercent { get; set; } = 0.35;
    public int CoordinateTemplateMinContrast { get; set; } = 18;
    public bool CoordinateTemplateAutoProfileEnabled { get; set; }
    public bool CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode { get; set; } = true;
    public int CoordinateTemplateAutoProfileMaxSamples { get; set; } = 200;
    public double CoordinateTemplateAutoProfileValidationMaxDigitScore { get; set; } = 0.18;
    public int CoordinateTemplateMaxTemplatesPerDigit { get; set; } = 5;
    public bool CoordinateTemplateRequirePerDigitOcrValidation { get; set; } = true;
    public bool CoordinateTemplateNormalizeDigitPaddingEnabled { get; set; } = true;
    public int CoordinateTemplateDigitHorizontalPaddingPixels { get; set; } = 1;
    public int CoordinateTemplateDigitVerticalPaddingPixels { get; set; } = 1;
    public bool CoordinateTemplateDebugPrintDigitBitmaps { get; set; }
    // When CoordinateReadMode is FastTemplate, divide CoordinateIntervalMilliseconds by this value.
    // Example: 1500ms / 8 = about 188ms.
    public int CoordinateTemplateFastModeSpeedMultiplier { get; set; } = 8;

    public int CoordinateIntervalMilliseconds { get; set; } = 2000;
    public int CoordinateRecentlyVisibleSeconds { get; set; } = 10;
    public bool CoordinateRequiresProbablyAtSea { get; set; } = true;
    public int ProbablyAtSeaAfterNoCityOrMenuSeconds { get; set; } = 30;
    public int MinCityNameLength { get; set; } = 5;

    public bool EnableCoordinateCorrection { get; set; } = true;
    public int MaxCoordinateJumpX { get; set; } = 1200;
    public int MaxCoordinateJumpY { get; set; } = 900;
    public bool CoordinateFarJumpConfirmationEnabled { get; set; } = true;
    public int CoordinateFarJumpRequiredReads { get; set; } = 4;
    public int CoordinateFarJumpClusterToleranceX { get; set; } = 100;
    public int CoordinateFarJumpClusterToleranceY { get; set; } = 100;

    // Preprocessing helps when coordinate text is small: upscale + grayscale + threshold.
    public bool CoordinateTryPreprocess { get; set; } = true;
    public int CoordinateOcrUpscale { get; set; } = 3;
    public int CoordinateOcrThreshold { get; set; } = 145;
    public bool CoordinateForcePreprocess { get; set; }
    public bool OcrPreprocessCleanupEnabled { get; set; } = true;
    public bool CoordinatePreprocessCleanupEnabled { get; set; } = true;
    public bool CoordinatePreprocessRemoveSmallBlobsEnabled { get; set; } = true;
    public int CoordinatePreprocessMinWhiteBlobPixels { get; set; } = 3;
    public bool CoordinatePreprocessTextShapeFilterEnabled { get; set; }
    public int CoordinatePreprocessMinTextLikeBlobWidth { get; set; } = 2;
    public int CoordinatePreprocessMinTextLikeBlobHeight { get; set; } = 4;
    public int CoordinatePreprocessMaxTextLikeBlobHeightPercent { get; set; } = 90;

    public bool CityTryPreprocess { get; set; } = true;
    public int CityOcrUpscale { get; set; } = 2;
    public int CityOcrThreshold { get; set; } = 145;
    public bool CityOcrInvert { get; set; }
    public bool CityForcePreprocess { get; set; }

    public bool PriceTryPreprocess { get; set; } = true;
    public int PriceOcrUpscale { get; set; } = 2;
    public int PriceOcrThreshold { get; set; } = 145;
    public bool PriceOcrInvert { get; set; }
    public bool PriceForcePreprocess { get; set; } = true;

    // Legacy switch for OCR hash skipping; full-hash cache now uses OcrFullHashCacheEnabled.
    public bool SkipUnchangedOcrByHash { get; set; } = true;

    // When enabled, every OCR image is full-hashed before Paddle OCR runs.
    public bool OcrFullHashCacheEnabled { get; set; } = true;

    // Full-hash OCR cache TTL. Re-reading a cached hash refreshes this lifetime.
    public double OcrFullHashCacheMinutes { get; set; } = 5.0;

    // Maximum full-hash OCR results kept in memory before oldest entries are evicted.
    public int OcrFullHashCacheMaxEntries { get; set; } = 1000;

    public bool PriceBatchCaptureEnabled { get; set; } = true;
    public double PriceBatchIdleFlushSeconds { get; set; }
    public double PriceBatchFlushEverySeconds { get; set; } = 5.0;
    public int PriceBatchCaptureIntervalMs { get; set; } = 150;
    public int PriceBatchMaxImages { get; set; } = 30;

    public bool PriceRecentHashCacheEnabled { get; set; } = true;

    // Keeps recently processed price image hashes from being OCR-processed again.
    public double PriceRecentHashCacheMinutes { get; set; } = 10.0;

    // Maximum remembered price image hashes for duplicate-skip protection.
    public int PriceRecentHashCacheMaxEntries { get; set; } = 5000;

    // Validates that a Buy/Sell menu is visible before reading price rows.
    public bool PriceMenuValidationEnabled { get; set; } = true;

    // Percent of the price area used for Buy/Sell menu validation.
    public double PriceMenuValidationTopPercent { get; set; } = 25.0;

    // Preprocesses menu validation crop before OCR.
    public bool PriceMenuValidationUsePreprocess { get; set; } = true;

    // Words accepted as proof that the price menu is open.
    public string PriceMenuValidationValidWords { get; set; } = "Buy|Sell";

    // After menu validation, only capture the body below the validation area.
    public bool PriceCaptureBodyOnlyAfterMenuValidation { get; set; } = true;

    // Preprocesses Buy/Sell validation layout boxes before OCR.
    public bool PriceLayoutValidationPreprocess { get; set; } = true;

    // Preprocesses row OCR crops before OCR.
    public bool PriceLayoutFieldPreprocess { get; set; } = true;

    // If whole-row OCR fails, try separate item/price/multiplier boxes.
    public bool PriceLayoutFieldFallbackEnabled { get; set; } = false;

    // Allowed difference for whole-row perceptual fingerprint cache hits.
    public int PriceLayoutRowFingerprintTolerance { get; set; } = 10;

    public bool OcrAllowedCharFilteringEnabled { get; set; } = true;
    public string CoordinateOcrAllowedChars { get; set; } = "0123456789XYxy,:=. \r\n";
    public string PriceNumberOcrAllowedChars { get; set; } = "0123456789,. \r\n";
    public string PriceMultiplierOcrAllowedChars { get; set; } = "0123456789% \r\n";
    public string PriceMenuOcrAllowedChars { get; set; } = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz \r\n";

    // Off, BeforePreprocess, AfterPreprocess, or BeforeAndAfter; skips OCR when crop likely has no text.
    public string OcrTextPresenceGateMode { get; set; } = "BeforePreprocess";

    // Minimum brightness contrast required by the text-presence gate.
    public int OcrTextPresenceMinContrast { get; set; } = 18;

    // Minimum edge-pixel percent required by the text-presence gate.
    public double OcrTextPresenceMinEdgePixelsPercent { get; set; } = 0.35;

    // Pixel sampling step used by the text-presence gate; higher is faster but less sensitive.
    public int OcrTextPresenceSampleStep { get; set; } = 3;
    public bool OcrBenchmarkLogging { get; set; } = true;

    public string CoordinateOcrZoneName { get; set; } = "Coordinate";
    public string CityOcrZoneName { get; set; } = "City";
    public string PriceOcrZoneName { get; set; } = "Price";
}

public sealed class OcrControlState
{
    public bool Enabled { get; set; }
    public DateTime? LastCoordinateAttemptUtc { get; set; }
    public DateTime? LastCoordinateReadUtc { get; set; }
    public DateTime? LastCityAttemptUtc { get; set; }
    public DateTime? LastCityReadUtc { get; set; }
    public DateTime? LastPriceReadUtc { get; set; }
    public DateTime? LastPriceAttemptUtc { get; set; }
    public DateTime? LastPriceStateChangeUtc { get; set; }
    public DateTime? PriceFastModeUntilUtc { get; set; }
    public DateTime? LastNotAtSeaSignalUtc { get; set; }
    public DateTime? SeaCandidateSinceUtc { get; set; }
    public bool ProbablyAtSea { get; set; }
    public string? LastError { get; set; }
}

public sealed record ParsedCoordinate(int X, int Y, string RawText);
public sealed record ParsedPriceLine(string ItemName, string TradeGoodType, decimal Price, decimal? Multiplier, string TradeType, string RawText);
public sealed record TradingRecommendation(string ItemName, string TradeGoodType, string BuyCity, decimal BuyPrice, string SellCity, decimal SellPrice, decimal Profit, decimal? BuyMultiplier, decimal? SellMultiplier);
public sealed record TradingSearchResult(string City, string ItemName, string TradeGoodType, decimal Price, decimal? Multiplier, string TradeType, DateTime CapturedAtUtc, string RawText);
