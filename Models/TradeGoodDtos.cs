namespace OcrTradingBackend.Models;

public sealed record AddTradeGoodRequest(
    string Name,
    string Type,
    IReadOnlyList<string>? Aliases,
    bool Force = false
);

public sealed record TradeGoodSuggestion(
    string Name,
    string Type,
    double Score,
    IReadOnlyList<string> Aliases
);

public sealed record AddTradeGoodResult(
    bool Added,
    string Message,
    object? TradeGood,
    IReadOnlyList<TradeGoodSuggestion> Suggestions
);
