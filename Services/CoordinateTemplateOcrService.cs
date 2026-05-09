using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using OcrTradingBackend.Models;

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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Regex CoordinateRegex = new(@"^\s*(?<x>\d{1,5})\s*,\s*(?<y>\d{1,4})\s*$", RegexOptions.Compiled);

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
        var templates = build.Templates;
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
            LastAutoSampleCoordinate = existing?.LastAutoSampleCoordinate,
            LastAutoSampleMessage = existing?.LastAutoSampleMessage,
            LastSegmentationMode = build.Mode,
            LastLowQualityDigits = build.LowQualityDigits.ToList(),
            LastCalibrationMessage = $"Profile created from {normalized}. Templates: {templates.Count}; missing: {(missing.Count == 0 ? "none" : string.Join(",", missing))}.",
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now
        };

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
            if (existing is not null &&
                existing.SampleCount >= coordinateOcrSettings.CoordinateTemplateAutoProfileMaxSamples)
            {
                existing.LastAutoSampleMessage = "Auto profile sample limit reached.";
                existing.UpdatedAtUtc = DateTime.UtcNow;
                SaveProfileLocked(existing);
                return BuildProfileStatusLocked(existing, coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
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

            var digitOcr = ValidateDigitsWithOcr(
                bitmap,
                sampleTemplates,
                coordinateOcrSettings,
                perDigitOcrReader);
            profile.LastDigitOcrValidatedDigits = digitOcr.ValidatedDigits.ToList();
            profile.LastDigitOcrRejectedDigits = digitOcr.RejectedDigits.ToList();
            profile.LastDigitOcrValidationMessage = digitOcr.Message;

            if (!digitOcr.Accepted)
            {
                profile.LastValidatedDigits = new List<string>();
                profile.LastLearnedDigits = new List<string>();
                profile.LastRejectedDigits = digitOcr.RejectedDigits.ToList();
                profile.LastSampleAccepted = false;
                profile.LastValidationMessage = digitOcr.Message;
                profile.LastAutoSampleMessage = digitOcr.Message;
                profile.UpdatedAtUtc = now;
                SaveProfileLocked(profile);
                return BuildProfileStatusLocked(profile, coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
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
                profile.LastValidationMessage = $"Auto sample {normalized} rejected: no previously learned digits were present to validate.";
                profile.LastAutoSampleMessage = profile.LastValidationMessage;
                profile.UpdatedAtUtc = now;
                SaveProfileLocked(profile);
                return BuildProfileStatusLocked(profile, coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
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
                profile.LastValidationMessage = $"Auto sample {normalized} rejected: validation failed for {string.Join(",", rejectedDigits)} ({string.Join("; ", validationMessages)}).";
                profile.LastAutoSampleMessage = profile.LastValidationMessage;
                profile.UpdatedAtUtc = now;
                SaveProfileLocked(profile);
                return BuildProfileStatusLocked(profile, coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
            }

            foreach (var (digit, templates) in sampleTemplates)
            {
                if (!profile.DigitTemplates.TryGetValue(digit, out var existingTemplates))
                {
                    existingTemplates = new List<CoordinateDigitTemplate>();
                    profile.DigitTemplates[digit] = existingTemplates;
                }

                var before = existingTemplates.Count;
                foreach (var template in templates.OrderByDescending(x => x.QualityScore))
                    AddTemplateVariant(existingTemplates, template, coordinateOcrSettings.CoordinateTemplateMaxTemplatesPerDigit);

                if (before == 0 && existingTemplates.Count > 0)
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
            profile.LastRejectedDigits = new List<string>();
            profile.LastSampleAccepted = true;
            profile.LastValidationMessage = validatedDigits.Count == 0
                ? $"Auto sample {normalized} accepted as first sample."
                : $"Auto sample {normalized} validated {string.Join(",", validatedDigits)}.";
            profile.LastAutoSampleMessage = learnedDigits.Count == 0
                ? $"Auto sample {normalized} accepted; no new digits learned."
                : $"Auto sample {normalized} learned {string.Join(",", learnedDigits)}.";
            UpdateFullProfileOcrComparison(
                profile,
                bitmap,
                normalized,
                coordinateOcrSettings.CoordinateTemplateAutoProfileValidationMaxDigitScore);
            profile.UpdatedAtUtc = now;

            SaveProfileLocked(profile);

            if (profile.DigitTemplates.Count > 0)
            {
                _failedReadCount = 0;
                _needsRecalibration = false;
                _lastFailureReason = null;
                _updatedAtUtc = now;
            }

            return BuildProfileStatusLocked(profile, coordinateOcrSettings.CoordinateTemplateAutoProfileEnabled);
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
        var failureReason = profile is null
            ? "coordinate text visible but fast template profile is missing"
            : "coordinate text visible but fast template runtime matching is not implemented yet";

        var status = MaybeCountFailedFastRead(
            settings,
            failureReason);

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
            ProfileReady: profile is not null && templateCount > 0,
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

            return JsonSerializer.Deserialize<CoordinateTemplateProfile>(
                File.ReadAllText(_profilePath),
                JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void SaveProfileLocked(CoordinateTemplateProfile profile)
    {
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

    private static SegmentationResult SegmentGlyphs(Bitmap bitmap, string coordinate, int threshold)
    {
        var expected = coordinate.ToCharArray();
        var separatorIndex = coordinate.IndexOf(',');

        if (separatorIndex > 0)
        {
            var centered = TrySegmentFromSeparator(bitmap, coordinate, separatorIndex, threshold);
            if (centered is not null)
                return centered;
        }

        return SegmentFallback(bitmap, expected, threshold);
    }

    private static SegmentationResult? TrySegmentFromSeparator(
        Bitmap bitmap,
        string coordinate,
        int separatorIndex,
        int threshold)
    {
        var runs = FindColumnRuns(bitmap, threshold);
        if (runs.Count < 3)
            return null;

        var separator = FindSeparatorRun(bitmap, runs, coordinate, separatorIndex);
        if (separator is null)
            return null;

        var leftDigits = coordinate[..separatorIndex].Where(char.IsDigit).ToArray();
        var rightDigits = coordinate[(separatorIndex + 1)..].Where(char.IsDigit).ToArray();
        var leftRuns = runs
            .Where(run => run.Right < separator.Value.Left)
            .OrderBy(run => run.Left)
            .ToList();
        var rightRuns = runs
            .Where(run => run.Left > separator.Value.Right)
            .OrderBy(run => run.Left)
            .ToList();

        if (leftRuns.Count != leftDigits.Length || rightRuns.Count != rightDigits.Length)
            return null;

        var glyphs = new List<TemplateGlyph>();

        var leftRunsFromSeparator = leftRuns.OrderByDescending(x => x.Right).ToList();
        var leftDigitsFromSeparator = leftDigits.Reverse().ToArray();
        for (var i = 0; i < leftDigitsFromSeparator.Length; i++)
        {
            var run = leftRunsFromSeparator[i];
            var touches = run.Left <= 0;
            glyphs.Add(ExtractGlyph(
                bitmap,
                leftDigitsFromSeparator[i],
                run.Left,
                run.Right,
                threshold,
                "left",
                i,
                touches,
                QualityFor(i, touches, fallback: false)));
        }

        for (var i = 0; i < rightDigits.Length; i++)
        {
            var run = rightRuns[i];
            var touches = run.Right >= bitmap.Width - 1;
            glyphs.Add(ExtractGlyph(
                bitmap,
                rightDigits[i],
                run.Left,
                run.Right,
                threshold,
                "right",
                i,
                touches,
                QualityFor(i, touches, fallback: false)));
        }

        var lowQualityDigits = glyphs
            .Where(x => x.QualityScore < 70)
            .Select(x => x.Character.ToString())
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        return new SegmentationResult(
            Glyphs: glyphs.Where(x => x.Width > 0 && x.Height > 0).ToArray(),
            Mode: "SeparatorCentered",
            LowQualityDigits: lowQualityDigits);
    }

    private static SegmentationResult SegmentFallback(Bitmap bitmap, char[] expected, int threshold)
    {
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

    private static DigitOcrValidationResult ValidateDigitsWithOcr(
        Bitmap source,
        IReadOnlyDictionary<string, List<CoordinateDigitTemplate>> sampleTemplates,
        CoordinateOcrSettingsResponse settings,
        Func<Bitmap, string?>? perDigitOcrReader)
    {
        if (!settings.CoordinateTemplateRequirePerDigitOcrValidation)
        {
            return new DigitOcrValidationResult(
                Accepted: true,
                ValidatedDigits: Array.Empty<string>(),
                RejectedDigits: Array.Empty<string>(),
                Message: "Per-digit OCR validation disabled.");
        }

        if (perDigitOcrReader is null)
        {
            return new DigitOcrValidationResult(
                Accepted: true,
                ValidatedDigits: Array.Empty<string>(),
                RejectedDigits: Array.Empty<string>(),
                Message: "Per-digit OCR validation unavailable; skipped.");
        }

        var validated = new SortedSet<string>(StringComparer.Ordinal);
        var rejected = new SortedSet<string>(StringComparer.Ordinal);
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
                    validated.Add(expectedDigit);
                }
                else
                {
                    rejected.Add(expectedDigit);
                    messages.Add($"{expectedDigit}->{(string.IsNullOrWhiteSpace(raw) ? "empty" : raw!.Trim())}");
                }
            }
        }

        return new DigitOcrValidationResult(
            Accepted: rejected.Count == 0,
            ValidatedDigits: validated.ToArray(),
            RejectedDigits: rejected.ToArray(),
            Message: rejected.Count == 0
                ? $"Per-digit OCR validated {string.Join(",", validated)}."
                : $"Per-digit OCR rejected {string.Join(",", rejected)} ({string.Join("; ", messages)}).");
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
        maxTemplates = Math.Max(1, maxTemplates);

        if (existingTemplates.Count < maxTemplates)
        {
            existingTemplates.Add(candidate);
            return;
        }

        var lowestQuality = existingTemplates
            .Select((template, index) => new { template.QualityScore, index })
            .OrderBy(x => x.QualityScore)
            .First();

        if (candidate.QualityScore > lowestQuality.QualityScore)
            existingTemplates[lowestQuality.index] = candidate;
    }

    private static (int Left, int Right)? FindSeparatorRun(
        Bitmap bitmap,
        IReadOnlyList<(int Left, int Right)> runs,
        string coordinate,
        int separatorIndex)
    {
        var expectedCenter = bitmap.Width * (separatorIndex + 0.5) / coordinate.Length;
        var candidates = runs
            .Select(run =>
            {
                var top = bitmap.Height;
                var bottom = -1;
                var ink = 0;

                for (var y = 0; y < bitmap.Height; y++)
                {
                    for (var x = run.Left; x <= run.Right && x < bitmap.Width; x++)
                    {
                        if (Gray(bitmap.GetPixel(x, y)) >= 180)
                        {
                            top = Math.Min(top, y);
                            bottom = Math.Max(bottom, y);
                            ink++;
                        }
                    }
                }

                var height = Math.Max(0, bottom - top + 1);
                var width = Math.Max(1, run.Right - run.Left + 1);
                var center = (run.Left + run.Right) / 2.0;
                var lowerHalfBonus = top >= bitmap.Height / 2 ? 0 : 1000;
                var smallBonus = height <= Math.Max(2, bitmap.Height / 2) ? 0 : 500;
                var distance = Math.Abs(center - expectedCenter);

                return new
                {
                    Run = run,
                    Score = distance + lowerHalfBonus + smallBonus + (ink == 0 ? 1000 : 0),
                    Distance = distance
                };
            })
            .OrderBy(x => x.Score)
            .ToList();

        var best = candidates.FirstOrDefault();
        return best is null || best.Distance > bitmap.Width * 0.35
            ? null
            : best.Run;
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
            profile.LastOcrComparisonMessage = $"Profile not complete yet; missing {string.Join(",", profile.MissingDigitTemplates)}.";
            return;
        }

        try
        {
            var sampleTemplates = BuildTemplates(bitmap, expectedCoordinate, profile.BrightnessWhiteThreshold).Templates;
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
            profile.LastOcrComparisonMatched = failed.Count == 0 &&
                                               readText.Equals(expectedCoordinate, StringComparison.Ordinal);
            profile.LastOcrComparisonMessage = profile.LastOcrComparisonMatched
                ? $"Full profile validated against normal OCR: {readText}."
                : $"Full profile mismatch. Normal OCR={expectedCoordinate}; template={readText}; failed={string.Join(",", failed)}.";
        }
        catch (Exception ex)
        {
            profile.LastOcrComparisonText = null;
            profile.LastOcrComparisonMatched = false;
            profile.LastOcrComparisonMessage = $"Full profile comparison failed: {ex.Message}";
        }
    }

    private static double CompareTemplate(
        CoordinateDigitTemplate sample,
        CoordinateDigitTemplate template)
    {
        var samplePixels = NormalizePixels(sample.Pixels, template.Width, template.Height);
        var templatePixels = NormalizePixels(template.Pixels, template.Width, template.Height);
        var total = Math.Max(1, template.Width * template.Height);
        var mismatch = 0.0;

        for (var y = 0; y < template.Height; y++)
        {
            for (var x = 0; x < template.Width; x++)
            {
                var expected = templatePixels[y][x];
                var actual = samplePixels[y][x];

                if (expected == actual)
                    continue;

                mismatch += HasMatchingNeighbor(samplePixels, x, y, expected)
                    ? 0.25
                    : 1.0;
            }
        }

        return mismatch / total;
    }

    private static string[] NormalizePixels(string[] pixels, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (pixels.Length == height && pixels.All(row => row.Length == width))
            return pixels;

        var sourceHeight = Math.Max(1, pixels.Length);
        var sourceWidth = Math.Max(1, pixels.FirstOrDefault()?.Length ?? 1);
        var result = new string[height];

        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp((int)Math.Round(y * (sourceHeight - 1) / (double)Math.Max(1, height - 1)), 0, sourceHeight - 1);
            var row = new char[width];

            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp((int)Math.Round(x * (sourceWidth - 1) / (double)Math.Max(1, width - 1)), 0, sourceWidth - 1);
                row[x] = sourceY < pixels.Length && sourceX < pixels[sourceY].Length
                    ? pixels[sourceY][sourceX]
                    : '0';
            }

            result[y] = new string(row);
        }

        return result;
    }

    private static bool HasMatchingNeighbor(string[] pixels, int x, int y, char expected)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                var nx = x + dx;
                var ny = y + dy;

                if (ny < 0 || ny >= pixels.Length || nx < 0 || nx >= pixels[ny].Length)
                    continue;

                if (pixels[ny][nx] == expected)
                    return true;
            }
        }

        return false;
    }

    private static List<(int Left, int Right)> FindColumnRuns(Bitmap bitmap, int threshold)
    {
        var runs = new List<(int Left, int Right)>();
        var inRun = false;
        var start = 0;
        var gap = 0;

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

            if (hasInk)
            {
                if (!inRun)
                {
                    start = x;
                    inRun = true;
                }

                gap = 0;
            }
            else if (inRun)
            {
                gap++;

                if (gap > 1)
                {
                    runs.Add((start, x - gap));
                    inRun = false;
                    gap = 0;
                }
            }
        }

        if (inRun)
            runs.Add((start, bitmap.Width - 1));

        return runs;
    }

    private static List<(int Left, int Right)> BuildEvenRuns(int width, int count)
    {
        var runs = new List<(int Left, int Right)>();

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
        var top = bitmap.Height;
        var bottom = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = left; x <= right && x < bitmap.Width; x++)
            {
                if (Gray(bitmap.GetPixel(x, y)) >= threshold)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if (bottom < top)
            return new TemplateGlyph(character, 0, 0, Array.Empty<string>(), side, distanceFromSeparator, touchesCropEdge, qualityScore, left, 0);

        var rows = new List<string>();

        for (var y = top; y <= bottom; y++)
        {
            var chars = new char[Math.Max(1, right - left + 1)];

            for (var x = left; x <= right && x < bitmap.Width; x++)
                chars[x - left] = Gray(bitmap.GetPixel(x, y)) >= threshold ? '1' : '0';

            rows.Add(new string(chars));
        }

        return new TemplateGlyph(
            character,
            right - left + 1,
            bottom - top + 1,
            rows.ToArray(),
            side,
            distanceFromSeparator,
            touchesCropEdge,
            qualityScore,
            left,
            top);
    }

    private static int Gray(Color pixel)
        => (int)((pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114));

    private static string ResolveProfilePath(string root, IConfiguration configuration)
    {
        var configured = configuration.GetValue<string>("OcrSettings:CoordinateTemplateProfilePath");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine("Data", "coordinate-template-profile.json")
            : configured;

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
    }

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

    private sealed record SegmentationResult(
        IReadOnlyList<TemplateGlyph> Glyphs,
        string Mode,
        IReadOnlyList<string> LowQualityDigits);

    private sealed record TemplateBuildResult(
        Dictionary<string, List<CoordinateDigitTemplate>> Templates,
        string Mode,
        IReadOnlyList<string> LowQualityDigits);

    private sealed record DigitOcrValidationResult(
        bool Accepted,
        IReadOnlyList<string> ValidatedDigits,
        IReadOnlyList<string> RejectedDigits,
        string Message);

    private static bool MayContainCoordinateText(
        Bitmap bitmap,
        CoordinateOcrSettingsResponse settings)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            return false;

        var minGray = 255;
        var maxGray = 0;
        var brightPixels = 0;
        var totalPixels = bitmap.Width * bitmap.Height;
        var brightThreshold = 180;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var gray = (int)((pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114));

                minGray = Math.Min(minGray, gray);
                maxGray = Math.Max(maxGray, gray);

                if (gray >= brightThreshold)
                    brightPixels++;
            }
        }

        var contrast = maxGray - minGray;
        var brightPercent = brightPixels * 100.0 / totalPixels;

        return contrast >= settings.CoordinateTemplateMinContrast &&
               brightPercent >= settings.CoordinateTemplateMinTextPixelsPercent;
    }
}
