using OcrTradingBackend.Models;
using Microsoft.Extensions.Options;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace OcrTradingBackend.Services;

public interface IPriceTradeTypeTemplateSettingsService
{
    PriceTradeTypeTemplateSettingsResponse Get();
    PriceTradeTypeTemplateSettingsResponse GetEffective(OcrRuntimeSettings defaults);
    Task<PriceTradeTypeTemplateSettingsResponse> UpdateAsync(
        UpdatePriceTradeTypeTemplateSettingsRequest request,
        CancellationToken ct);
}

public interface IPriceTradeTypeTemplateOcrService
{
    PriceTradeTypeTemplateProfileStatus GetProfileStatus(bool autoProfileEnabled = false);
    PriceTradeTypeTemplateReadAttempt TryRead(
        Bitmap bitmap,
        string expectedTradeType,
        PriceTradeTypeTemplateSettingsResponse settings);
    PriceTradeTypeTemplateProfileStatus AddProfileSampleFromNormalOcr(
        Bitmap bitmap,
        OcrLayoutBox sourceBox,
        string tradeType,
        PriceTradeTypeTemplateSettingsResponse settings,
        string? rawText = null);
    PriceTradeTypeTemplateProfileStatus MaybeCountFailedFastRead(
        PriceTradeTypeTemplateSettingsResponse settings,
        string reason);
    void ResetFailures();
    void DeleteProfile();
    void RecordAttempt(PriceTradeTypeTemplateAttemptLog entry);
}

public sealed class PriceTradeTypeTemplateSettingsService : IPriceTradeTypeTemplateSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IOptionsMonitor<OcrRuntimeSettings> _defaults;
    private readonly string _path;
    private readonly object _gate = new();

    public PriceTradeTypeTemplateSettingsService(
        IOptionsMonitor<OcrRuntimeSettings> defaults,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _defaults = defaults;

        var configured = configuration.GetValue<string>("OcrSettings:PriceTradeTypeTemplateSettingsPath");
        _path = ResolvePath(
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine("Data", "price-trade-type-template-settings.json")
                : configured,
            environment.ContentRootPath);
    }

    public PriceTradeTypeTemplateSettingsResponse Get()
        => GetEffective(_defaults.CurrentValue);

    public PriceTradeTypeTemplateSettingsResponse GetEffective(OcrRuntimeSettings defaults)
    {
        var saved = LoadSaved();

        return Normalize(new PriceTradeTypeTemplateSettingsResponse(
            PriceTradeTypeReadMode: saved?.PriceTradeTypeReadMode ?? defaults.PriceTradeTypeReadMode,
            PriceTradeTypeTemplateFallbackToNormalOcr: saved?.PriceTradeTypeTemplateFallbackToNormalOcr ?? defaults.PriceTradeTypeTemplateFallbackToNormalOcr,
            PriceTradeTypeTemplateAutoProfileEnabled: saved?.PriceTradeTypeTemplateAutoProfileEnabled ?? defaults.PriceTradeTypeTemplateAutoProfileEnabled,
            PriceTradeTypeTemplateMaxTemplatesPerType: saved?.PriceTradeTypeTemplateMaxTemplatesPerType ?? defaults.PriceTradeTypeTemplateMaxTemplatesPerType,
            PriceTradeTypeTemplateMaxScore: saved?.PriceTradeTypeTemplateMaxScore ?? defaults.PriceTradeTypeTemplateMaxScore,
            PriceTradeTypeTemplateCountFailedReadsForRecalibration: saved?.PriceTradeTypeTemplateCountFailedReadsForRecalibration ?? defaults.PriceTradeTypeTemplateCountFailedReadsForRecalibration,
            PriceTradeTypeTemplateRecalibrationFailureLimit: saved?.PriceTradeTypeTemplateRecalibrationFailureLimit ?? defaults.PriceTradeTypeTemplateRecalibrationFailureLimit,
            PriceTradeTypeTemplateProbeIntervalMs: saved?.PriceTradeTypeTemplateProbeIntervalMs ?? defaults.PriceTradeTypeTemplateProbeIntervalMs));
    }

    public async Task<PriceTradeTypeTemplateSettingsResponse> UpdateAsync(
        UpdatePriceTradeTypeTemplateSettingsRequest request,
        CancellationToken ct)
    {
        var current = Get();

        var updated = Normalize(new PriceTradeTypeTemplateSettingsResponse(
            PriceTradeTypeReadMode: request.PriceTradeTypeReadMode ?? current.PriceTradeTypeReadMode,
            PriceTradeTypeTemplateFallbackToNormalOcr: request.PriceTradeTypeTemplateFallbackToNormalOcr ?? current.PriceTradeTypeTemplateFallbackToNormalOcr,
            PriceTradeTypeTemplateAutoProfileEnabled: request.PriceTradeTypeTemplateAutoProfileEnabled ?? current.PriceTradeTypeTemplateAutoProfileEnabled,
            PriceTradeTypeTemplateMaxTemplatesPerType: request.PriceTradeTypeTemplateMaxTemplatesPerType ?? current.PriceTradeTypeTemplateMaxTemplatesPerType,
            PriceTradeTypeTemplateMaxScore: request.PriceTradeTypeTemplateMaxScore ?? current.PriceTradeTypeTemplateMaxScore,
            PriceTradeTypeTemplateCountFailedReadsForRecalibration: request.PriceTradeTypeTemplateCountFailedReadsForRecalibration ?? current.PriceTradeTypeTemplateCountFailedReadsForRecalibration,
            PriceTradeTypeTemplateRecalibrationFailureLimit: request.PriceTradeTypeTemplateRecalibrationFailureLimit ?? current.PriceTradeTypeTemplateRecalibrationFailureLimit,
            PriceTradeTypeTemplateProbeIntervalMs: request.PriceTradeTypeTemplateProbeIntervalMs ?? current.PriceTradeTypeTemplateProbeIntervalMs));

        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var tempPath = $"{_path}.tmp";
        var json = JsonSerializer.Serialize(updated, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, _path, overwrite: true);

        return updated;
    }

    private PriceTradeTypeTemplateSettingsResponse? LoadSaved()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    return null;

                var saved = JsonSerializer.Deserialize<PriceTradeTypeTemplateSettingsResponse>(
                    File.ReadAllText(_path),
                    JsonOptions);

                return saved is null ? null : Normalize(saved);
            }
            catch
            {
                return null;
            }
        }
    }

    private static PriceTradeTypeTemplateSettingsResponse Normalize(PriceTradeTypeTemplateSettingsResponse settings)
        => settings with
        {
            PriceTradeTypeReadMode = PriceTradeTypeReadModes.Normalize(settings.PriceTradeTypeReadMode),
            PriceTradeTypeTemplateMaxTemplatesPerType = Math.Clamp(settings.PriceTradeTypeTemplateMaxTemplatesPerType, 1, 50),
            PriceTradeTypeTemplateMaxScore = Math.Clamp(settings.PriceTradeTypeTemplateMaxScore, 0, 1),
            PriceTradeTypeTemplateRecalibrationFailureLimit = Math.Clamp(settings.PriceTradeTypeTemplateRecalibrationFailureLimit, 1, 100),
            PriceTradeTypeTemplateProbeIntervalMs = Math.Clamp(settings.PriceTradeTypeTemplateProbeIntervalMs, 25, 60_000)
        };

    private static string ResolvePath(string path, string root)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
}

public sealed class PriceTradeTypeTemplateOcrService : IPriceTradeTypeTemplateOcrService
{
    private const char InkPixel = '1';
    private const char BackgroundPixel = '0';
    private const int MaxAttemptLogEntries = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _profilePath;
    private readonly List<PriceTradeTypeTemplateAttemptLog> _attempts = new();

    public PriceTradeTypeTemplateOcrService()
        : this(Path.Combine(AppContext.BaseDirectory, "Data", "price-trade-type-template-profile.json"))
    {
    }

    public PriceTradeTypeTemplateOcrService(IWebHostEnvironment environment, IConfiguration configuration)
        : this(ResolveProfilePath(environment.ContentRootPath, configuration))
    {
    }

    public PriceTradeTypeTemplateOcrService(string profilePath)
    {
        _profilePath = profilePath;
    }

    public PriceTradeTypeTemplateProfileStatus GetProfileStatus(bool autoProfileEnabled = false)
    {
        lock (_gate)
            return BuildStatusLocked(autoProfileEnabled);
    }

    public PriceTradeTypeTemplateReadAttempt TryRead(
        Bitmap bitmap,
        string expectedTradeType,
        PriceTradeTypeTemplateSettingsResponse settings)
    {
        var normalizedTradeType = NormalizeTradeType(expectedTradeType);
        if (normalizedTradeType is null)
        {
            return new PriceTradeTypeTemplateReadAttempt(
                false,
                null,
                null,
                "Unknown trade type template requested.",
                false);
        }

        lock (_gate)
        {
            var profile = LoadProfileLocked();
            if (profile is null)
            {
                return new PriceTradeTypeTemplateReadAttempt(
                    false,
                    null,
                    null,
                    "Buy/Sell template profile missing.",
                    true);
            }

            var templates = TemplatesFor(profile, normalizedTradeType);
            if (templates.Count == 0)
            {
                return new PriceTradeTypeTemplateReadAttempt(
                    false,
                    null,
                    null,
                    $"{normalizedTradeType} template missing.",
                    profile.NeedsRecalibration);
            }

            double? bestScore = null;

            foreach (var template in templates)
            {
                var pixels = BuildBinaryPixels(bitmap, template.Width, template.Height);
                var score = ComparePixelsWithNeighborTolerance(pixels, template.Pixels);

                if (bestScore is null || score < bestScore)
                    bestScore = score;
            }

            var threshold = Math.Clamp(settings.PriceTradeTypeTemplateMaxScore, 0, 1);
            if (bestScore <= threshold)
            {
                profile.FailedReadCount = 0;
                profile.NeedsRecalibration = false;
                profile.LastMessage = $"{normalizedTradeType} fast template matched. Score={bestScore:0.000}.";
                profile.UpdatedAtUtc = DateTime.UtcNow;
                SaveProfileLocked(profile);

                return new PriceTradeTypeTemplateReadAttempt(
                    true,
                    normalizedTradeType,
                    bestScore,
                    profile.LastMessage,
                    false);
            }

            return new PriceTradeTypeTemplateReadAttempt(
                false,
                null,
                bestScore,
                $"{normalizedTradeType} fast template score too high. Score={bestScore:0.000}; Limit={threshold:0.000}.",
                profile.NeedsRecalibration);
        }
    }

    public PriceTradeTypeTemplateProfileStatus AddProfileSampleFromNormalOcr(
        Bitmap bitmap,
        OcrLayoutBox sourceBox,
        string tradeType,
        PriceTradeTypeTemplateSettingsResponse settings,
        string? rawText = null)
    {
        var normalizedTradeType = NormalizeTradeType(tradeType);
        if (normalizedTradeType is null)
        {
            lock (_gate)
            {
                var profile = LoadProfileLocked() ?? CreateProfile();
                profile.LastMessage = "Normal OCR did not return Buy or Sell, so no template was learned.";
                profile.UpdatedAtUtc = DateTime.UtcNow;
                SaveProfileLocked(profile);
                return BuildStatusLocked(settings.PriceTradeTypeTemplateAutoProfileEnabled);
            }
        }

        lock (_gate)
        {
            var profile = LoadProfileLocked() ?? CreateProfile();
            var templates = TemplatesFor(profile, normalizedTradeType);

            if (templates.Count >= settings.PriceTradeTypeTemplateMaxTemplatesPerType)
            {
                profile.LastMessage = $"{normalizedTradeType} template cap reached. Kept {templates.Count} templates.";
                profile.UpdatedAtUtc = DateTime.UtcNow;
                SaveProfileLocked(profile);
                return BuildStatusLocked(settings.PriceTradeTypeTemplateAutoProfileEnabled);
            }

            var pixels = BuildBinaryPixels(bitmap, bitmap.Width, bitmap.Height);
            templates.Add(new PriceTradeTypeBoxTemplate
            {
                TradeType = normalizedTradeType,
                Width = bitmap.Width,
                Height = bitmap.Height,
                Pixels = pixels,
                SourceBox = sourceBox,
                ScoreThreshold = settings.PriceTradeTypeTemplateMaxScore,
                CreatedAtUtc = DateTime.UtcNow
            });

            profile.SampleCount++;
            profile.FailedReadCount = 0;
            profile.NeedsRecalibration = false;
            profile.MissingTemplates = BuildMissingTemplates(profile);
            profile.LastMessage = string.IsNullOrWhiteSpace(rawText)
                ? $"{normalizedTradeType} template learned from normal OCR."
                : $"{normalizedTradeType} template learned from normal OCR text '{rawText.Trim()}'.";
            profile.UpdatedAtUtc = DateTime.UtcNow;
            SaveProfileLocked(profile);

            return BuildStatusLocked(settings.PriceTradeTypeTemplateAutoProfileEnabled);
        }
    }

    public PriceTradeTypeTemplateProfileStatus MaybeCountFailedFastRead(
        PriceTradeTypeTemplateSettingsResponse settings,
        string reason)
    {
        lock (_gate)
        {
            var profile = LoadProfileLocked() ?? CreateProfile();

            if (!settings.PriceTradeTypeTemplateCountFailedReadsForRecalibration)
            {
                profile.LastMessage = $"{reason}; failure counting disabled.";
                profile.UpdatedAtUtc = DateTime.UtcNow;
                SaveProfileLocked(profile);
                return BuildStatusLocked(settings.PriceTradeTypeTemplateAutoProfileEnabled);
            }

            profile.FailedReadCount++;
            if (profile.FailedReadCount >= settings.PriceTradeTypeTemplateRecalibrationFailureLimit)
                profile.NeedsRecalibration = true;

            profile.LastMessage = reason;
            profile.UpdatedAtUtc = DateTime.UtcNow;
            SaveProfileLocked(profile);

            return BuildStatusLocked(settings.PriceTradeTypeTemplateAutoProfileEnabled);
        }
    }

    public void ResetFailures()
    {
        lock (_gate)
        {
            var profile = LoadProfileLocked();
            if (profile is null)
                return;

            profile.FailedReadCount = 0;
            profile.NeedsRecalibration = false;
            profile.LastMessage = "Fast Buy/Sell template failure count reset.";
            profile.UpdatedAtUtc = DateTime.UtcNow;
            SaveProfileLocked(profile);
        }
    }

    public void DeleteProfile()
    {
        lock (_gate)
        {
            if (File.Exists(_profilePath))
                File.Delete(_profilePath);

            _attempts.Clear();
        }
    }

    public void RecordAttempt(PriceTradeTypeTemplateAttemptLog entry)
    {
        lock (_gate)
        {
            _attempts.Insert(0, entry);

            if (_attempts.Count > MaxAttemptLogEntries)
                _attempts.RemoveRange(MaxAttemptLogEntries, _attempts.Count - MaxAttemptLogEntries);
        }
    }

    private PriceTradeTypeTemplateProfileStatus BuildStatusLocked(bool autoProfileEnabled)
    {
        var profile = LoadProfileLocked();
        var missing = profile is null
            ? new[] { "Buy", "Sell" }
            : BuildMissingTemplates(profile).ToArray();

        if (profile is not null)
            profile.MissingTemplates = missing.ToList();

        return new PriceTradeTypeTemplateProfileStatus(
            ProfileReady: profile is not null && missing.Length == 0,
            ProfileId: profile?.ProfileId,
            BuyReady: profile?.BuyTemplates.Count > 0,
            SellReady: profile?.SellTemplates.Count > 0,
            MissingTemplates: missing,
            BuyTemplateCount: profile?.BuyTemplates.Count ?? 0,
            SellTemplateCount: profile?.SellTemplates.Count ?? 0,
            SampleCount: profile?.SampleCount ?? 0,
            FailedReadCount: profile?.FailedReadCount ?? 0,
            NeedsRecalibration: profile?.NeedsRecalibration ?? false,
            LastMessage: profile?.LastMessage,
            AutoProfileEnabled: autoProfileEnabled,
            CreatedAtUtc: profile?.CreatedAtUtc,
            UpdatedAtUtc: profile?.UpdatedAtUtc,
            LastAttempts: _attempts.ToArray());
    }

    private PriceTradeTypeTemplateProfile? LoadProfileLocked()
    {
        try
        {
            if (!File.Exists(_profilePath))
                return null;

            var profile = JsonSerializer.Deserialize<PriceTradeTypeTemplateProfile>(
                File.ReadAllText(_profilePath),
                JsonOptions);

            if (profile is null)
                return null;

            profile.MissingTemplates = BuildMissingTemplates(profile);
            return profile;
        }
        catch
        {
            return null;
        }
    }

    private void SaveProfileLocked(PriceTradeTypeTemplateProfile profile)
    {
        profile.MissingTemplates = BuildMissingTemplates(profile);

        var folder = Path.GetDirectoryName(_profilePath);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var tempPath = $"{_profilePath}.tmp";
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _profilePath, overwrite: true);
    }

    private static PriceTradeTypeTemplateProfile CreateProfile()
    {
        var remembered = GameWindowSelectionStore.GetRemembered();

        return new PriceTradeTypeTemplateProfile
        {
            ProfileId = Guid.NewGuid().ToString("N"),
            GameWindowTitle = remembered?.Title ?? string.Empty,
            MissingTemplates = new List<string> { "Buy", "Sell" },
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static List<PriceTradeTypeBoxTemplate> TemplatesFor(
        PriceTradeTypeTemplateProfile profile,
        string tradeType)
        => tradeType.Equals("Buy", StringComparison.OrdinalIgnoreCase)
            ? profile.BuyTemplates
            : profile.SellTemplates;

    private static List<string> BuildMissingTemplates(PriceTradeTypeTemplateProfile profile)
    {
        var missing = new List<string>();

        if (profile.BuyTemplates.Count == 0)
            missing.Add("Buy");

        if (profile.SellTemplates.Count == 0)
            missing.Add("Sell");

        return missing;
    }

    private static string? NormalizeTradeType(string? value)
    {
        if (string.Equals(value, "Buy", StringComparison.OrdinalIgnoreCase))
            return "Buy";

        if (string.Equals(value, "Sell", StringComparison.OrdinalIgnoreCase))
            return "Sell";

        return null;
    }

    private static string[] BuildBinaryPixels(Bitmap source, int targetWidth, int targetHeight)
    {
        using var normalized = ResizeIfNeeded(source, targetWidth, targetHeight);
        var grayValues = new int[normalized.Width * normalized.Height];
        var dark = 0;
        var light = 0;

        for (var y = 0; y < normalized.Height; y++)
        {
            for (var x = 0; x < normalized.Width; x++)
            {
                var color = normalized.GetPixel(x, y);
                var gray = (int)((color.R * 0.299) + (color.G * 0.587) + (color.B * 0.114));
                grayValues[(y * normalized.Width) + x] = gray;

                if (gray < 128)
                    dark++;
                else
                    light++;
            }
        }

        var inkIsDark = dark <= light;
        var rows = new string[normalized.Height];

        for (var y = 0; y < normalized.Height; y++)
        {
            var chars = new char[normalized.Width];

            for (var x = 0; x < normalized.Width; x++)
            {
                var gray = grayValues[(y * normalized.Width) + x];
                var isInk = inkIsDark ? gray < 128 : gray >= 128;
                chars[x] = isInk ? InkPixel : BackgroundPixel;
            }

            rows[y] = new string(chars);
        }

        return rows;
    }

    private static Bitmap ResizeIfNeeded(Bitmap source, int targetWidth, int targetHeight)
    {
        targetWidth = Math.Max(1, targetWidth);
        targetHeight = Math.Max(1, targetHeight);

        if (source.Width == targetWidth && source.Height == targetHeight)
            return new Bitmap(source);

        var resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);

        return resized;
    }

    private static double ComparePixelsWithNeighborTolerance(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> template)
    {
        if (candidate.Count == 0 || template.Count == 0)
            return 1;

        var height = Math.Min(candidate.Count, template.Count);
        var width = Math.Min(candidate[0].Length, template[0].Length);
        if (width == 0 || height == 0)
            return 1;

        var mismatches = 0;
        var total = width * height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var candidateInk = candidate[y][x] == InkPixel;
                var templateInk = template[y][x] == InkPixel;

                if (candidateInk == templateInk)
                    continue;

                if (candidateInk && HasInkNeighbor(template, x, y, width, height))
                    continue;

                if (templateInk && HasInkNeighbor(candidate, x, y, width, height))
                    continue;

                mismatches++;
            }
        }

        return mismatches / (double)total;
    }

    private static bool HasInkNeighbor(
        IReadOnlyList<string> pixels,
        int x,
        int y,
        int width,
        int height)
    {
        for (var ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
        {
            for (var nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
            {
                if (pixels[ny][nx] == InkPixel)
                    return true;
            }
        }

        return false;
    }

    private static string ResolveProfilePath(string root, IConfiguration configuration)
    {
        var configured = configuration.GetValue<string>("OcrSettings:PriceTradeTypeTemplateProfilePath");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine("Data", "price-trade-type-template-profile.json")
            : configured;

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
    }
}
