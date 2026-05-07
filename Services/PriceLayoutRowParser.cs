using System.Globalization;
using System.Text.RegularExpressions;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public static class PriceLayoutRowParser
{
    public static ParsedPriceLine? TryParseCombinedLayoutPriceRow(
        int rowIndex,
        string rawText,
        string tradeType,
        Func<string, StrictTradeGoodMatch?> strictTradeGoodMatcher)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        if (!TryParseLayoutRowPrice(rawText, out var price, out var multiplier))
            return null;

        var itemText = ExtractLayoutRowItemText(rawText);
        if (string.IsNullOrWhiteSpace(itemText))
            return null;

        if (itemText.Any(char.IsDigit))
            return null;

        var strict = strictTradeGoodMatcher(itemText);
        var itemName = strict?.Name ?? itemText;
        var tradeGoodType = strict?.TradeGoodType ?? "Unknown";

        var raw =
            $"Row {rowIndex}: {itemName} | {price.ToString(CultureInfo.InvariantCulture)} | {multiplier.ToString(CultureInfo.InvariantCulture)} | {tradeType}";

        return new ParsedPriceLine(
            itemName,
            tradeGoodType,
            price,
            multiplier,
            tradeType,
            raw);
    }

    public static string ExtractLayoutRowItemText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        var normalized = NormalizePriceRowText(rawText, removeDecimalPoint: true)
            .Replace("\r", " ")
            .Replace("\n", " ");

        var multiplierMatch = FindPercentMultiplier(normalized);
        var searchEnd = multiplierMatch.Success
            ? multiplierMatch.Index
            : normalized.Length;

        var beforeMultiplier = normalized[..searchEnd];
        var priceMatches = Regex.Matches(
                beforeMultiplier,
                @"\d+",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToList();

        if (priceMatches.Count > 0)
            beforeMultiplier = beforeMultiplier[..priceMatches[^1].Index];

        return NormalizeOcrItemName(beforeMultiplier);
    }

    public static bool TryParseLayoutRowPrice(
        string rawText,
        out decimal price,
        out decimal multiplier)
    {
        price = 0;
        multiplier = 0;

        var normalized = NormalizePriceRowText(rawText, removeDecimalPoint: true);
        var multiplierMatch = FindPercentMultiplier(normalized);

        if (multiplierMatch.Success &&
            decimal.TryParse(
                multiplierMatch.Groups["mult"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsedMultiplier))
        {
            multiplier = parsedMultiplier;
        }
        else
        {
            return false;
        }

        var beforeMultiplier = normalized[..multiplierMatch.Index];
        var numbers = Regex.Matches(
                beforeMultiplier,
                @"\d+",
                RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .ToList();

        if (numbers.Count == 0)
            return false;

        foreach (var number in numbers.AsEnumerable().Reverse())
        {
            if (decimal.TryParse(
                    number,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out price))
            {
                return true;
            }
        }

        return false;
    }

    private static Match FindPercentMultiplier(string normalized)
    {
        return Regex.Match(
            normalized,
            @"(?<mult>\d{1,3})\s*\)?\s*%",
            RegexOptions.CultureInvariant);
    }

    private static string NormalizePriceRowText(
        string rawText,
        bool removeDecimalPoint)
    {
        var normalized = rawText
            .Replace("ÃƒÂ¯Ã‚Â¼Ã¢â‚¬Â¦", "%")
            .Replace("Ã¯Â¼â€¦", "%")
            .Replace("ï¼…", "%")
            .Replace(",", "");

        return removeDecimalPoint
            ? normalized.Replace(".", "")
            : normalized;
    }

    private static string NormalizeOcrItemName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Normalize();

        normalized = Regex.Replace(
            normalized,
            @"[^\p{L}\p{N}]+",
            " ");

        normalized = Regex.Replace(
            normalized,
            @"\s+",
            " ");

        return normalized
            .Trim()
            .ToLowerInvariant();
    }
}
