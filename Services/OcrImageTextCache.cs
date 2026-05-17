using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;

namespace OcrTradingBackend.Services;

public sealed record OcrHashCacheOptions(
    bool Enabled,
    double TtlMinutes,
    int MaxEntries,
    string SettingsSignature,
    bool BenchmarkLogging);

public sealed record OcrCachedTextRead(
    string Text,
    bool WasHashHit,
    string Decision,
    string? SampleHash,
    string? FullHash,
    TimeSpan SampleHashElapsed,
    TimeSpan FullHashElapsed,
    TimeSpan OcrElapsed,
    int CacheEntryCount = 0,
    int EvictedCount = 0);

public interface IOcrImageTextCache
{
    OcrCachedTextRead ReadText(
        string cacheKey,
        Bitmap bitmap,
        Func<Bitmap, string> readText,
        OcrHashCacheOptions options);
}

public sealed class OcrImageTextCache : IOcrImageTextCache
{
    private static readonly TimeSpan DefaultPruneInterval = TimeSpan.FromSeconds(10);

    private readonly IOcrImageHasher _hasher;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _pruneInterval;
    private readonly ConcurrentDictionary<string, CachedOcrReadState> _states = new();
    private readonly object _evictionSync = new();
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public OcrImageTextCache(
        IOcrImageHasher hasher,
        Func<DateTime>? utcNow = null,
        TimeSpan? pruneInterval = null)
    {
        _hasher = hasher;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _pruneInterval = pruneInterval ?? DefaultPruneInterval;
    }

    public OcrCachedTextRead ReadText(
        string cacheKey,
        Bitmap bitmap,
        Func<Bitmap, string> readText,
        OcrHashCacheOptions options)
    {
        _ = cacheKey;

        if (!options.Enabled)
        {
            var disabledOcrStopwatch = Stopwatch.StartNew();
            var text = readText(bitmap);
            disabledOcrStopwatch.Stop();

            return new OcrCachedTextRead(
                Text: text,
                WasHashHit: false,
                Decision: "hash-disabled-ocr-ran",
                SampleHash: null,
                FullHash: null,
                SampleHashElapsed: TimeSpan.Zero,
                FullHashElapsed: TimeSpan.Zero,
                OcrElapsed: disabledOcrStopwatch.Elapsed);
        }

        var now = _utcNow();

        using var hashReader = _hasher.CreateReader(bitmap);
        var fullStopwatch = Stopwatch.StartNew();
        var fullHash = hashReader.ComputeFullHash();
        fullStopwatch.Stop();

        var ttl = TimeSpan.FromMinutes(Math.Max(0.1, options.TtlMinutes));
        var maxEntries = Math.Max(1, options.MaxEntries);
        var entryKey = BuildEntryKey(options.SettingsSignature, fullHash);
        var evictedBeforeLookup = MaybePruneExpiredAndOversize(now, ttl, maxEntries);

        if (_states.TryGetValue(entryKey, out var state))
        {
            lock (state.Sync)
            {
                if (!IsExpired(state, now, ttl))
                {
                    state.LastAccessUtc = now;

                    return new OcrCachedTextRead(
                        Text: state.Text,
                        WasHashHit: true,
                        Decision: "full-hash-cache-hit",
                        SampleHash: null,
                        FullHash: fullHash,
                        SampleHashElapsed: TimeSpan.Zero,
                        FullHashElapsed: fullStopwatch.Elapsed,
                        OcrElapsed: TimeSpan.Zero,
                        CacheEntryCount: _states.Count,
                        EvictedCount: evictedBeforeLookup);
                }
            }

            _states.TryRemove(entryKey, out _);
        }

        var ocrStopwatch = Stopwatch.StartNew();
        var rawText = readText(bitmap);
        ocrStopwatch.Stop();

        var addedState = _states.GetOrAdd(entryKey, _ => new CachedOcrReadState());
        lock (addedState.Sync)
        {
            addedState.Text = rawText;
            addedState.FullHash = fullHash;
            addedState.CreatedAtUtc = now;
            addedState.LastAccessUtc = now;
            addedState.LastOcrReadAtUtc = now;
        }

        var evictedAfterAdd = _states.Count > maxEntries
            ? MaybePruneExpiredAndOversize(_utcNow(), ttl, maxEntries, force: true)
            : 0;

        return new OcrCachedTextRead(
            Text: rawText,
            WasHashHit: false,
            Decision: "full-hash-cache-miss-ocr-ran",
            SampleHash: null,
            FullHash: fullHash,
            SampleHashElapsed: TimeSpan.Zero,
            FullHashElapsed: fullStopwatch.Elapsed,
            OcrElapsed: ocrStopwatch.Elapsed,
            CacheEntryCount: _states.Count,
            EvictedCount: evictedBeforeLookup + evictedAfterAdd);
    }

    private int MaybePruneExpiredAndOversize(
        DateTime now,
        TimeSpan ttl,
        int maxEntries,
        bool force = false)
    {
        if (!force &&
            _states.Count <= maxEntries &&
            now - _lastPruneUtc < _pruneInterval)
        {
            return 0;
        }

        return PruneExpiredAndOversize(now, ttl, maxEntries, force);
    }

    private int PruneExpiredAndOversize(
        DateTime now,
        TimeSpan ttl,
        int maxEntries,
        bool force)
    {
        lock (_evictionSync)
        {
            if (!force &&
                _states.Count <= maxEntries &&
                now - _lastPruneUtc < _pruneInterval)
            {
                return 0;
            }

            _lastPruneUtc = now;
            var evicted = 0;

            foreach (var pair in _states.ToArray())
            {
                if (IsExpired(pair.Value, now, ttl) &&
                    _states.TryRemove(pair.Key, out _))
                {
                    evicted++;
                }
            }

            if (_states.Count <= maxEntries)
                return evicted;

            foreach (var pair in _states
                         .OrderBy(x => x.Value.LastAccessUtc)
                         .Take(Math.Max(0, _states.Count - maxEntries))
                         .ToArray())
            {
                if (_states.TryRemove(pair.Key, out _))
                    evicted++;
            }

            return evicted;
        }
    }

    private static bool IsExpired(
        CachedOcrReadState state,
        DateTime now,
        TimeSpan ttl)
    {
        return now - state.LastAccessUtc >= ttl;
    }

    private static string BuildEntryKey(string settingsSignature, string fullHash)
    {
        return $"{settingsSignature}:{fullHash}";
    }

    private sealed class CachedOcrReadState
    {
        public object Sync { get; } = new();
        public string Text { get; set; } = string.Empty;
        public string? FullHash { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.MinValue;
        public DateTime LastAccessUtc { get; set; } = DateTime.MinValue;
        public DateTime LastOcrReadAtUtc { get; set; } = DateTime.MinValue;
    }
}
