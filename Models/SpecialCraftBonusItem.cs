namespace OcrTradingBackend.Models;

public sealed record SpecialCraftBonusItem(
    string ItemName,
    string ItemNameJapanese,
    string ItemType,
    string BonusStats,
    string CraftLocation,
    string NpcOrFacility,
    string Materials,
    string SkillRank,
    string ContributionCost,
    string UnlockConditions,
    string TradableBound,
    string Notes,
    string DataStatus,
    string SourceUrls);
