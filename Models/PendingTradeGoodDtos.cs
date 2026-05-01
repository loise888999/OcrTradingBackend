namespace OcrTradingBackend.Models;

public enum PendingTradeGoodStatus
{
    Pending,
    Accepted,
    Dismissed
}

public sealed class PendingTradeGoodCandidate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string SuggestedType { get; set; } = "";
    public double Confidence { get; set; }
    public int SeenCount { get; set; }
    public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    public string LastRawText { get; set; } = "";
    public string LastTradeType { get; set; } = "Unknown";
    public decimal? LastPrice { get; set; }
    public decimal? LastMultiplier { get; set; }
    public IReadOnlyList<TradeGoodSuggestion> Similar { get; set; } = Array.Empty<TradeGoodSuggestion>();
    public PendingTradeGoodStatus Status { get; set; } = PendingTradeGoodStatus.Pending;
    public DateTime? ResolvedAtUtc { get; set; }
    public string ResolutionMessage { get; set; } = "";
}

public sealed record PendingTradeGoodCandidateRequest(
    string Name,
    double Confidence,
    string? RawText,
    string? TradeType,
    decimal? Price,
    decimal? Multiplier
);

public sealed record AcceptPendingTradeGoodRequest(
    string? Name,
    string? Type,
    IReadOnlyList<string>? Aliases,
    bool Force = false
);

public sealed record PendingTradeGoodActionResult(
    bool Success,
    string Message,
    PendingTradeGoodCandidate? Candidate
);
