namespace OcrTradingBackend.Models;

public sealed record NanbanMarketRateItem(
    string SourceMarket,
    string TradeGood,
    string Category,
    string SellArea,
    int Price,
    string MarketSignal,
    string SourceUrl);
