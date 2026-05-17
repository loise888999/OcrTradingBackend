using System.Text.RegularExpressions;

namespace OcrTradingBackend.Services;

public static class TradeTypeMenuTextDetector
{
    public static string Detect(string? rawText)
    {
        if (LooksLikeSell(rawText))
            return "Sell";

        if (LooksLikeBuy(rawText))
            return "Buy";

        return "Unknown";
    }

    public static bool LooksLikeBuy(string? rawText)
    {
        var normalized = Normalize(rawText);

        return ContainsWord(normalized, "buy") ||
               normalized.Contains("for sale", StringComparison.Ordinal) ||
               normalized.Contains("items for sale", StringComparison.Ordinal);
    }

    public static bool LooksLikeSell(string? rawText)
    {
        var normalized = Normalize(rawText);

        return ContainsWord(normalized, "sell") ||
               ContainsWord(normalized, "inventory") ||
               ContainsWord(normalized, "nventory");
    }

    public static string Normalize(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        var normalized = rawText
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

    private static bool ContainsWord(string normalized, string word)
    {
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        var escaped = Regex.Escape(word.ToLowerInvariant());

        return Regex.IsMatch(
            normalized,
            $@"(^|\s){escaped}($|\s)",
            RegexOptions.CultureInvariant);
    }
}
