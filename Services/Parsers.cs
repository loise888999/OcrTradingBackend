using System.Globalization;
using System.Text.RegularExpressions;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public interface ICoordinateParser { ParsedCoordinate? TryParse(string text, int worldWidth, int worldHeight); }

public sealed class CoordinateParser : ICoordinateParser
{
    private static readonly Regex CoordinateRegex = new(@"(?<!\d)(\d{1,5})\s*[,\.]\s*(\d{1,5})(?!\d)", RegexOptions.Compiled);

    public ParsedCoordinate? TryParse(string text, int worldWidth, int worldHeight)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = CoordinateRegex.Match(text);
        if (!match.Success) return null;

        var x = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var y = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        return x >= 0 && x <= worldWidth && y >= 0 && y <= worldHeight
            ? new ParsedCoordinate(x, y, match.Value)
            : null;
    }
}

public interface ICityParser { string? TryParse(string text, int minLetters); }

public sealed class CityParser : ICityParser
{
    private readonly ICityCatalog _catalog;
    public CityParser(ICityCatalog catalog) => _catalog = catalog;

    public string? TryParse(string text, int minLetters)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = CleanCityCandidate(raw);

            if (candidate.Count(char.IsLetter) < minLetters)
                continue;

            var city = _catalog.FindByName(candidate);
            if (city is not null)
                return city.Name;
        }

        return null;
    }

    private static string CleanCityCandidate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // City OCR can return values like:
        // "Mogadishu (Allied"
        // "Mogadishu (Allied Country C -"
        // We only want the city name before the first parenthesis.
        var beforeParenthesis = raw.Split('(', 2)[0];

        var candidate = Regex.Replace(beforeParenthesis, @"[^\p{L}\s\-']", " ").Trim();
        candidate = Regex.Replace(candidate, @"\s+", " ");

        return candidate;
    }
}

public interface IPriceParser { IReadOnlyList<ParsedPriceLine> ParseLines(string text); }

public sealed class PriceParser : IPriceParser
{
    private readonly ITradeGoodCatalog _catalog;
    private readonly IPendingTradeGoodService _pendingTradeGoods;

    public PriceParser(ITradeGoodCatalog catalog, IPendingTradeGoodService pendingTradeGoods)
    {
        _catalog = catalog;
        _pendingTradeGoods = pendingTradeGoods;
    }

    public IReadOnlyList<ParsedPriceLine> ParseLines(string text)
    {
        var results = new List<ParsedPriceLine>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        var tradeType = DetectTradeType(text);
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanLine)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !IsHeaderOrNoise(x))
            .ToList();

        string? pendingKnownItem = null;
        TradeGoodDefinition? pendingKnownGood = null;
        string? pendingUnknownItem = null;

        foreach (var line in lines)
        {
            var knownGood = _catalog.FindByName(line);
            if (knownGood is not null)
            {
                pendingKnownItem = knownGood.Name;
                pendingKnownGood = knownGood;
                pendingUnknownItem = null;
                continue;
            }

            var parsed = TryParsePriceAndMultiplier(line);
            if (parsed is not null)
            {
                if (pendingKnownItem is not null && pendingKnownGood is not null)
                {
                    results.Add(new ParsedPriceLine(
                        pendingKnownItem,
                        pendingKnownGood.Type,
                        parsed.Value.Price,
                        parsed.Value.Multiplier,
                        tradeType,
                        $"{pendingKnownItem} | {line}"
                    ));
                }
                else if (!string.IsNullOrWhiteSpace(pendingUnknownItem))
                {
                    RegisterPendingTradeGood(pendingUnknownItem, line, tradeType, parsed.Value.Price, parsed.Value.Multiplier);
                }

                pendingKnownItem = null;
                pendingKnownGood = null;
                pendingUnknownItem = null;
                continue;
            }

            if (LooksLikeUnknownTradeGoodName(line))
            {
                pendingUnknownItem = line;
                pendingKnownItem = null;
                pendingKnownGood = null;
            }
        }

        return results;
    }

    private void RegisterPendingTradeGood(string name, string priceLine, string tradeType, decimal price, decimal multiplier)
    {
        var confidence = EstimateUnknownTradeGoodConfidence(name, priceLine, tradeType);
        if (confidence < 0.72) return;

        _pendingTradeGoods.AddOrUpdate(new PendingTradeGoodCandidateRequest(
            name,
            confidence,
            $"{name} | {priceLine}",
            tradeType,
            price,
            multiplier));
    }

    private static double EstimateUnknownTradeGoodConfidence(string name, string priceLine, string tradeType)
    {
        var score = 0.55;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = name.Count(char.IsLetter);

        if (letters >= 4) score += 0.08;
        if (words.Length <= 3) score += 0.05;
        if (priceLine.Contains('%')) score += 0.15;
        if (tradeType is "Buy" or "Sell") score += 0.07;
        if (name.Any(char.IsDigit)) score -= 0.25;
        if (name.Length <= 2) score -= 0.3;

        return Math.Clamp(score, 0, 1);
    }

    private static bool LooksLikeUnknownTradeGoodName(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (IsHeaderOrNoise(line)) return false;
        if (line.Any(char.IsDigit)) return false;
        if (line.Contains('%')) return false;

        var letters = line.Count(char.IsLetter);
        if (letters < 3) return false;

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 4) return false;

        var normalized = line.ToLowerInvariant().Trim();
        if (normalized is "arke" or "all" or "aill" or "sell aill") return false;

        return true;
    }

    private static string DetectTradeType(string raw)
    {
        var normalized = raw.ToLowerInvariant();
        if (normalized.Contains("sell") || normalized.Contains("inventory") || normalized.Contains("nventory")) return "Sell";
        if (normalized.Contains("items for sale") || normalized.Contains("for sale")) return "Buy";
        return "Unknown";
    }

    private static string CleanLine(string line)
    {
        return line
            .Replace("（", "(")
            .Replace("）", ")")
            .Replace("％", "%")
            .Replace("，", ",")
            .Replace("。", ".")
            .Replace("野", "(")
            .Replace("'", "")
            .Replace("?", "")
            .Trim();
    }

    private static bool IsHeaderOrNoise(string line)
    {
        var normalized = line.ToLowerInvariant().Trim();
        return normalized is "items for sale" or "inventory" or "nventory" or "sell" or "sella" or "sell all" or "sell aill" || normalized.Length <= 1;
    }

    private static (decimal Price, decimal Multiplier)? TryParsePriceAndMultiplier(string line)
    {
        return TryParseExplicitPriceMultiplier(line) ?? TryParseCompactPriceMultiplier(line);
    }

    private static (decimal Price, decimal Multiplier)? TryParseExplicitPriceMultiplier(string line)
    {
        var normalized = CleanLine(line);
        var match = Regex.Match(
            normalized,
            @"(?<price>\d[\d\.,]*)\s*[\(\s]+(?<mult>\d{1,3})\s*%",
            RegexOptions.Compiled
        );

        if (!match.Success) return null;

        var priceText = NormalizePriceNumber(match.Groups["price"].Value);
        var multiplierText = match.Groups["mult"].Value;

        if (!decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)) return null;
        if (!decimal.TryParse(multiplierText, NumberStyles.Number, CultureInfo.InvariantCulture, out var multiplier)) return null;
        if (!IsValidMultiplier(multiplier)) return null;

        return (price, multiplier);
    }

    private static (decimal Price, decimal Multiplier)? TryParseCompactPriceMultiplier(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        if (digits.Length < 3) return null;

        var candidates = new List<(string PricePart, string MultiplierPart)>();

        if (digits.Length > 3 && digits[^3] == '0')
        {
            candidates.Add((digits[..^2], digits[^2..]));
            candidates.Add((digits[..^3], digits[^3..]));
        }
        else
        {
            if (digits.Length > 3) candidates.Add((digits[..^3], digits[^3..]));
            if (digits.Length > 2) candidates.Add((digits[..^2], digits[^2..]));
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.PricePart)) continue;
            if (!decimal.TryParse(candidate.PricePart, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)) continue;
            if (!decimal.TryParse(candidate.MultiplierPart, NumberStyles.Number, CultureInfo.InvariantCulture, out var multiplier)) continue;
            if (!IsValidMultiplier(multiplier)) continue;
            return (price, multiplier);
        }

        return null;
    }

    private static string NormalizePriceNumber(string priceText)
    {
        return priceText.Replace(".", "").Replace(",", "").Trim();
    }

    private static bool IsValidMultiplier(decimal multiplier)
    {
        return multiplier > 0 && multiplier <= 199;
    }
}
