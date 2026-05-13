namespace OcrTradingBackend.Models;

public sealed record NpcNormalCraftingItem(
    string Category,
    string NpcOrFacility,
    string Locations,
    string RecipeMethod,
    string Product,
    string RequiredSkills,
    string Materials,
    string ItemType,
    string Scope,
    string Notes,
    string DataStatus,
    string SourceUrl);
