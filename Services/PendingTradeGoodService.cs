using OcrTradingBackend.Models;
using System.Text.Json;

namespace OcrTradingBackend.Services;

public interface IPendingTradeGoodService
{
    IReadOnlyList<PendingTradeGoodCandidate> GetAll(bool includeResolved = false);
    PendingTradeGoodCandidate AddOrUpdate(PendingTradeGoodCandidateRequest request);
    PendingTradeGoodActionResult Accept(string id, AcceptPendingTradeGoodRequest request);
    PendingTradeGoodActionResult Dismiss(string id);
}

public sealed class PendingTradeGoodService : IPendingTradeGoodService
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly ITradeGoodCatalog _tradeGoodCatalog;
    private List<PendingTradeGoodCandidate> _items = new();

    public PendingTradeGoodService(IWebHostEnvironment environment, ITradeGoodCatalog tradeGoodCatalog)
    {
        _tradeGoodCatalog = tradeGoodCatalog;
        _path = Path.Combine(environment.ContentRootPath, "Data", "pending-trade-goods.json");
        Load();
    }

    public IReadOnlyList<PendingTradeGoodCandidate> GetAll(bool includeResolved = false)
    {
        lock (_lock)
        {
            var query = includeResolved
                ? _items
                : _items.Where(x => x.Status == PendingTradeGoodStatus.Pending);

            return query
                .OrderByDescending(x => x.LastSeenAtUtc)
                .ToList();
        }
    }

    public PendingTradeGoodCandidate AddOrUpdate(PendingTradeGoodCandidateRequest request)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Candidate name is required.");

        lock (_lock)
        {
            var normalized = Normalize(name);
            var existing = _items.FirstOrDefault(x => x.NormalizedName == normalized && x.Status == PendingTradeGoodStatus.Pending);
            var now = DateTime.UtcNow;

            if (existing is not null)
            {
                existing.SeenCount += 1;
                existing.LastSeenAtUtc = now;
                existing.LastRawText = request.RawText ?? existing.LastRawText;
                existing.LastTradeType = request.TradeType ?? existing.LastTradeType;
                existing.LastPrice = request.Price ?? existing.LastPrice;
                existing.LastMultiplier = request.Multiplier ?? existing.LastMultiplier;
                existing.Confidence = Math.Max(existing.Confidence, request.Confidence);
                Save();
                return existing;
            }

            var suggestions = _tradeGoodCatalog.SuggestSimilar(name, 5);
            var candidate = new PendingTradeGoodCandidate
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                NormalizedName = normalized,
                SuggestedType = suggestions.FirstOrDefault()?.Type ?? string.Empty,
                Confidence = Math.Clamp(request.Confidence, 0, 1),
                SeenCount = 1,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
                LastRawText = request.RawText ?? string.Empty,
                LastTradeType = request.TradeType ?? "Unknown",
                LastPrice = request.Price,
                LastMultiplier = request.Multiplier,
                Similar = suggestions.ToList(),
                Status = PendingTradeGoodStatus.Pending
            };

            _items.Add(candidate);
            Save();
            return candidate;
        }
    }

    public PendingTradeGoodActionResult Accept(string id, AcceptPendingTradeGoodRequest request)
    {
        lock (_lock)
        {
            var candidate = _items.FirstOrDefault(x => x.Id == id);
            if (candidate is null)
                return new PendingTradeGoodActionResult(false, "Candidate was not found.", null);

            if (candidate.Status != PendingTradeGoodStatus.Pending)
                return new PendingTradeGoodActionResult(false, $"Candidate is already {candidate.Status}.", candidate);

            var acceptedName = string.IsNullOrWhiteSpace(request.Name)
                ? candidate.Name
                : request.Name.Trim();

            var acceptedType = string.IsNullOrWhiteSpace(request.Type)
                ? candidate.SuggestedType
                : request.Type.Trim();

            var aliases = BuildAcceptedAliases(
                request.Aliases,
                candidate.Name,
                acceptedName);

            var addResult = _tradeGoodCatalog.AddTradeGood(new AddTradeGoodRequest(
                acceptedName,
                acceptedType,
                aliases,
                request.Force));

            if (!addResult.Added)
                return new PendingTradeGoodActionResult(false, addResult.Message, candidate);

            candidate.Status = PendingTradeGoodStatus.Accepted;
            candidate.ResolvedAtUtc = DateTime.UtcNow;
            candidate.ResolutionMessage = addResult.Message;
            Save();

            return new PendingTradeGoodActionResult(true, addResult.Message, candidate);
        }
    }

    public PendingTradeGoodActionResult Dismiss(string id)
    {
        lock (_lock)
        {
            var candidate = _items.FirstOrDefault(x => x.Id == id);
            if (candidate is null)
                return new PendingTradeGoodActionResult(false, "Candidate was not found.", null);

            candidate.Status = PendingTradeGoodStatus.Dismissed;
            candidate.ResolvedAtUtc = DateTime.UtcNow;
            candidate.ResolutionMessage = "Dismissed by user.";
            Save();

            return new PendingTradeGoodActionResult(true, "Candidate dismissed.", candidate);
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            if (!File.Exists(_path))
            {
                _items = new List<PendingTradeGoodCandidate>();
                Save();
                return;
            }

            var json = File.ReadAllText(_path);
            _items = string.IsNullOrWhiteSpace(json)
                ? new List<PendingTradeGoodCandidate>()
                : JsonSerializer.Deserialize<List<PendingTradeGoodCandidate>>(json, JsonOptions()) ?? new List<PendingTradeGoodCandidate>();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_items, JsonOptions()));
    }

    private static IReadOnlyList<string> BuildAcceptedAliases(
        IReadOnlyList<string>? requestedAliases,
        string candidateName,
        string acceptedName)
    {
        var aliases = requestedAliases?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList() ?? new List<string>();

        var originalOcrName = (candidateName ?? string.Empty).Trim();
        var finalName = (acceptedName ?? string.Empty).Trim();

        // Important:
        // If the user corrects/renames the pending OCR value before accepting it,
        // keep the original OCR-read name as an alias.
        //
        // Example:
        // OCR saw: "Diamoncl"
        // User accepts as: "Diamond"
        // Saved good becomes:
        // Name = Diamond
        // Alias = Diamoncl
        //
        // This lets the next OCR validation match the same bad OCR text.
        if (!string.IsNullOrWhiteSpace(originalOcrName) &&
            !string.Equals(originalOcrName, finalName, StringComparison.OrdinalIgnoreCase))
        {
            aliases.Add(originalOcrName);
        }

        return aliases
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string Normalize(string value)
    {
        value = value.ToLowerInvariant();
        value = System.Text.RegularExpressions.Regex.Replace(value, @"[^a-z0-9]+", " ");
        return System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
    }
}