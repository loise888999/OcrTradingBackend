using OcrTradingBackend.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OcrTradingBackend.Services;

public interface ICoordinateTemplateOcrService
{
    CoordinateTemplateOcrStatus GetStatus();
    CoordinateTemplateProfileStatus GetProfileStatus(bool autoProfileEnabled = false);
    Task<CoordinateTemplateProfileStatus> CreateProfileAsync(
        Bitmap bitmap,
        OcrLayoutBox captureBox,
        CreateCoordinateTemplateProfileRequest request,
        OcrRuntimeSettings settings,
        CancellationToken ct);
    CoordinateTemplateProfileStatus AddProfileSampleFromNormalOcr(
        Bitmap bitmap,
        OcrLayoutBox captureBox,
        ParsedCoordinate parsedCoordinate,
        CoordinateOcrSettingsResponse coordinateOcrSettings,
        OcrRuntimeSettings settings,
        Func<Bitmap, string?>? perDigitOcrReader = null);
    CoordinateTemplateReadAttempt TryRead(Bitmap bitmap, CoordinateOcrSettingsResponse settings);
    void ResetFailures();
    CoordinateTemplateOcrStatus MaybeCountFailedFastRead(CoordinateOcrSettingsResponse settings, string reason);
}

public sealed record CoordinateTemplateReadAttempt(
    bool Success,
    string? RawText,
    ParsedCoordinate? Parsed,
    string Reason,
    bool NeedsRecalibration);

public sealed class CoordinateTemplateOcrService : ICoordinateTemplateOcrService
{
    private const string InkPixel = "1";
    private const string BackgroundPixel = "0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Regex CoordinateRegex = new(
        @"^\s*(?<x>\d{1,5})\s*,\s*(?<y>\d{1,4})\s*$",
        RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly string _profilePath;
    private int _failedReadCount;
    private bool _needsRecalibration;
    private string? _lastFailureReason;
    private DateTime _updatedAtUtc = DateTime.UtcNow;

    public CoordinateTemplateOcrService()
        : this(Path.Combine(AppContext.BaseDirectory, "Data", "coordinate-template-profile.json"))
    {
    }

    public CoordinateTemplateOcrService(IWebHostEnvironment environment, IConfiguration configuration)
        : this(ResolveProfilePath(environment.ContentRootPath, configuration))
    {
    }

    public CoordinateTemplateOcrService(string profilePath)
    {
        _profilePath = profilePath;
    }

    public CoordinateTemplateOcrStatus GetStatus()
    {
        lock (_gate)
            return BuildStatusLocked();
    }

    public CoordinateTemplateProfileStatus GetProfileStatus(bool autoProfileEnabled = false)
    {
        lock (_gate)
            return BuildProfileStatusLocked(autoProfileEnabled);
    }

    public async Task<CoordinateTemplateProfileStatus> CreateProfileAsync(
        Bitmap bitmap,
        OcrLayoutBox captureBox,
        CreateCoordinateTemplateProfileRequest request,
        OcrRuntimeSettings settings,
        CancellationToken ct)
    {
        if (captureBox is not { IsValid: true })
            throw new InvalidOperationException("Coordinate layout box is missing or invalid.");

        var normalized = ValidateAndNormalizeCoordinate(
            request.VisibleCoordinate,
            settings.WorldWidth,
            settings.WorldHeight);

        var build = BuildTemplates(bitmap, normalized, threshold: 180);
        var templates = KeepSingleTemplatePerDigit(build.Templates);
        var allTemplates = templates.Values.SelectMany(x => x).ToList();

        if (allTemplates.Count == 0)
            throw new InvalidOperationException("No digit pixels were found in the coordinate crop.");

        var digitWidth = Math.Max(1, (int)Math.Round(allTemplates.Average(x => x.Width)));
        var digitHeight = Math.Max(1, (int)Math.Round(allTemplates.Average(x => x.Height)));

        var missing = BuildMissingDigits(templates);
        var now = DateTime.UtcNow;
        var existing = LoadProfileLocked();

        await Task.CompletedTask;

        var profile = new CoordinateTemplateProfile
        {
            ProfileId = existing?.ProfileId ?? Guid.NewGuid().ToString("N"),
            CaptureBox = captureBox,
            DigitWidth = digitWidth,
            DigitHeight = digitHeight,
            BrightnessWhiteThreshold = 180,
            DigitTemplates = templates,
            MissingDigitTemplates = missing,
            SampleCount = existing?.SampleCount ?? 0,
            LastAutoSampleCoordinate = normalized,
            LastAutoSampleMessage = missing.Count == 0
                ? "Manual profile created with all 10 digits."
                : $"Manual profile created; missing {string.Join(",", missing)}.",
            LastValidatedDigits = templates.Keys.OrderBy(x => x).ToList(),
            LastLearnedDigits = templates.Keys.OrderBy(x => x).ToList(),
            LastRejectedDigits = new List<string>(),
            LastValidationMessage = "Manual profile created from visible coordinate.",
            LastSampleAccepted = true,
            LastSegmentationMode = build.Mode,
            LastLowQualityDigits = build.LowQualityDigits.ToList(),
            LastDigitOcrValidatedDigits = new List<string>(),
            LastDigitOcrRejectedDigits = new List<string>(),
            LastDigitOcrValidationMessage = "Manual profile creation does not use per-digit OCR validation.",
            LastCalibrationMessage = $"Profile created from {normalized}. Templates: {templates.Count}; missing: {(missing.Count == 0 ? "none" : string.Join(",", missing))}.",
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now
        };

        UpdateFullProfileOcrComparison(profile, bitmap, normalized, maxDigitScore: 0.45);

        lock (_gate)
        {
            SaveProfileLocked(profile);
            _failedReadCount = 0;
            _needsRecalibration = false;
            _lastFailureReason = null;
            _updatedAtUtc = now;
            return BuildProfileStatusLocked(profile);
        }
    }

    public CoordinateTemplateProfileStatus AddProfileSampleFromNormalOcr(
        Bitmap bitmap,
        OcrLayoutBox captureBox,
        ParsedCoordinate parsedCoordinate,
        CoordinateOcrSettingsResponse coordinateOcrSettings,
        OcrRuntimeSettings settings,
        Func<Bitmap, string?>? perDigitOcrReader = null)
    {
        if (!coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled)
            return GetProfileStatus(coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);

        lock (_gate)
        {
            var existing = LoadProfileLocked();

            if (existing is not null)
            {
                var missingDigits = BuildMissingDigits(existing.DigitTemplates);
                var templateCount = existing.DigitTemplates.Values.Sum(x => x.Count);
                var hasAllTenDigits = missingDigits.Count == 0 && templateCount >= 10;

                if (hasAllTenDigits)
                {
                    existing.LastAutoSampleMessage =
                        "Profile already learned all 10 digits. Auto profile learning is complete.";

                    existing.LastSampleAccepted = true;
                    existing.LastLearnedDigits = new List<string>();
                    existing.LastRejectedDigits = new List<string>();
                    existing.LastValidationMessage =
                        "Learning skipped because the profile already contains digits 0-9.";

                    existing.LastOcrComparisonText = null;
                    existing.LastOcrComparisonMatched = false;
                    existing.LastOcrComparisonMessage =
                        "Skipped full profile check because learning is already complete.";

                    existing.UpdatedAtUtc = DateTime.UtcNow;
                    SaveProfileLocked(existing);

                    return BuildProfileStatusLocked(
                        existing,
                        coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
                }

                if (existing.SampleCount >= coordinateOcrSettings.CoordinateTemplateAutoProfileMaxSamples)
                {
                    // Do not stop before all 10 digits are learned.
                    // Otherwise one or two missing digits can get stuck forever.
                    existing.LastAutoSampleMessage =
                        $"Auto profile sample limit reached, but profile is incomplete. Continuing until missing digits are learned: {string.Join(",", missingDigits)}.";

                    existing.SampleCount = 0;
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                    SaveProfileLocked(existing);
                }
            }
        }

        var normalized = ValidateAndNormalizeCoordinate(
            $"{parsedCoordinate.X},{parsedCoordinate.Y}",
            settings.WorldWidth,
            settings.WorldHeight);

        var sampleBuild = BuildTemplates(bitmap, normalized, threshold: 180);
        var sampleTemplates = sampleBuild.Templates;
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            var profile = LoadProfileLocked() ?? new CoordinateTemplateProfile
            {
                ProfileId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = now
            };

            profile.CaptureBox = captureBox;
            profile.BrightnessWhiteThreshold = 180;
            profile.SampleCount++;
            profile.LastAutoSampleCoordinate = normalized;
            profile.LastSegmentationMode = sampleBuild.Mode;
            profile.LastLowQualityDigits = sampleBuild.LowQualityDigits.ToList();

            var digitOcr = ValidateAndFilterTemplatesWithDigitOcr(
                bitmap,
                sampleTemplates,
                coordinateOcrSettings,
                perDigitOcrReader);

            profile.LastDigitOcrValidatedDigits = digitOcr.ValidatedDigits.ToList();
            profile.LastDigitOcrRejectedDigits = digitOcr.RejectedDigits.ToList();
            profile.LastDigitOcrValidationMessage = digitOcr.Message;

            // Only keep digit crops where:
            // full-coordinate OCR expected digit == ReadCalibrationDigitOcr(crop).
            sampleTemplates = digitOcr.ValidatedTemplates
                .ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.Ordinal);

            if (sampleTemplates.Count == 0)
            {
                profile.LastValidatedDigits = new List<string>();
                profile.LastLearnedDigits = new List<string>();
                profile.LastRejectedDigits = digitOcr.RejectedDigits.ToList();
                profile.LastSampleAccepted = false;
                profile.LastValidationMessage = digitOcr.Message;
                profile.LastAutoSampleMessage = digitOcr.Message;

                profile.LastOcrComparisonText = null;
                profile.LastOcrComparisonMatched = false;
                profile.LastOcrComparisonMessage =
                    "Skipped full profile check because per-digit OCR rejected all digit crops.";

                profile.UpdatedAtUtc = now;
                SaveProfileLocked(profile);

                return BuildProfileStatusLocked(
                    profile,
                    coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
            }

            var learnedBefore = profile.DigitTemplates
                .Where(x => x.Value.Count > 0)
                .Select(x => x.Key)
                .ToHashSet(StringComparer.Ordinal);

            var profileHasLearnedDigits = learnedBefore.Count > 0;
            var validatedDigits = new SortedSet<string>(StringComparer.Ordinal);
            var rejectedDigits = new SortedSet<string>(StringComparer.Ordinal);
            var learnedDigits = new SortedSet<string>(StringComparer.Ordinal);
            var validationMessages = new List<string>();

            var knownDigitsInSample = sampleTemplates.Keys
                .Where(learnedBefore.Contains)
                .OrderBy(x => x)
                .ToList();

            if (profileHasLearnedDigits && knownDigitsInSample.Count == 0)
            {
                profile.LastValidatedDigits = new List<string>();
                profile.LastLearnedDigits = new List<string>();
                profile.LastRejectedDigits = sampleTemplates.Keys.OrderBy(x => x).ToList();
                profile.LastSampleAccepted = false;
                profile.LastValidationMessage =
                    $"Auto sample {normalized} rejected: no previously learned digits were present to validate.";
                profile.LastAutoSampleMessage = profile.LastValidationMessage;
                profile.UpdatedAtUtc = now;
                SaveProfileLocked(profile);

                return BuildProfileStatusLocked(
                    profile,
                    coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
            }

            foreach (var digit in knownDigitsInSample)
            {
                var candidates = sampleTemplates[digit];
                var best = candidates
                    .Select(candidate => BestTemplateScore(candidate, profile.DigitTemplates[digit]))
                    .DefaultIfEmpty(double.PositiveInfinity)
                    .Min();

                if (best <= coordinateOcrSettings.CoordinateTemplateAutoProfileValidationMaxDigitScore)
                {
                    validatedDigits.Add(digit);
                }
                else
                {
                    rejectedDigits.Add(digit);
                    validationMessages.Add($"{digit} score {best:F3}");
                }
            }

            if (rejectedDigits.Count > 0)
            {
                profile.LastValidatedDigits = validatedDigits.ToList();
                profile.LastLearnedDigits = new List<string>();
                profile.LastRejectedDigits = rejectedDigits.ToList();
                profile.LastSampleAccepted = false;
                profile.LastValidationMessage =
                    $"Auto sample {normalized} rejected: validation failed for {string.Join(",", rejectedDigits)} ({string.Join("; ", validationMessages)}).";
                profile.LastAutoSampleMessage = profile.LastValidationMessage;

                profile.LastOcrComparisonText = null;
                profile.LastOcrComparisonMatched = false;
                profile.LastOcrComparisonMessage =
                    $"Skipped full profile check because auto sample {normalized} was rejected.";

                profile.UpdatedAtUtc = now;
                SaveProfileLocked(profile);

                return BuildProfileStatusLocked(
                    profile,
                    coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
            }

            foreach (var (digit, templates) in sampleTemplates.OrderBy(x => x.Key))
            {
                if (profile.DigitTemplates.TryGetValue(digit, out var existingTemplates) &&
                    existingTemplates.Count > 0)
                {
                    continue;
                }

                if (!profile.DigitTemplates.TryGetValue(digit, out existingTemplates))
                {
                    existingTemplates = new List<CoordinateDigitTemplate>();
                    profile.DigitTemplates[digit] = existingTemplates;
                }

                var templateToLearn = templates
                    .OrderByDescending(x => x.QualityScore)
                    .FirstOrDefault();

                if (templateToLearn is null)
                    continue;

                AddTemplateVariant(existingTemplates, templateToLearn, maxTemplates: 1);

                if (existingTemplates.Count > 0)
                    learnedDigits.Add(digit);
            }

            var allTemplates = profile.DigitTemplates.Values.SelectMany(x => x).ToList();
            if (allTemplates.Count > 0)
            {
                profile.DigitWidth = Math.Max(1, (int)Math.Round(allTemplates.Average(x => x.Width)));
                profile.DigitHeight = Math.Max(1, (int)Math.Round(allTemplates.Average(x => x.Height)));
            }

            profile.MissingDigitTemplates = BuildMissingDigits(profile.DigitTemplates);
            profile.LastValidatedDigits = validatedDigits.ToList();
            profile.LastLearnedDigits = learnedDigits.ToList();
            profile.LastRejectedDigits = digitOcr.RejectedDigits.ToList();
            profile.LastSampleAccepted = true;

            var learnedMessage = learnedDigits.Count == 0
                ? "no new digits learned"
                : $"learned {string.Join(",", learnedDigits)}";

            profile.LastValidationMessage = validatedDigits.Count == 0
                ? $"Auto sample {normalized} accepted by per-digit OCR validation."
                : $"Auto sample {normalized} validated known digits {string.Join(",", validatedDigits)}.";

            profile.LastAutoSampleMessage = profile.MissingDigitTemplates.Count == 0
                ? $"Auto sample {normalized} {learnedMessage}. Profile learned all 10 digits."
                : $"Auto sample {normalized} {learnedMessage}. Missing {string.Join(",", profile.MissingDigitTemplates)}.";

            UpdateFullProfileOcrComparison(
                profile,
                bitmap,
                normalized,
                coordinateOcrSettings.CoordinateTemplateAutoProfileValidationMaxDigitScore);

            profile.UpdatedAtUtc = now;

            SaveProfileLocked(profile);

            if (profile.DigitTemplates.Values.Any(x => x.Count > 0))
            {
                _failedReadCount = 0;
                _needsRecalibration = false;
                _lastFailureReason = null;
                _updatedAtUtc = now;
            }

            return BuildProfileStatusLocked(
                profile,
                coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
        }
    }

    public CoordinateTemplateReadAttempt TryRead(Bitmap bitmap, CoordinateOcrSettingsResponse settings)
    {
        if (settings.CoordinateTemplateRequireVisibleTextForFailure &&
            !MayContainCoordinateText(bitmap, settings))
        {
            return new CoordinateTemplateReadAttempt(
                Success: false,
                RawText: null,
                Parsed: null,
                Reason: "coordinate text not visible",
                NeedsRecalibration: false);
        }

        var profile = LoadProfile();

        if (profile is null)
        {
            var missingStatus = MaybeCountFailedFastRead(
                settings,
                "coordinate text visible but fast template profile is missing");

            return new CoordinateTemplateReadAttempt(
                Success: false,
                RawText: null,
                Parsed: null,
                Reason: missingStatus.LastFailureReason ?? "fast template profile is missing",
                NeedsRecalibration: missingStatus.NeedsRecalibration);
        }

        if (BuildMissingDigits(profile.DigitTemplates).Count > 0)
        {
            var incompleteStatus = MaybeCountFailedFastRead(
                settings,
                "fast template profile is incomplete");

            return new CoordinateTemplateReadAttempt(
                Success: false,
                RawText: null,
                Parsed: null,
                Reason: incompleteStatus.LastFailureReason ?? "fast template profile is incomplete",
                NeedsRecalibration: incompleteStatus.NeedsRecalibration);
        }

        var read = TryReadRuntimeCoordinate(bitmap, profile);
        if (read is not null)
        {
            lock (_gate)
            {
                _failedReadCount = 0;
                _needsRecalibration = false;
                _lastFailureReason = null;
                _updatedAtUtc = DateTime.UtcNow;
            }

            return new CoordinateTemplateReadAttempt(
                Success: true,
                RawText: read.RawText,
                Parsed: read.Parsed,
                Reason: "fast template OCR succeeded",
                NeedsRecalibration: false);
        }

        var status = MaybeCountFailedFastRead(
            settings,
            "coordinate text visible but fast template OCR could not parse a coordinate");

        return new CoordinateTemplateReadAttempt(
            Success: false,
            RawText: null,
            Parsed: null,
            Reason: status.LastFailureReason ?? "fast template OCR failed",
            NeedsRecalibration: status.NeedsRecalibration);
    }

    public void ResetFailures()
    {
        lock (_gate)
        {
            _failedReadCount = 0;
            _needsRecalibration = false;
            _lastFailureReason = null;
            _updatedAtUtc = DateTime.UtcNow;
        }
    }

    public CoordinateTemplateOcrStatus MaybeCountFailedFastRead(
        CoordinateOcrSettingsResponse settings,
        string reason)
    {
        lock (_gate)
        {
            _lastFailureReason = settings.CoordinateTemplateCountFailedReadsForRecalibration
                ? reason
                : $"{reason}; failure counting disabled";

            if (settings.CoordinateTemplateCountFailedReadsForRecalibration)
            {
                _failedReadCount++;

                if (_failedReadCount >= settings.CoordinateTemplateRecalibrationFailureLimit)
                    _needsRecalibration = true;
            }

            _updatedAtUtc = DateTime.UtcNow;
            return BuildStatusLocked();
        }
    }

    private CoordinateTemplateOcrStatus BuildStatusLocked()
        => new(
            FailedReadCount: _failedReadCount,
            NeedsRecalibration: _needsRecalibration,
            LastFailureReason: _lastFailureReason,
            UpdatedAtUtc: _updatedAtUtc);

    private CoordinateTemplateProfileStatus BuildProfileStatusLocked(bool autoProfileEnabled = false)
        => BuildProfileStatusLocked(LoadProfileLocked(), autoProfileEnabled);

    private CoordinateTemplateProfileStatus BuildProfileStatusLocked(
        CoordinateTemplateProfile? profile,
        bool autoProfileEnabled = false)
    {
        var templateCount = profile?.DigitTemplates.Values.Sum(x => x.Count) ?? 0;
        var learnedDigits = profile?.DigitTemplates
            .Where(x => x.Value.Count > 0)
            .Select(x => x.Key)
            .OrderBy(x => x)
            .ToArray() ?? Array.Empty<string>();

        return new CoordinateTemplateProfileStatus(
            ProfileReady: profile is not null && BuildMissingDigits(profile.DigitTemplates).Count == 0,
            ProfileId: profile?.ProfileId,
            LearnedDigits: learnedDigits,
            MissingDigitTemplates: profile?.MissingDigitTemplates.ToArray() ?? Array.Empty<string>(),
            TemplateCount: templateCount,
            SampleCount: profile?.SampleCount ?? 0,
            LastAutoSampleCoordinate: profile?.LastAutoSampleCoordinate,
            LastAutoSampleMessage: profile?.LastAutoSampleMessage,
            AutoProfileEnabled: autoProfileEnabled,
            LastValidatedDigits: profile?.LastValidatedDigits.ToArray() ?? Array.Empty<string>(),
            LastLearnedDigits: profile?.LastLearnedDigits.ToArray() ?? Array.Empty<string>(),
            LastRejectedDigits: profile?.LastRejectedDigits.ToArray() ?? Array.Empty<string>(),
            LastValidationMessage: profile?.LastValidationMessage,
            LastSampleAccepted: profile?.LastSampleAccepted ?? false,
            LastOcrComparisonText: profile?.LastOcrComparisonText,
            LastOcrComparisonMessage: profile?.LastOcrComparisonMessage,
            LastOcrComparisonMatched: profile?.LastOcrComparisonMatched ?? false,
            LastSegmentationMode: profile?.LastSegmentationMode,
            LastLowQualityDigits: profile?.LastLowQualityDigits.ToArray() ?? Array.Empty<string>(),
            LastDigitOcrValidatedDigits: profile?.LastDigitOcrValidatedDigits.ToArray() ?? Array.Empty<string>(),
            LastDigitOcrRejectedDigits: profile?.LastDigitOcrRejectedDigits.ToArray() ?? Array.Empty<string>(),
            LastDigitOcrValidationMessage: profile?.LastDigitOcrValidationMessage,
            LastCalibrationMessage: profile?.LastCalibrationMessage,
            CreatedAtUtc: profile?.CreatedAtUtc,
            UpdatedAtUtc: profile?.UpdatedAtUtc,
            Runtime: BuildStatusLocked());
    }

    private CoordinateTemplateProfile? LoadProfile()
    {
        lock (_gate)
            return LoadProfileLocked();
    }

    private CoordinateTemplateProfile? LoadProfileLocked()
    {
        try
        {
            if (!File.Exists(_profilePath))
                return null;

            var profile = JsonSerializer.Deserialize<CoordinateTemplateProfile>(
                File.ReadAllText(_profilePath),
                JsonOptions);

            if (profile is not null)
                profile.MissingDigitTemplates = BuildMissingDigits(profile.DigitTemplates);

            return profile;
        }
        catch
        {
            return null;
        }
    }

    private void SaveProfileLocked(CoordinateTemplateProfile profile)
    {
        profile.MissingDigitTemplates = BuildMissingDigits(profile.DigitTemplates);

        var folder = Path.GetDirectoryName(_profilePath);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var tempPath = $"{_profilePath}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(profile, JsonOptions));
        File.Move(tempPath, _profilePath, overwrite: true);
    }

    private static string ValidateAndNormalizeCoordinate(string? value, int worldWidth, int worldHeight)
    {
        var match = CoordinateRegex.Match(value ?? string.Empty);

        if (!match.Success)
            throw new InvalidOperationException("Coordinate must look like 12345,6789.");

        var x = int.Parse(match.Groups["x"].Value);
        var y = int.Parse(match.Groups["y"].Value);

        if (x < 0 || x > worldWidth)
            throw new InvalidOperationException($"Longitude must be between 0 and {worldWidth}.");

        if (y < 0 || y > worldHeight)
            throw new InvalidOperationException($"Latitude must be between 0 and {worldHeight}.");

        return $"{x},{y}";
    }

    private static TemplateBuildResult BuildTemplates(
        Bitmap bitmap,
        string coordinate,
        int threshold)
    {
        var segmentation = SegmentGlyphs(bitmap, coordinate, threshold);
        var glyphs = segmentation.Glyphs
            .Where(x => char.IsDigit(x.Character))
            .ToList();

        if (glyphs.Count == 0)
            throw new InvalidOperationException("No digit pixels were found in the coordinate crop.");

        var templates = new Dictionary<string, List<CoordinateDigitTemplate>>(StringComparer.Ordinal);

        foreach (var glyph in glyphs)
        {
            var digit = glyph.Character.ToString();

            if (!templates.TryGetValue(digit, out var list))
            {
                list = new List<CoordinateDigitTemplate>();
                templates[digit] = list;
            }

            list.Add(new CoordinateDigitTemplate
            {
                Digit = digit,
                Width = glyph.Width,
                Height = glyph.Height,
                Pixels = glyph.Pixels,
                Side = glyph.Side,
                DistanceFromSeparator = glyph.DistanceFromSeparator,
                TouchesCropEdge = glyph.TouchesCropEdge,
                QualityScore = glyph.QualityScore,
                SourceX = glyph.SourceX,
                SourceY = glyph.SourceY
            });
        }

        return new TemplateBuildResult(
            Templates: templates,
            Mode: segmentation.Mode,
            LowQualityDigits: segmentation.LowQualityDigits);
    }

    private static SegmentationResult SegmentGlyphs(Bitmap bitmap, string coordinate, int threshold)
    {
        // Best for UWO-style coordinate text:
        // the character cells usually use fixed advance/spacing,
        // but the actual ink width differs. "1" is narrow while "8" is wide.
        // Splitting by ink width can shift every following digit.
        var fixedAdvance = TrySegmentByFixedAdvanceSlots(bitmap, coordinate, threshold);
        if (fixedAdvance is not null)
            return fixedAdvance;

        var adaptive = TrySegmentByAdaptiveTextCuts(bitmap, coordinate, threshold);
        if (adaptive is not null)
            return adaptive;

        var expected = coordinate.ToCharArray();
        var runs = FindColumnRuns(bitmap, threshold);

        if (runs.Count != expected.Length)
            runs = BuildEvenRuns(bitmap.Width, expected.Length);

        var glyphs = new List<TemplateGlyph>();

        for (var i = 0; i < expected.Length; i++)
        {
            if (!char.IsDigit(expected[i]))
                continue;

            var (left, right) = runs[i];
            var touches = left <= 0 || right >= bitmap.Width - 1;

            var glyph = ExtractGlyph(
                bitmap,
                expected[i],
                left,
                right,
                threshold,
                "fallback",
                i,
                touches,
                QualityFor(i, touches, fallback: true));

            if (glyph.Width > 0 && glyph.Height > 0)
                glyphs.Add(glyph);
        }

        return new SegmentationResult(
            Glyphs: glyphs,
            Mode: "Fallback",
            LowQualityDigits: glyphs
                .Select(x => x.Character.ToString())
                .Distinct()
                .OrderBy(x => x)
                .ToArray());
    }

    private static SegmentationResult? TrySegmentByFixedAdvanceSlots(
        Bitmap bitmap,
        string coordinate,
        int threshold)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            return null;

        if (string.IsNullOrWhiteSpace(coordinate))
            return null;

        var expected = coordinate.ToCharArray();
        var charCount = expected.Length;

        if (charCount < 2)
            return null;

        var bounds = TryFindInkBounds(bitmap, threshold);
        if (bounds is null)
            return null;

        var textBounds = bounds.Value;
        var runs = FindColumnRuns(bitmap, threshold);

        var runCenters = runs
            .Select(run => (run.Left + run.Right) / 2.0)
            .OrderBy(x => x)
            .ToList();

        var widestRun = runs.Count == 0
            ? 1
            : runs.Max(run => Math.Max(1, run.Right - run.Left + 1));

        var advance = EstimateFixedAdvance(
            runCenters,
            textBounds.Width,
            charCount,
            widestRun);

        if (advance <= 0)
            return null;

        var firstCenter = EstimateFirstCellCenter(
            runCenters,
            textBounds,
            advance,
            charCount);

        var top = Math.Max(0, textBounds.Top - 1);
        var bottom = Math.Min(bitmap.Height - 1, textBounds.Bottom);

        if (bottom <= top)
            return null;

        var separatorIndex = coordinate.IndexOf(',');
        var glyphs = new List<TemplateGlyph>();

        for (var i = 0; i < expected.Length; i++)
        {
            var c = expected[i];

            if (!char.IsDigit(c))
                continue;

            var center = firstCenter + (i * advance);
            var left = (int)Math.Floor(center - (advance / 2.0));
            var right = (int)Math.Ceiling(center + (advance / 2.0)) - 1;

            left = Math.Clamp(left, 0, bitmap.Width - 1);
            right = Math.Clamp(right, left, bitmap.Width - 1);

            // Keep the fixed horizontal cell width.
            // Do NOT trim "1" to its narrow ink width, otherwise all following
            // slots drift and per-digit OCR/template validation rejects samples.
            var side = "fixed-advance";
            var distanceFromSeparator = i;

            if (separatorIndex >= 0)
            {
                if (i < separatorIndex)
                {
                    side = "left";
                    distanceFromSeparator = separatorIndex - i - 1;
                }
                else if (i > separatorIndex)
                {
                    side = "right";
                    distanceFromSeparator = i - separatorIndex - 1;
                }
            }

            var touchesCropEdge =
                left <= 0 ||
                right >= bitmap.Width - 1 ||
                top <= 0 ||
                bottom >= bitmap.Height - 1;

            var glyph = ExtractFixedSlotGlyph(
                bitmap,
                c,
                left,
                right,
                top,
                bottom,
                threshold,
                side,
                distanceFromSeparator,
                touchesCropEdge,
                QualityFor(distanceFromSeparator, touchesCropEdge, fallback: false));

            if (glyph.Width > 0 && glyph.Height > 0)
                glyphs.Add(glyph);
        }

        if (glyphs.Count == 0)
            return null;

        return new SegmentationResult(
            Glyphs: glyphs,
            Mode: "FixedAdvanceSlots",
            LowQualityDigits: glyphs
                .Where(x => x.QualityScore < 70)
                .Select(x => x.Character.ToString())
                .Distinct()
                .OrderBy(x => x)
                .ToArray());
    }

    private static double EstimateFixedAdvance(
        IReadOnlyList<double> runCenters,
        int textBoundsWidth,
        int charCount,
        int widestRun)
    {
        if (runCenters.Count >= 2)
        {
            var deltas = new List<double>();

            for (var i = 1; i < runCenters.Count; i++)
            {
                var delta = runCenters[i] - runCenters[i - 1];

                if (delta >= 2)
                    deltas.Add(delta);
            }

            if (deltas.Count > 0)
            {
                deltas.Sort();
                var medianDelta = deltas[deltas.Count / 2];

                // Cell advance must be at least wide enough for the widest glyph.
                return Math.Max(widestRun + 1, medianDelta);
            }
        }

        var fromBounds = textBoundsWidth / (double)Math.Max(1, charCount);

        return Math.Max(widestRun + 1, fromBounds);
    }

    private static double EstimateFirstCellCenter(
        IReadOnlyList<double> runCenters,
        Rectangle textBounds,
        double advance,
        int charCount)
    {
        if (runCenters.Count >= Math.Min(charCount, 3))
        {
            var candidates = new List<double>();

            var count = Math.Min(runCenters.Count, charCount);
            for (var i = 0; i < count; i++)
                candidates.Add(runCenters[i] - (i * advance));

            candidates.Sort();
            return candidates[candidates.Count / 2];
        }

        return textBounds.Left + (advance / 2.0);
    }

    private static TemplateGlyph ExtractFixedSlotGlyph(
        Bitmap bitmap,
        char character,
        int left,
        int right,
        int top,
        int bottom,
        int threshold,
        string side,
        int distanceFromSeparator,
        bool touchesCropEdge,
        double qualityScore)
    {
        left = Math.Clamp(left, 0, Math.Max(0, bitmap.Width - 1));
        right = Math.Clamp(right, left, Math.Max(0, bitmap.Width - 1));
        top = Math.Clamp(top, 0, Math.Max(0, bitmap.Height - 1));
        bottom = Math.Clamp(bottom, top, Math.Max(0, bitmap.Height - 1));

        var width = Math.Max(1, right - left + 1);
        var height = Math.Max(1, bottom - top + 1);
        var pixels = new string[width * height];

        var index = 0;
        var inkCount = 0;

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var ink = Gray(bitmap.GetPixel(x, y)) >= threshold;
                if (ink)
                    inkCount++;

                pixels[index++] = ink ? InkPixel : BackgroundPixel;
            }
        }

        if (inkCount == 0)
        {
            return new TemplateGlyph(
                Character: character,
                Width: 0,
                Height: 0,
                Pixels: Array.Empty<string>(),
                Side: side,
                DistanceFromSeparator: distanceFromSeparator,
                TouchesCropEdge: touchesCropEdge,
                QualityScore: 0,
                SourceX: left,
                SourceY: top);
        }

        return new TemplateGlyph(
            Character: character,
            Width: width,
            Height: height,
            Pixels: pixels,
            Side: side,
            DistanceFromSeparator: distanceFromSeparator,
            TouchesCropEdge: touchesCropEdge,
            QualityScore: qualityScore,
            SourceX: left,
            SourceY: top);
    }

    private static SegmentationResult? TrySegmentByAdaptiveTextCuts(
        Bitmap bitmap,
        string coordinate,
        int threshold)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            return null;

        if (string.IsNullOrWhiteSpace(coordinate))
            return null;

        var expected = coordinate.ToCharArray();
        var charCount = expected.Length;

        if (charCount < 2)
            return null;

        var textBounds = TryFindInkBounds(bitmap, threshold);
        if (textBounds is null)
            return null;

        var bounds = textBounds.Value;

        if (bounds.Width < charCount)
            return null;

        var cuts = BuildAdaptiveCharacterCuts(bitmap, bounds, charCount, threshold);
        if (cuts is null)
            return null;

        var separatorIndex = coordinate.IndexOf(',');
        var glyphs = new List<TemplateGlyph>();

        for (var i = 0; i < expected.Length; i++)
        {
            var c = expected[i];

            if (!char.IsDigit(c))
                continue;

            var left = cuts[i];
            var right = cuts[i + 1] - 1;

            if (right < left)
                continue;

            left = Math.Clamp(left, 0, bitmap.Width - 1);
            right = Math.Clamp(right, left, bitmap.Width - 1);

            var trimmed = TryTrimSlotToInk(bitmap, left, right, bounds.Top, bounds.Bottom - 1, threshold);
            if (trimmed is null)
                continue;

            left = trimmed.Value.Left;
            right = trimmed.Value.Right;

            var side = "adaptive";
            var distanceFromSeparator = i;

            if (separatorIndex >= 0)
            {
                if (i < separatorIndex)
                {
                    side = "left";
                    distanceFromSeparator = separatorIndex - i - 1;
                }
                else if (i > separatorIndex)
                {
                    side = "right";
                    distanceFromSeparator = i - separatorIndex - 1;
                }
            }

            var touchesCropEdge =
                left <= 0 ||
                right >= bitmap.Width - 1 ||
                bounds.Top <= 0 ||
                bounds.Bottom >= bitmap.Height;

            var glyph = ExtractGlyph(
                bitmap,
                c,
                left,
                right,
                threshold,
                side,
                distanceFromSeparator,
                touchesCropEdge,
                QualityFor(distanceFromSeparator, touchesCropEdge, fallback: false));

            if (glyph.Width > 0 && glyph.Height > 0)
                glyphs.Add(glyph);
        }

        if (glyphs.Count == 0)
            return null;

        return new SegmentationResult(
            Glyphs: glyphs,
            Mode: "AdaptiveTextBounds",
            LowQualityDigits: glyphs
                .Where(x => x.QualityScore < 70)
                .Select(x => x.Character.ToString())
                .Distinct()
                .OrderBy(x => x)
                .ToArray());
    }

    private static Rectangle? TryFindInkBounds(Bitmap bitmap, int threshold)
    {
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (Gray(bitmap.GetPixel(x, y)) < threshold)
                    continue;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
            return null;

        minX = Math.Max(0, minX - 1);
        minY = Math.Max(0, minY - 1);
        maxX = Math.Min(bitmap.Width - 1, maxX + 1);
        maxY = Math.Min(bitmap.Height - 1, maxY + 1);

        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static int[]? BuildAdaptiveCharacterCuts(
        Bitmap bitmap,
        Rectangle bounds,
        int charCount,
        int threshold)
    {
        var cuts = new int[charCount + 1];

        cuts[0] = bounds.Left;
        cuts[charCount] = bounds.Right;

        var averageCharWidth = bounds.Width / (double)charCount;
        if (averageCharWidth < 1)
            return null;

        var minimumCharWidth = Math.Max(1, (int)Math.Floor(averageCharWidth * 0.35));

        for (var i = 1; i < charCount; i++)
        {
            var target = bounds.Left + averageCharWidth * i;
            var radius = Math.Clamp(
                (int)Math.Round(averageCharWidth * 0.75),
                2,
                Math.Max(2, bounds.Width / 3));

            var minX = Math.Max(
                cuts[i - 1] + minimumCharWidth,
                (int)Math.Floor(target - radius));

            var maxX = Math.Min(
                bounds.Right - ((charCount - i) * minimumCharWidth),
                (int)Math.Ceiling(target + radius));

            if (minX > maxX)
                return null;

            cuts[i] = FindLowestInkCutColumn(
                bitmap,
                bounds,
                minX,
                maxX,
                target,
                threshold);
        }

        for (var i = 1; i < cuts.Length; i++)
        {
            if (cuts[i] <= cuts[i - 1])
                return null;
        }

        return cuts;
    }

    private static int FindLowestInkCutColumn(
        Bitmap bitmap,
        Rectangle bounds,
        int minX,
        int maxX,
        double target,
        int threshold)
    {
        var bestX = minX;
        var bestScore = double.PositiveInfinity;

        for (var x = minX; x <= maxX; x++)
        {
            var ink = 0;

            for (var y = bounds.Top; y < bounds.Bottom; y++)
            {
                if (x - 1 >= 0 && x - 1 < bitmap.Width &&
                    Gray(bitmap.GetPixel(x - 1, y)) >= threshold)
                {
                    ink++;
                }

                if (x >= 0 && x < bitmap.Width &&
                    Gray(bitmap.GetPixel(x, y)) >= threshold)
                {
                    ink++;
                }
            }

            var distancePenalty = Math.Abs(x - target) * 0.10;
            var score = ink + distancePenalty;

            if (score < bestScore)
            {
                bestScore = score;
                bestX = x;
            }
        }

        return bestX;
    }

    private static (int Left, int Right)? TryTrimSlotToInk(
        Bitmap bitmap,
        int left,
        int right,
        int top,
        int bottom,
        int threshold)
    {
        var minX = right + 1;
        var maxX = left - 1;

        for (var y = top; y <= bottom && y < bitmap.Height; y++)
        {
            if (y < 0)
                continue;

            for (var x = left; x <= right && x < bitmap.Width; x++)
            {
                if (x < 0)
                    continue;

                if (Gray(bitmap.GetPixel(x, y)) < threshold)
                    continue;

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }
        }

        if (maxX < minX)
            return null;

        minX = Math.Max(0, minX - 1);
        maxX = Math.Min(bitmap.Width - 1, maxX + 1);

        return (minX, maxX);
    }

    private static DigitOcrTemplateFilterResult ValidateAndFilterTemplatesWithDigitOcr(
        Bitmap source,
        IReadOnlyDictionary<string, List<CoordinateDigitTemplate>> sampleTemplates,
        CoordinateOcrSettingsResponse settings,
        Func<Bitmap, string?>? perDigitOcrReader)
    {
        if (!settings.CoordinateTemplateRequirePerDigitOcrValidation)
        {
            return new DigitOcrTemplateFilterResult(
                Accepted: true,
                ValidatedTemplates: sampleTemplates,
                ValidatedDigits: Array.Empty<string>(),
                RejectedDigits: Array.Empty<string>(),
                Message: "Per-digit OCR validation disabled.");
        }

        if (perDigitOcrReader is null)
        {
            return new DigitOcrTemplateFilterResult(
                Accepted: false,
                ValidatedTemplates: new Dictionary<string, List<CoordinateDigitTemplate>>(StringComparer.Ordinal),
                ValidatedDigits: Array.Empty<string>(),
                RejectedDigits: sampleTemplates.Keys.OrderBy(x => x).ToArray(),
                Message: "Per-digit OCR validation required, but no digit OCR reader was provided.");
        }

        var validatedTemplates = new Dictionary<string, List<CoordinateDigitTemplate>>(StringComparer.Ordinal);
        var validatedDigits = new SortedSet<string>(StringComparer.Ordinal);
        var rejectedDigits = new SortedSet<string>(StringComparer.Ordinal);
        var messages = new List<string>();

        foreach (var (expectedDigit, templates) in sampleTemplates.OrderBy(x => x.Key))
        {
            foreach (var template in templates)
            {
                using var crop = CropTemplate(source, template);

                var raw = perDigitOcrReader(crop);
                var actual = NormalizeSingleDigit(raw);

                if (actual == expectedDigit)
                {
                    if (!validatedTemplates.TryGetValue(expectedDigit, out var list))
                    {
                        list = new List<CoordinateDigitTemplate>();
                        validatedTemplates[expectedDigit] = list;
                    }

                    list.Add(template);
                    validatedDigits.Add(expectedDigit);
                }
                else
                {
                    rejectedDigits.Add(expectedDigit);
                    messages.Add($"{expectedDigit}->{(string.IsNullOrWhiteSpace(raw) ? "empty" : raw.Trim())}");
                }
            }
        }

        var accepted = validatedTemplates.Count > 0;

        return new DigitOcrTemplateFilterResult(
            Accepted: accepted,
            ValidatedTemplates: validatedTemplates,
            ValidatedDigits: validatedDigits.ToArray(),
            RejectedDigits: rejectedDigits.ToArray(),
            Message: accepted
                ? $"Per-digit OCR accepted {string.Join(",", validatedDigits)}; rejected {(rejectedDigits.Count == 0 ? "none" : string.Join(",", rejectedDigits))}."
                : $"Per-digit OCR rejected all digits ({string.Join("; ", messages)}).");
    }

    private static Bitmap CropTemplate(Bitmap source, CoordinateDigitTemplate template)
    {
        var x = Math.Clamp(template.SourceX, 0, Math.Max(0, source.Width - 1));
        var y = Math.Clamp(template.SourceY, 0, Math.Max(0, source.Height - 1));
        var width = Math.Clamp(template.Width, 1, source.Width - x);
        var height = Math.Clamp(template.Height, 1, source.Height - y);
        var crop = new Bitmap(width, height);

        using var graphics = Graphics.FromImage(crop);
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, width, height),
            new Rectangle(x, y, width, height),
            GraphicsUnit.Pixel);

        return crop;
    }

    private static string? NormalizeSingleDigit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var digits = raw.Where(char.IsDigit).Distinct().ToArray();
        return digits.Length == 1 ? digits[0].ToString() : null;
    }

    private static void AddTemplateVariant(
        List<CoordinateDigitTemplate> existingTemplates,
        CoordinateDigitTemplate candidate,
        int maxTemplates)
    {
        // This app learns exactly one template per digit: 0-9.
        // No variants are added after a digit is already learned.
        if (existingTemplates.Count == 0)
            existingTemplates.Add(candidate);
    }

    private static Dictionary<string, List<CoordinateDigitTemplate>> KeepSingleTemplatePerDigit(
        IReadOnlyDictionary<string, List<CoordinateDigitTemplate>> templates)
    {
        var result = new Dictionary<string, List<CoordinateDigitTemplate>>(StringComparer.Ordinal);

        foreach (var (digit, digitTemplates) in templates)
        {
            var best = digitTemplates
                .OrderByDescending(x => x.QualityScore)
                .FirstOrDefault();

            if (best is not null)
                result[digit] = new List<CoordinateDigitTemplate> { best };
        }

        return result;
    }

    private static List<string> BuildMissingDigits(
        IReadOnlyDictionary<string, List<CoordinateDigitTemplate>> templates)
    {
        var seenDigits = templates
            .Where(x => x.Value.Count > 0)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);

        return Enumerable.Range(0, 10)
            .Select(x => x.ToString())
            .Where(x => !seenDigits.Contains(x))
            .ToList();
    }

    private static double BestTemplateScore(
        CoordinateDigitTemplate sample,
        IReadOnlyList<CoordinateDigitTemplate> templates)
    {
        if (templates.Count == 0)
            return double.PositiveInfinity;

        return templates
            .Select(template => CompareTemplate(sample, template))
            .Min();
    }

    private static double CompareTemplate(
        CoordinateDigitTemplate sample,
        CoordinateDigitTemplate learned)
    {
        if (sample.Width <= 0 || sample.Height <= 0 ||
            learned.Width <= 0 || learned.Height <= 0 ||
            sample.Pixels.Length == 0 || learned.Pixels.Length == 0)
        {
            return 1.0;
        }

        var width = Math.Max(sample.Width, learned.Width);
        var height = Math.Max(sample.Height, learned.Height);
        var mismatches = 0;
        var checkedPixels = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var a = GetScaledPixel(sample, x, y, width, height);
                var b = GetScaledPixel(learned, x, y, width, height);

                checkedPixels++;

                if (!string.Equals(a, b, StringComparison.Ordinal))
                    mismatches++;
            }
        }

        return checkedPixels == 0
            ? 1.0
            : mismatches / (double)checkedPixels;
    }

    private static string GetScaledPixel(
        CoordinateDigitTemplate template,
        int x,
        int y,
        int targetWidth,
        int targetHeight)
    {
        var sourceX = Math.Clamp(
            (int)Math.Floor(x * template.Width / (double)Math.Max(1, targetWidth)),
            0,
            Math.Max(0, template.Width - 1));

        var sourceY = Math.Clamp(
            (int)Math.Floor(y * template.Height / (double)Math.Max(1, targetHeight)),
            0,
            Math.Max(0, template.Height - 1));

        var index = sourceY * template.Width + sourceX;

        return index >= 0 && index < template.Pixels.Length
            ? template.Pixels[index]
            : BackgroundPixel;
    }

    private static void UpdateFullProfileOcrComparison(
        CoordinateTemplateProfile profile,
        Bitmap bitmap,
        string expectedCoordinate,
        double maxDigitScore)
    {
        if (profile.MissingDigitTemplates.Count > 0)
        {
            profile.LastOcrComparisonText = null;
            profile.LastOcrComparisonMatched = false;
            profile.LastOcrComparisonMessage =
                $"Profile not complete yet; missing {string.Join(",", profile.MissingDigitTemplates)}.";
            return;
        }

        try
        {
            var sampleTemplates = BuildTemplates(
                bitmap,
                expectedCoordinate,
                profile.BrightnessWhiteThreshold).Templates;

            var chars = new List<char>();
            var failed = new List<string>();

            foreach (var c in expectedCoordinate)
            {
                if (c == ',')
                {
                    chars.Add(',');
                    continue;
                }

                var digit = c.ToString();

                if (!sampleTemplates.TryGetValue(digit, out var candidates) ||
                    !profile.DigitTemplates.TryGetValue(digit, out var profileTemplates))
                {
                    failed.Add(digit);
                    chars.Add('?');
                    continue;
                }

                var best = candidates
                    .Select(candidate => BestTemplateScore(candidate, profileTemplates))
                    .DefaultIfEmpty(double.PositiveInfinity)
                    .Min();

                if (best > maxDigitScore)
                {
                    failed.Add($"{digit}:{best:F3}");
                    chars.Add('?');
                }
                else
                {
                    chars.Add(c);
                }
            }

            var readText = new string(chars.ToArray());
            profile.LastOcrComparisonText = readText;
            profile.LastOcrComparisonMatched =
                failed.Count == 0 &&
                readText.Equals(expectedCoordinate, StringComparison.Ordinal);

            profile.LastOcrComparisonMessage = profile.LastOcrComparisonMatched
                ? $"Full profile validated against normal OCR: {readText}."
                : $"Full profile mismatch. Normal OCR={expectedCoordinate}; template={readText}; failed={string.Join(",", failed)}.";
        }
        catch (Exception ex)
        {
            profile.LastOcrComparisonText = null;
            profile.LastOcrComparisonMatched = false;
            profile.LastOcrComparisonMessage = $"Full profile check failed: {ex.Message}";
        }
    }

    private RuntimeReadResult? TryReadRuntimeCoordinate(
        Bitmap bitmap,
        CoordinateTemplateProfile profile)
    {
        var runs = FindColumnRuns(bitmap, profile.BrightnessWhiteThreshold);

        if (runs.Count == 0)
            return null;

        var candidates = new List<(int Left, int Right, string? Digit, double Score, bool IsSeparator)>();

        foreach (var run in runs)
        {
            var stats = MeasureRun(bitmap, run.Left, run.Right, profile.BrightnessWhiteThreshold);
            var isLikelySeparator =
                stats.Height <= Math.Max(3, bitmap.Height / 2) &&
                stats.Top >= bitmap.Height / 3;

            if (isLikelySeparator)
            {
                candidates.Add((run.Left, run.Right, null, 0, true));
                continue;
            }

            var glyph = ExtractGlyph(
                bitmap,
                '?',
                run.Left,
                run.Right,
                profile.BrightnessWhiteThreshold,
                "runtime",
                0,
                run.Left <= 0 || run.Right >= bitmap.Width - 1,
                100);

            var digitMatch = MatchDigit(glyph, profile);
            if (digitMatch is null)
                return null;

            candidates.Add((run.Left, run.Right, digitMatch.Value.Digit, digitMatch.Value.Score, false));
        }

        if (candidates.Count(x => !x.IsSeparator) < 5)
            return null;

        var ordered = candidates.OrderBy(x => x.Left).ToList();

        string raw;
        var separatorIndex = ordered.FindIndex(x => x.IsSeparator);

        if (separatorIndex >= 0)
        {
            var chars = new List<string>();

            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].IsSeparator)
                {
                    if (chars.Count == 0 || chars[^1] == ",")
                        continue;

                    chars.Add(",");
                }
                else if (ordered[i].Digit is not null)
                {
                    chars.Add(ordered[i].Digit!);
                }
            }

            raw = string.Concat(chars);
        }
        else
        {
            var digits = string.Concat(ordered
                .Where(x => !x.IsSeparator && x.Digit is not null)
                .Select(x => x.Digit));

            if (digits.Length <= 4)
                return null;

            raw = $"{digits[..^4]},{digits[^4..]}";
        }

        var match = CoordinateRegex.Match(raw);
        if (!match.Success)
            return null;

        var x = int.Parse(match.Groups["x"].Value);
        var y = int.Parse(match.Groups["y"].Value);

        return new RuntimeReadResult(
            RawText: raw,
            Parsed: new ParsedCoordinate(x, y, raw));
    }

    private static (string Digit, double Score)? MatchDigit(
        TemplateGlyph glyph,
        CoordinateTemplateProfile profile)
    {
        var sample = new CoordinateDigitTemplate
        {
            Digit = "",
            Width = glyph.Width,
            Height = glyph.Height,
            Pixels = glyph.Pixels,
            Side = glyph.Side,
            DistanceFromSeparator = glyph.DistanceFromSeparator,
            TouchesCropEdge = glyph.TouchesCropEdge,
            QualityScore = glyph.QualityScore,
            SourceX = glyph.SourceX,
            SourceY = glyph.SourceY
        };

        string? bestDigit = null;
        var bestScore = double.PositiveInfinity;

        foreach (var (digit, templates) in profile.DigitTemplates)
        {
            if (templates.Count == 0)
                continue;

            var score = BestTemplateScore(sample, templates);

            if (score < bestScore)
            {
                bestScore = score;
                bestDigit = digit;
            }
        }

        return bestDigit is null
            ? null
            : (bestDigit, bestScore);
    }

    private static List<(int Left, int Right)> FindColumnRuns(Bitmap bitmap, int threshold)
    {
        var runs = new List<(int Left, int Right)>();
        var inRun = false;
        var start = 0;

        for (var x = 0; x < bitmap.Width; x++)
        {
            var hasInk = false;

            for (var y = 0; y < bitmap.Height; y++)
            {
                if (Gray(bitmap.GetPixel(x, y)) >= threshold)
                {
                    hasInk = true;
                    break;
                }
            }

            if (hasInk && !inRun)
            {
                inRun = true;
                start = x;
            }
            else if (!hasInk && inRun)
            {
                runs.Add((start, x - 1));
                inRun = false;
            }
        }

        if (inRun)
            runs.Add((start, bitmap.Width - 1));

        return runs;
    }

    private static List<(int Left, int Right)> BuildEvenRuns(int width, int count)
    {
        var runs = new List<(int Left, int Right)>();

        if (count <= 0)
            return runs;

        for (var i = 0; i < count; i++)
        {
            var left = (int)Math.Round(i * width / (double)count);
            var right = (int)Math.Round((i + 1) * width / (double)count) - 1;
            runs.Add((Math.Max(0, left), Math.Max(left, right)));
        }

        return runs;
    }

    private static TemplateGlyph ExtractGlyph(
        Bitmap bitmap,
        char character,
        int left,
        int right,
        int threshold,
        string side,
        int distanceFromSeparator,
        bool touchesCropEdge,
        double qualityScore)
    {
        left = Math.Clamp(left, 0, Math.Max(0, bitmap.Width - 1));
        right = Math.Clamp(right, left, Math.Max(0, bitmap.Width - 1));

        var top = bitmap.Height;
        var bottom = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (Gray(bitmap.GetPixel(x, y)) >= threshold)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if (bottom < top)
        {
            return new TemplateGlyph(
                Character: character,
                Width: 0,
                Height: 0,
                Pixels: Array.Empty<string>(),
                Side: side,
                DistanceFromSeparator: distanceFromSeparator,
                TouchesCropEdge: touchesCropEdge,
                QualityScore: 0,
                SourceX: left,
                SourceY: 0);
        }

        top = Math.Max(0, top - 1);
        bottom = Math.Min(bitmap.Height - 1, bottom + 1);

        var width = Math.Max(1, right - left + 1);
        var height = Math.Max(1, bottom - top + 1);
        var pixels = new string[width * height];

        var index = 0;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                pixels[index++] = Gray(bitmap.GetPixel(x, y)) >= threshold
                    ? InkPixel
                    : BackgroundPixel;
            }
        }

        return new TemplateGlyph(
            Character: character,
            Width: width,
            Height: height,
            Pixels: pixels,
            Side: side,
            DistanceFromSeparator: distanceFromSeparator,
            TouchesCropEdge: touchesCropEdge,
            QualityScore: qualityScore,
            SourceX: left,
            SourceY: top);
    }

    private static (int Top, int Bottom, int Height, int Ink) MeasureRun(
        Bitmap bitmap,
        int left,
        int right,
        int threshold)
    {
        var top = bitmap.Height;
        var bottom = -1;
        var ink = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = left; x <= right && x < bitmap.Width; x++)
            {
                if (Gray(bitmap.GetPixel(x, y)) < threshold)
                    continue;

                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                ink++;
            }
        }

        var height = bottom >= top
            ? bottom - top + 1
            : 0;

        return (top, bottom, height, ink);
    }

    private static double QualityFor(int distanceFromSeparator, bool touchesCropEdge, bool fallback)
    {
        var quality = 100.0 - (distanceFromSeparator * 10.0);

        if (touchesCropEdge)
            quality -= 40;

        if (fallback)
            quality -= 35;

        return Math.Clamp(quality, 0, 100);
    }

    private static bool MayContainCoordinateText(
        Bitmap bitmap,
        CoordinateOcrSettingsResponse settings)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            return false;

        var min = 255;
        var max = 0;
        var ink = 0;
        var total = bitmap.Width * bitmap.Height;
        var threshold = 180;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var gray = Gray(bitmap.GetPixel(x, y));

                min = Math.Min(min, gray);
                max = Math.Max(max, gray);

                if (gray >= threshold)
                    ink++;
            }
        }

        var contrast = max - min;
        var inkPercent = total == 0
            ? 0
            : ink * 100.0 / total;

        return contrast >= settings.CoordinateTemplateMinContrast &&
               inkPercent >= settings.CoordinateTemplateMinTextPixelsPercent;
    }

    private static int Gray(Color color)
        => (color.R + color.G + color.B) / 3;

    private static string ResolveProfilePath(string contentRootPath, IConfiguration configuration)
    {
        var configured = configuration["CoordinateTemplateProfilePath"];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(contentRootPath, configured);
        }

        return Path.Combine(contentRootPath, "Data", "coordinate-template-profile.json");
    }

    private sealed record TemplateBuildResult(
        IReadOnlyDictionary<string, List<CoordinateDigitTemplate>> Templates,
        string Mode,
        IReadOnlyList<string> LowQualityDigits);

    private sealed record SegmentationResult(
        IReadOnlyList<TemplateGlyph> Glyphs,
        string Mode,
        IReadOnlyList<string> LowQualityDigits);

    private sealed record TemplateGlyph(
        char Character,
        int Width,
        int Height,
        string[] Pixels,
        string Side,
        int DistanceFromSeparator,
        bool TouchesCropEdge,
        double QualityScore,
        int SourceX,
        int SourceY);

    private sealed record DigitOcrTemplateFilterResult(
        bool Accepted,
        IReadOnlyDictionary<string, List<CoordinateDigitTemplate>> ValidatedTemplates,
        IReadOnlyList<string> ValidatedDigits,
        IReadOnlyList<string> RejectedDigits,
        string Message);

    private sealed record RuntimeReadResult(
        string RawText,
        ParsedCoordinate Parsed);
}
