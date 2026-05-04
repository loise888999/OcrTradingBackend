using System.Diagnostics;
using System.Drawing;

namespace OcrTradingBackend.Services;

public sealed record PriceOcrBatchOptions(
    bool Enabled,
    int MaxImages,
    bool UseSampleHashBeforeFullHash,
    int SampleHashStep,
    double ForceFullHashEverySeconds,
    bool BenchmarkLogging,
    bool RecentHashCacheEnabled,
    double RecentHashCacheMinutes,
    int RecentHashCacheMaxEntries);

public sealed record DeferredPriceOcrImage(
    Bitmap Image,
    string Source,
    string City,
    string FullHash,
    string? SampleHash,
    DateTime CapturedAtUtc);

public sealed record PriceOcrBatchAddResult(
    bool Added,
    bool Duplicate,
    bool MaxReached,
    int Count,
    string Decision,
    string? SampleHash,
    string? FullHash,
    TimeSpan SampleHashElapsed,
    TimeSpan FullHashElapsed);

public interface IPriceOcrBatchService
{
    int Count { get; }

    PriceOcrBatchAddResult TryAdd(
        Bitmap bitmap,
        string source,
        string city,
        PriceOcrBatchOptions options);

    bool ShouldFlushIdle(TimeSpan idleAfter);

    bool ShouldFlushByAge(TimeSpan maxAge);

    IReadOnlyList<DeferredPriceOcrImage> Drain();
}

public sealed class PriceOcrBatchService : IPriceOcrBatchService
{
    private readonly IOcrImageHasher _hasher;
    private readonly IPriceRecentHashCacheService _recentHashCache;
    private readonly object _sync = new();

    private readonly List<DeferredPriceOcrImage> _images = new();
    private readonly HashSet<string> _fullHashes = new(StringComparer.Ordinal);

    private string? _lastSampleHash;
    private string? _lastFullHash;
    private DateTime _lastFullHashCheckedAtUtc = DateTime.MinValue;
    private DateTime? _lastAddedAtUtc;

    public PriceOcrBatchService(
        IOcrImageHasher hasher,
        IPriceRecentHashCacheService recentHashCache)
    {
        _hasher = hasher;
        _recentHashCache = recentHashCache;
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _images.Count;
        }
    }

    public PriceOcrBatchAddResult TryAdd(
        Bitmap bitmap,
        string source,
        string city,
        PriceOcrBatchOptions options)
    {
        if (!options.Enabled)
        {
            return new PriceOcrBatchAddResult(
                Added: false,
                Duplicate: false,
                MaxReached: false,
                Count: Count,
                Decision: "batch-disabled",
                SampleHash: null,
                FullHash: null,
                SampleHashElapsed: TimeSpan.Zero,
                FullHashElapsed: TimeSpan.Zero);
        }

        var maxImages = Math.Max(1, options.MaxImages);
        var now = DateTime.UtcNow;

        lock (_sync)
        {
            if (_images.Count >= maxImages)
            {
                return new PriceOcrBatchAddResult(
                    Added: false,
                    Duplicate: false,
                    MaxReached: true,
                    Count: _images.Count,
                    Decision: "max-reached-before-add",
                    SampleHash: null,
                    FullHash: null,
                    SampleHashElapsed: TimeSpan.Zero,
                    FullHashElapsed: TimeSpan.Zero);
            }

            string? sampleHash = null;
            var sampleElapsed = TimeSpan.Zero;

            if (options.UseSampleHashBeforeFullHash)
            {
                var sampleStopwatch = Stopwatch.StartNew();
                sampleHash = _hasher.ComputeSampleHash(bitmap, options.SampleHashStep);
                sampleStopwatch.Stop();
                sampleElapsed = sampleStopwatch.Elapsed;

                var fullHashDue =
                    !string.IsNullOrWhiteSpace(_lastFullHash) &&
                    options.ForceFullHashEverySeconds > 0 &&
                    now - _lastFullHashCheckedAtUtc >=
                    TimeSpan.FromSeconds(options.ForceFullHashEverySeconds);

                if (!fullHashDue &&
                    string.Equals(_lastSampleHash, sampleHash, StringComparison.Ordinal))
                {
                    return new PriceOcrBatchAddResult(
                        Added: false,
                        Duplicate: true,
                        MaxReached: false,
                        Count: _images.Count,
                        Decision: "sample-duplicate-skipped",
                        SampleHash: sampleHash,
                        FullHash: _lastFullHash,
                        SampleHashElapsed: sampleElapsed,
                        FullHashElapsed: TimeSpan.Zero);
                }
            }

            var fullStopwatch = Stopwatch.StartNew();
            var fullHash = _hasher.ComputeFullHash(bitmap);
            fullStopwatch.Stop();

            _lastSampleHash = sampleHash;
            _lastFullHash = fullHash;
            _lastFullHashCheckedAtUtc = now;

            if (_fullHashes.Contains(fullHash))
            {
                return new PriceOcrBatchAddResult(
                    Added: false,
                    Duplicate: true,
                    MaxReached: false,
                    Count: _images.Count,
                    Decision: "full-duplicate-skipped",
                    SampleHash: sampleHash,
                    FullHash: fullHash,
                    SampleHashElapsed: sampleElapsed,
                    FullHashElapsed: fullStopwatch.Elapsed);
            }

            var recentCacheResult = _recentHashCache.CheckRecent(
                city,
                fullHash,
                GetRecentHashCacheOptions(options));

            if (recentCacheResult.WasHit)
            {
                return new PriceOcrBatchAddResult(
                    Added: false,
                    Duplicate: true,
                    MaxReached: false,
                    Count: _images.Count,
                    Decision: "recent-10min-hash-hit-skipped",
                    SampleHash: sampleHash,
                    FullHash: fullHash,
                    SampleHashElapsed: sampleElapsed,
                    FullHashElapsed: fullStopwatch.Elapsed);
            }

            var clone = new Bitmap(bitmap);

            _images.Add(new DeferredPriceOcrImage(
                Image: clone,
                Source: source,
                City: city,
                FullHash: fullHash,
                SampleHash: sampleHash,
                CapturedAtUtc: now));

            _fullHashes.Add(fullHash);
            _lastAddedAtUtc = now;

            var count = _images.Count;

            return new PriceOcrBatchAddResult(
                Added: true,
                Duplicate: false,
                MaxReached: count >= maxImages,
                Count: count,
                Decision: "added",
                SampleHash: sampleHash,
                FullHash: fullHash,
                SampleHashElapsed: sampleElapsed,
                FullHashElapsed: fullStopwatch.Elapsed);
        }
    }

    public bool ShouldFlushIdle(TimeSpan idleAfter)
    {
        if (idleAfter <= TimeSpan.Zero)
            return false;

        lock (_sync)
        {
            if (_images.Count == 0 || _lastAddedAtUtc is null)
                return false;

            return DateTime.UtcNow - _lastAddedAtUtc.Value >= idleAfter;
        }
    }

    public bool ShouldFlushByAge(TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero)
            return false;

        lock (_sync)
        {
            if (_images.Count == 0)
                return false;

            var oldestCaptureUtc = _images[0].CapturedAtUtc;
            return DateTime.UtcNow - oldestCaptureUtc >= maxAge;
        }
    }

    public IReadOnlyList<DeferredPriceOcrImage> Drain()
    {
        lock (_sync)
        {
            if (_images.Count == 0)
                return Array.Empty<DeferredPriceOcrImage>();

            var drained = _images.ToList();

            _images.Clear();
            _fullHashes.Clear();

            _lastSampleHash = null;
            _lastFullHash = null;
            _lastFullHashCheckedAtUtc = DateTime.MinValue;
            _lastAddedAtUtc = null;

            return drained;
        }
    }

    private static PriceRecentHashCacheOptions GetRecentHashCacheOptions(
        PriceOcrBatchOptions options)
    {
        return new PriceRecentHashCacheOptions(
            Enabled: options.RecentHashCacheEnabled,
            TtlMinutes: options.RecentHashCacheMinutes,
            MaxEntries: options.RecentHashCacheMaxEntries,
            BenchmarkLogging: options.BenchmarkLogging);
    }
}
