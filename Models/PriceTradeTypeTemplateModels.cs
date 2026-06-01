namespace OcrTradingBackend.Models;

public static class PriceTradeTypeReadModes
{
    public const string NormalOcr = "NormalOcr";
    public const string FastTemplate = "FastTemplate";

    public static bool IsValid(string? value)
        => value is not null &&
           (value.Equals(NormalOcr, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(FastTemplate, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? value)
        => value is not null && value.Equals(FastTemplate, StringComparison.OrdinalIgnoreCase)
            ? FastTemplate
            : NormalOcr;
}

public sealed record PriceTradeTypeTemplateSettingsResponse(
    string PriceTradeTypeReadMode,
    bool PriceTradeTypeTemplateFallbackToNormalOcr,
    bool PriceTradeTypeTemplateAutoProfileEnabled,
    int PriceTradeTypeTemplateMaxTemplatesPerType,
    double PriceTradeTypeTemplateMaxScore,
    bool PriceTradeTypeTemplateCountFailedReadsForRecalibration,
    int PriceTradeTypeTemplateRecalibrationFailureLimit,
    int PriceTradeTypeTemplateProbeIntervalMs);

public sealed record UpdatePriceTradeTypeTemplateSettingsRequest(
    string? PriceTradeTypeReadMode,
    bool? PriceTradeTypeTemplateFallbackToNormalOcr,
    bool? PriceTradeTypeTemplateAutoProfileEnabled,
    int? PriceTradeTypeTemplateMaxTemplatesPerType,
    double? PriceTradeTypeTemplateMaxScore,
    bool? PriceTradeTypeTemplateCountFailedReadsForRecalibration,
    int? PriceTradeTypeTemplateRecalibrationFailureLimit,
    int? PriceTradeTypeTemplateProbeIntervalMs);

public sealed record PriceTradeTypeTemplateTestBoxRequest(
    string Region,
    bool LearnIfNormalOcrMatches = false);

public sealed record PriceTradeTypeTemplateReadAttempt(
    bool Success,
    string? TradeType,
    double? Score,
    string Reason,
    bool NeedsRecalibration);

public sealed record PriceTradeTypeTemplateProfileStatus(
    bool ProfileReady,
    string? ProfileId,
    bool BuyReady,
    bool SellReady,
    IReadOnlyList<string> MissingTemplates,
    int BuyTemplateCount,
    int SellTemplateCount,
    int SampleCount,
    int FailedReadCount,
    bool NeedsRecalibration,
    string? LastMessage,
    bool AutoProfileEnabled,
    DateTime? CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    PriceTradeTypeTemplateSetupProofStatus? LastSuccessfulBuySetupProof,
    PriceTradeTypeTemplateSetupProofStatus? LastSuccessfulSellSetupProof,
    IReadOnlyList<PriceTradeTypeTemplateAttemptLog> LastAttempts);

public sealed record PriceTradeTypeTemplateSetupProofStatus(
    DateTime CapturedAtUtc,
    string Region,
    string? ImageDataUrl,
    string? ImagePath,
    bool TextVisible,
    double Contrast,
    double EdgePixelsPercent,
    string? NormalOcrRawText,
    string? NormalOcrDetectedTradeType,
    string? FastTemplateDetectedTradeType,
    bool FastTemplateSuccess,
    double? FastTemplateScore,
    string? FastTemplateReason,
    bool LearnedTemplate);

public sealed class PriceTradeTypeTemplateProfile
{
    public string ProfileId { get; set; } = "";
    public string GameWindowTitle { get; set; } = "";
    public List<PriceTradeTypeBoxTemplate> BuyTemplates { get; set; } = new();
    public List<PriceTradeTypeBoxTemplate> SellTemplates { get; set; } = new();
    public List<string> MissingTemplates { get; set; } = new() { "Buy", "Sell" };
    public int SampleCount { get; set; }
    public int FailedReadCount { get; set; }
    public bool NeedsRecalibration { get; set; }
    public string? LastMessage { get; set; }
    public PriceTradeTypeTemplateSetupProof? LastSuccessfulBuySetupProof { get; set; }
    public PriceTradeTypeTemplateSetupProof? LastSuccessfulSellSetupProof { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PriceTradeTypeTemplateSetupProof
{
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public string Region { get; set; } = "";
    public string? ImagePath { get; set; }
    public bool TextVisible { get; set; }
    public double Contrast { get; set; }
    public double EdgePixelsPercent { get; set; }
    public string? NormalOcrRawText { get; set; }
    public string? NormalOcrDetectedTradeType { get; set; }
    public string? FastTemplateDetectedTradeType { get; set; }
    public bool FastTemplateSuccess { get; set; }
    public double? FastTemplateScore { get; set; }
    public string? FastTemplateReason { get; set; }
    public bool LearnedTemplate { get; set; }
}

public sealed class PriceTradeTypeBoxTemplate
{
    public string TradeType { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string[] Pixels { get; set; } = Array.Empty<string>();
    public OcrLayoutBox SourceBox { get; set; } = new();
    public double ScoreThreshold { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record PriceTradeTypeTemplateAttemptLog(
    DateTime CapturedAtUtc,
    string Region,
    string Source,
    bool Success,
    string? DetectedTradeType,
    double? Score,
    double Threshold,
    string? RawText,
    bool UsedNormalOcr,
    bool LearnedTemplate,
    string Reason,
    string? DebugImagePath);
