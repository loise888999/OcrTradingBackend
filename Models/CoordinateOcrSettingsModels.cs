namespace OcrTradingBackend.Models;

public static class CoordinateOcrModes
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

public sealed record CoordinateOcrSettingsResponse(
    string CoordinateReadMode,
    bool CoordinateTemplateFallbackToNormalOcr,
    bool CoordinateTemplateCountFailedReadsForRecalibration,
    int CoordinateTemplateRecalibrationFailureLimit,
    bool CoordinateTemplateRequireVisibleTextForFailure,
    double CoordinateTemplateMinTextPixelsPercent,
    int CoordinateTemplateMinContrast,
    bool CoordinateTemplateAutoProfileEnabled,
    bool CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode,
    int CoordinateTemplateAutoProfileMaxSamples,
    double CoordinateTemplateAutoProfileValidationMaxDigitScore,
    int CoordinateTemplateMaxTemplatesPerDigit,
    bool CoordinateTemplateRequirePerDigitOcrValidation,
    bool CoordinateTemplateDebugPrintDigitBitmaps = false,
    bool CoordinateTemplateNormalizeDigitPaddingEnabled = true,
    int CoordinateTemplateDigitHorizontalPaddingPixels = 1,
    int CoordinateTemplateDigitVerticalPaddingPixels = 1,
    int CoordinateTemplateFastModeSpeedMultiplier = 8);

public sealed record UpdateCoordinateOcrSettingsRequest(
    string? CoordinateReadMode,
    bool? CoordinateTemplateFallbackToNormalOcr,
    bool? CoordinateTemplateCountFailedReadsForRecalibration,
    int? CoordinateTemplateRecalibrationFailureLimit,
    bool? CoordinateTemplateRequireVisibleTextForFailure,
    double? CoordinateTemplateMinTextPixelsPercent,
    int? CoordinateTemplateMinContrast,
    bool? CoordinateTemplateAutoProfileEnabled,
    bool? CoordinateTemplateAutoProfileOnlyWhenNormalOcrMode,
    int? CoordinateTemplateAutoProfileMaxSamples,
    double? CoordinateTemplateAutoProfileValidationMaxDigitScore,
    int? CoordinateTemplateMaxTemplatesPerDigit,
    bool? CoordinateTemplateRequirePerDigitOcrValidation,
    bool? CoordinateTemplateDebugPrintDigitBitmaps = null,
    bool? CoordinateTemplateNormalizeDigitPaddingEnabled = null,
    int? CoordinateTemplateDigitHorizontalPaddingPixels = null,
    int? CoordinateTemplateDigitVerticalPaddingPixels = null,
    int? CoordinateTemplateFastModeSpeedMultiplier = null);

public sealed record CoordinateTemplateOcrStatus(
    int FailedReadCount,
    bool NeedsRecalibration,
    string? LastFailureReason,
    DateTime UpdatedAtUtc);

public sealed record CreateCoordinateTemplateProfileRequest(
    string VisibleCoordinate);

public sealed record CoordinateTemplateProfileStatus(
    bool ProfileReady,
    string? ProfileId,
    IReadOnlyList<string> LearnedDigits,
    IReadOnlyList<string> MissingDigitTemplates,
    int TemplateCount,
    int SampleCount,
    string? LastAutoSampleCoordinate,
    string? LastAutoSampleMessage,
    bool AutoProfileEnabled,
    IReadOnlyList<string> LastValidatedDigits,
    IReadOnlyList<string> LastLearnedDigits,
    IReadOnlyList<string> LastRejectedDigits,
    string? LastValidationMessage,
    bool LastSampleAccepted,
    string? LastOcrComparisonText,
    string? LastOcrComparisonMessage,
    bool LastOcrComparisonMatched,
    string? LastSegmentationMode,
    IReadOnlyList<string> LastLowQualityDigits,
    IReadOnlyList<string> LastDigitOcrValidatedDigits,
    IReadOnlyList<string> LastDigitOcrRejectedDigits,
    string? LastDigitOcrValidationMessage,
    string? LastCalibrationMessage,
    CoordinateTemplateSetupProofStatus? LastSuccessfulSetupProof,
    IReadOnlyList<CoordinateDigitTemplatePreview> DigitTemplatePreviews,
    DateTime? CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    CoordinateTemplateOcrStatus Runtime);

public sealed record CoordinateTemplateSetupProofStatus(
    DateTime CapturedAtUtc,
    string Source,
    string? ImageDataUrl,
    string? ImagePath,
    string? VisibleCoordinate,
    string? NormalOcrRawText,
    string? NormalOcrParsedCoordinate,
    string? FastTemplateRawText,
    string? FastTemplateParsedCoordinate,
    bool FastTemplateSuccess,
    string? FastTemplateReason);

public sealed record CoordinateDigitTemplatePreview(
    string Digit,
    bool Ready,
    string? ImageDataUrl,
    string? ImagePath,
    int Width,
    int Height,
    string? Side,
    int DistanceFromSeparator,
    bool TouchesCropEdge,
    double QualityScore);

public sealed class CoordinateTemplateProfile
{
    public string ProfileId { get; set; } = "";
    public OcrLayoutBox CaptureBox { get; set; } = new();
    public int DigitWidth { get; set; }
    public int DigitHeight { get; set; }
    public int BrightnessWhiteThreshold { get; set; } = 180;
    public Dictionary<string, List<CoordinateDigitTemplate>> DigitTemplates { get; set; } = new();
    public List<string> MissingDigitTemplates { get; set; } = new();
    public int SampleCount { get; set; }
    public string? LastAutoSampleCoordinate { get; set; }
    public string? LastAutoSampleMessage { get; set; }
    public List<string> LastValidatedDigits { get; set; } = new();
    public List<string> LastLearnedDigits { get; set; } = new();
    public List<string> LastRejectedDigits { get; set; } = new();
    public string? LastValidationMessage { get; set; }
    public bool LastSampleAccepted { get; set; }
    public string? LastOcrComparisonText { get; set; }
    public string? LastOcrComparisonMessage { get; set; }
    public bool LastOcrComparisonMatched { get; set; }
    public string? LastSegmentationMode { get; set; }
    public List<string> LastLowQualityDigits { get; set; } = new();
    public List<string> LastDigitOcrValidatedDigits { get; set; } = new();
    public List<string> LastDigitOcrRejectedDigits { get; set; } = new();
    public string? LastDigitOcrValidationMessage { get; set; }
    public string LastCalibrationMessage { get; set; } = "";
    public CoordinateTemplateSetupProof? LastSuccessfulSetupProof { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CoordinateTemplateSetupProof
{
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "";
    public string? ImagePath { get; set; }
    public string? VisibleCoordinate { get; set; }
    public string? NormalOcrRawText { get; set; }
    public string? NormalOcrParsedCoordinate { get; set; }
    public string? FastTemplateRawText { get; set; }
    public string? FastTemplateParsedCoordinate { get; set; }
    public bool FastTemplateSuccess { get; set; }
    public string? FastTemplateReason { get; set; }
}

public sealed class CoordinateDigitTemplate
{
    public string Digit { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string[] Pixels { get; set; } = Array.Empty<string>();
    public string Side { get; set; } = "";
    public int DistanceFromSeparator { get; set; }
    public bool TouchesCropEdge { get; set; }
    public double QualityScore { get; set; } = 50;
    public string? ImagePath { get; set; }
    public int SourceX { get; set; }
    public int SourceY { get; set; }
}
