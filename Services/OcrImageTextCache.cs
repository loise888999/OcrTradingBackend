using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;

namespace OcrTradingBackend.Services;

public sealed record OcrHashCacheOptions(
    bool Enabled,
    bool UseSampleHashBeforeFullHash,
    int SampleHashStep,
    double ForceFullHashEverySeconds,
    bool BenchmarkLogging);

public sealed record OcrCachedTextRead(
    string Text,
    bool WasHashHit,
    string Decision,
    string? SampleHash,
    string? FullHash,
    TimeSpan SampleHashElapsed,
    TimeSpan FullHashElapsed,
    TimeSpan OcrElapsed);

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
    private readonly IOcrImageHasher _hasher;
    private readonly ConcurrentDictionary<string, CachedOcrReadState> _states = new();

    public OcrImageTextCache(IOcrImageHasher hasher)
    {
        _hasher = hasher;
    }

    public OcrCachedTextRead ReadText(
        string cacheKey,
        Bitmap bitmap,
        Func<Bitmap, string> readText,
        OcrHashCacheOptions options)
    {
        if (!options.Enabled)
        {
            var ocrStopwatch = Stopwatch.StartNew();
            var text = readText(bitmap);
            ocrStopwatch.Stop();

            return new OcrCachedTextRead(
                Text: text,
                WasHashHit: false,
                Decision: "hash-disabled-ocr-ran",
                SampleHash: null,
                FullHash: null,
                SampleHashElapsed: TimeSpan.Zero,
                FullHashElapsed: TimeSpan.Zero,
                OcrElapsed: ocrStopwatch.Elapsed);
        }

        var state = _states.GetOrAdd(cacheKey, _ => new CachedOcrReadState());

        lock (state.Sync)
        {
            var now = DateTime.UtcNow;

            string? sampleHash = null;
            var sampleElapsed = TimeSpan.Zero;

            if (options.UseSampleHashBeforeFullHash)
            {
                var sampleStopwatch = Stopwatch.StartNew();
                sampleHash = _hasher.ComputeSampleHash(bitmap, options.SampleHashStep);
                sampleStopwatch.Stop();
                sampleElapsed = sampleStopwatch.Elapsed;

                var fullHashDue =
                    state.HasValue &&
                    options.ForceFullHashEverySeconds > 0 &&
                    now - state.LastFullHashCheckedAtUtc >=
                    TimeSpan.FromSeconds(options.ForceFullHashEverySeconds);

                if (state.HasValue &&
                    !fullHashDue &&
                    string.Equals(state.SampleHash, sampleHash, StringComparison.Ordinal))
                {
                    return new OcrCachedTextRead(
                        Text: state.Text,
                        WasHashHit: true,
                        Decision: "sample-hash-hit",
                        SampleHash: sampleHash,
                        FullHash: state.FullHash,
                        SampleHashElapsed: sampleElapsed,
                        FullHashElapsed: TimeSpan.Zero,
                        OcrElapsed: TimeSpan.Zero);
                }
            }

            var fullStopwatch = Stopwatch.StartNew();
            var fullHash = _hasher.ComputeFullHash(bitmap);
            fullStopwatch.Stop();

            if (state.HasValue &&
                string.Equals(state.FullHash, fullHash, StringComparison.Ordinal))
            {
                state.SampleHash = sampleHash ?? state.SampleHash;
                state.LastFullHashCheckedAtUtc = now;

                return new OcrCachedTextRead(
                    Text: state.Text,
                    WasHashHit: true,
                    Decision: "full-hash-hit",
                    SampleHash: sampleHash,
                    FullHash: fullHash,
                    SampleHashElapsed: sampleElapsed,
                    FullHashElapsed: fullStopwatch.Elapsed,
                    OcrElapsed: TimeSpan.Zero);
            }

            var ocrStopwatch = Stopwatch.StartNew();
            var rawText = readText(bitmap);
            ocrStopwatch.Stop();

            state.HasValue = true;
            state.Text = rawText;
            state.SampleHash = sampleHash;
            state.FullHash = fullHash;
            state.LastFullHashCheckedAtUtc = now;
            state.LastOcrReadAtUtc = now;

            return new OcrCachedTextRead(
                Text: rawText,
                WasHashHit: false,
                Decision: "hash-miss-ocr-ran",
                SampleHash: sampleHash,
                FullHash: fullHash,
                SampleHashElapsed: sampleElapsed,
                FullHashElapsed: fullStopwatch.Elapsed,
                OcrElapsed: ocrStopwatch.Elapsed);
        }
    }

    private sealed class CachedOcrReadState
    {
        public object Sync { get; } = new();

        public bool HasValue { get; set; }
        public string Text { get; set; } = string.Empty;
        public string? SampleHash { get; set; }
        public string? FullHash { get; set; }
        public DateTime LastFullHashCheckedAtUtc { get; set; } = DateTime.MinValue;
        public DateTime LastOcrReadAtUtc { get; set; } = DateTime.MinValue;
    }
}
