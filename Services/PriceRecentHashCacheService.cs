namespace OcrTradingBackend.Services;

public sealed record PriceRecentHashCacheOptions(
    bool Enabled,
    double TtlMinutes,
    int MaxEntries,
    bool BenchmarkLogging);

public sealed record PriceRecentHashCacheCheckResult(
    bool Enabled,
    bool IsKnownCity,
    bool WasCityChanged,
    bool WasHit,
    int Count,
    string? PreviousCity,
    string? CurrentCity);

public interface IPriceRecentHashCacheService
{
    PriceRecentHashCacheCheckResult CheckRecent(
        string? city,
        string fullHash,
        PriceRecentHashCacheOptions options);

    PriceRecentHashCacheCheckResult RememberProcessed(
        string? city,
        string fullHash,
        PriceRecentHashCacheOptions options);

    void NotifyCityStatus(string? city);
}

public sealed class PriceRecentHashCacheService : IPriceRecentHashCacheService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTime> _hashes = new(StringComparer.Ordinal);

    private string? _currentCity;

    public PriceRecentHashCacheCheckResult CheckRecent(
        string? city,
        string fullHash,
        PriceRecentHashCacheOptions options)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(fullHash))
        {
            return new PriceRecentHashCacheCheckResult(
                Enabled: false,
                IsKnownCity: IsKnownCity(city),
                WasCityChanged: false,
                WasHit: false,
                Count: CountUnsafe(),
                PreviousCity: _currentCity,
                CurrentCity: NormalizeCity(city));
        }

        lock (_sync)
        {
            var cityChange = UpdateCityAndClearIfNeeded(city);
            var knownCity = IsKnownCity(city);

            if (!knownCity)
            {
                return new PriceRecentHashCacheCheckResult(
                    Enabled: true,
                    IsKnownCity: false,
                    WasCityChanged: cityChange.WasChanged,
                    WasHit: false,
                    Count: _hashes.Count,
                    PreviousCity: cityChange.PreviousCity,
                    CurrentCity: _currentCity);
            }

            PruneExpired(options);

            var hit = _hashes.ContainsKey(fullHash);

            return new PriceRecentHashCacheCheckResult(
                Enabled: true,
                IsKnownCity: true,
                WasCityChanged: cityChange.WasChanged,
                WasHit: hit,
                Count: _hashes.Count,
                PreviousCity: cityChange.PreviousCity,
                CurrentCity: _currentCity);
        }
    }

    public PriceRecentHashCacheCheckResult RememberProcessed(
        string? city,
        string fullHash,
        PriceRecentHashCacheOptions options)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(fullHash))
        {
            return new PriceRecentHashCacheCheckResult(
                Enabled: false,
                IsKnownCity: IsKnownCity(city),
                WasCityChanged: false,
                WasHit: false,
                Count: CountUnsafe(),
                PreviousCity: _currentCity,
                CurrentCity: NormalizeCity(city));
        }

        lock (_sync)
        {
            var cityChange = UpdateCityAndClearIfNeeded(city);
            var knownCity = IsKnownCity(city);

            if (!knownCity)
            {
                return new PriceRecentHashCacheCheckResult(
                    Enabled: true,
                    IsKnownCity: false,
                    WasCityChanged: cityChange.WasChanged,
                    WasHit: false,
                    Count: _hashes.Count,
                    PreviousCity: cityChange.PreviousCity,
                    CurrentCity: _currentCity);
            }

            PruneExpired(options);
            TrimToMaxEntries(options.MaxEntries);

            var wasHit = _hashes.ContainsKey(fullHash);
            _hashes[fullHash] = DateTime.UtcNow;

            return new PriceRecentHashCacheCheckResult(
                Enabled: true,
                IsKnownCity: true,
                WasCityChanged: cityChange.WasChanged,
                WasHit: wasHit,
                Count: _hashes.Count,
                PreviousCity: cityChange.PreviousCity,
                CurrentCity: _currentCity);
        }
    }

    public void NotifyCityStatus(string? city)
    {
        lock (_sync)
        {
            UpdateCityAndClearIfNeeded(city);
        }
    }

    private CityChangeResult UpdateCityAndClearIfNeeded(string? city)
    {
        var normalized = NormalizeCity(city);
        var previous = _currentCity;

        if (string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return new CityChangeResult(
                WasChanged: false,
                PreviousCity: previous,
                CurrentCity: normalized);
        }

        _hashes.Clear();
        _currentCity = normalized;

        return new CityChangeResult(
            WasChanged: true,
            PreviousCity: previous,
            CurrentCity: normalized);
    }

    private void PruneExpired(PriceRecentHashCacheOptions options)
    {
        var ttl = TimeSpan.FromMinutes(Math.Max(0.1, options.TtlMinutes));
        var cutoff = DateTime.UtcNow - ttl;

        var expiredKeys = _hashes
            .Where(pair => pair.Value < cutoff)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in expiredKeys)
            _hashes.Remove(key);
    }

    private void TrimToMaxEntries(int maxEntries)
    {
        var safeMax = Math.Max(1, maxEntries);

        if (_hashes.Count <= safeMax)
            return;

        var removeCount = _hashes.Count - safeMax;

        var oldestKeys = _hashes
            .OrderBy(pair => pair.Value)
            .Take(removeCount)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in oldestKeys)
            _hashes.Remove(key);
    }

    private int CountUnsafe()
    {
        lock (_sync)
            return _hashes.Count;
    }

    private static bool IsKnownCity(string? city)
    {
        return !string.IsNullOrWhiteSpace(city) &&
               !string.Equals(city.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCity(string? city)
    {
        return IsKnownCity(city)
            ? city!.Trim()
            : "Unknown";
    }

    private sealed record CityChangeResult(
        bool WasChanged,
        string? PreviousCity,
        string CurrentCity);
}
