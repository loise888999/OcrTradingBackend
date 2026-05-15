namespace OcrTradingBackend.Models;

public sealed record FlorenceCraftsmanContributionItem(
    string RecordId,
    string PoCategory,
    string TradeGoodType,
    string TradeGood,
    string ContributionSkill,
    decimal? ScoreMin,
    decimal? ScoreMax,
    decimal? ScoreAvg,
    string Uncertain,
    string Confidence,
    string DisplayLabel,
    string Remarks,
    string SourceTable,
    string SourceUrl,
    string GoogleSheetUrl,
    string AppNote);
