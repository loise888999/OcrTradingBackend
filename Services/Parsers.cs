using System.Globalization;
using System.Text.RegularExpressions;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record CoordinateCorrectionOptions(
    bool Enabled,
    int MaxJumpX,
    int MaxJumpY
);

public interface ICoordinateParser
{
    ParsedCoordinate? TryParse(string text, int worldWidth, int worldHeight);
    ParsedCoordinate? TryParse(string text, int worldWidth, int worldHeight, CoordinateCapture? previous, CoordinateCorrectionOptions correctionOptions);
}

public sealed class CoordinateParser : ICoordinateParser
{
    private static readonly Regex LabeledCoordinateRegex = new(
        @"x\s*[:=]?\s*(?<x>\d{1,5})\D{0,12}y\s*[:=]?\s*(?<y>\d{1,5})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PairCoordinateRegex = new(
        @"(?<!\d)(?<x>\d{1,5})\s*[,\.]\s*(?<y>\d{1,5})(?!\d)",
        RegexOptions.Compiled);

    private static readonly Regex SplitLinePairCoordinateRegex = new(
        @"(?<!\d)(?<x>\d{1,5})\s*[,\.]\s*\d{1,2}\s+(?<y>\d{3,5})(?!\d)",
        RegexOptions.Compiled);

    public ParsedCoordinate? TryParse(string text, int worldWidth, int worldHeight)
    {
        return TryParse(text, worldWidth, worldHeight, null, new CoordinateCorrectionOptions(false, 0, 0));
    }

    public ParsedCoordinate? TryParse(string text, int worldWidth, int worldHeight, CoordinateCapture? previous, CoordinateCorrectionOptions correctionOptions)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = NormalizeOcrCoordinateText(text);
        var rawPairs = ExtractRawPairs(normalized).ToList();

        if (rawPairs.Count == 0)
            return null;

        var allCandidates = new List<CoordinateCandidate>();

        foreach (var pair in rawPairs)
        {
            allCandidates.AddRange(BuildCandidates(pair.XText, pair.YText, pair.RawText, worldWidth, worldHeight, correctionOptions.Enabled));
        }

        if (allCandidates.Count == 0)
            return null;

        // If we do not have a previous coordinate yet, choose the best direct/least corrected valid coordinate.
        if (previous is null)
        {
            var bestWithoutPrevious = allCandidates
                .OrderBy(c => c.CorrectionCount)
                .ThenBy(c => c.RawOrder)
                .FirstOrDefault();

            return bestWithoutPrevious?.ToParsedCoordinate();
        }

        // With a previous coordinate, use circular X distance and normal Y distance.
        // This treats x=1 and x=16250 as close when worldWidth=16500.
        var reasonableCandidates = allCandidates
            .Select(c => c with
            {
                CircularDx = CircularDistance(previous.X, c.X, worldWidth),
                Dy = Math.Abs(previous.Y - c.Y)
            })
            .Where(c => c.CircularDx <= Math.Max(1, correctionOptions.MaxJumpX) && c.Dy <= Math.Max(1, correctionOptions.MaxJumpY))
            .OrderBy(c => c.CorrectionCount)
            .ThenBy(c => c.CircularDx + c.Dy)
            .ThenBy(c => c.RawOrder)
            .ToList();

        if (reasonableCandidates.Count > 0)
            return reasonableCandidates[0].ToParsedCoordinate();

        // If direct value is valid but jump is too large, only allow it when correction is disabled.
        // This prevents bad OCR jumps from polluting the map.
        if (!correctionOptions.Enabled)
        {
            return allCandidates
                .Where(c => c.CorrectionCount == 0)
                .OrderBy(c => c.RawOrder)
                .FirstOrDefault()
                ?.ToParsedCoordinate();
        }

        return null;
    }

    private static IEnumerable<(string XText, string YText, string RawText)> ExtractRawPairs(string normalized)
    {
        foreach (Match match in LabeledCoordinateRegex.Matches(normalized))
            yield return (match.Groups["x"].Value, match.Groups["y"].Value, match.Value);

        foreach (Match match in SplitLinePairCoordinateRegex.Matches(normalized))
            yield return (match.Groups["x"].Value, match.Groups["y"].Value, match.Value);

        foreach (Match match in PairCoordinateRegex.Matches(normalized))
            yield return (match.Groups["x"].Value, match.Groups["y"].Value, match.Value);
    }

    private static IEnumerable<CoordinateCandidate> BuildCandidates(
        string xText,
        string yText,
        string rawText,
        int worldWidth,
        int worldHeight,
        bool correctionEnabled)
    {
        var rawOrder = 0;
        var xCandidates = correctionEnabled ? GenerateDigitCorrections(xText) : new[] { new DigitCandidate(xText, 0) };
        var yCandidates = correctionEnabled ? GenerateDigitCorrections(yText) : new[] { new DigitCandidate(yText, 0) };

        foreach (var xCandidate in xCandidates)
        {
            foreach (var yCandidate in yCandidates)
            {
                rawOrder++;

                if (!int.TryParse(xCandidate.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) continue;
                if (!int.TryParse(yCandidate.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) continue;

                if (x < 0 || x > worldWidth) continue;
                if (y < 0 || y > worldHeight) continue;

                var correctionCount = xCandidate.CorrectionCount + yCandidate.CorrectionCount;
                var correctedRaw = correctionCount == 0
                    ? rawText.Trim()
                    : $"{rawText.Trim()} corrected to {x},{y}";

                yield return new CoordinateCandidate(x, y, correctedRaw, correctionCount, rawOrder, 0, 0);
            }
        }
    }

    private static IEnumerable<DigitCandidate> GenerateDigitCorrections(string value)
    {
        yield return new DigitCandidate(value, 0);

        // One-digit substitutions only. This keeps corrections conservative.
        // Most useful case: OCR reads 8825, but valid coordinate is 3825.
        var substitutions = new Dictionary<char, char[]>
        {
            ['8'] = new[] { '3', '6', '0' },
            ['3'] = new[] { '8' },
            ['6'] = new[] { '8', '5' },
            ['5'] = new[] { '6' },
            ['0'] = new[] { '8' },
            ['1'] = new[] { '7' },
            ['7'] = new[] { '1', '2' },
            ['2'] = new[] { '7' }
        };

        var seen = new HashSet<string> { value };

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!substitutions.TryGetValue(c, out var replacements)) continue;

            foreach (var replacement in replacements)
            {
                var chars = value.ToCharArray();
                chars[i] = replacement;
                var corrected = new string(chars).TrimStart('0');
                if (string.IsNullOrWhiteSpace(corrected)) corrected = "0";

                if (seen.Add(corrected))
                    yield return new DigitCandidate(corrected, 1);
            }
        }
    }

    private static int CircularDistance(int a, int b, int width)
    {
        var dx = Math.Abs(a - b);
        return Math.Min(dx, Math.Abs(width - dx));
    }

    private static string NormalizeOcrCoordinateText(string text)
    {
        return text
            .Replace('，', ',')
            .Replace('。', '.')
            .Replace('：', ':')
            .Replace('Ｘ', 'X')
            .Replace('Ｙ', 'Y')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private sealed record DigitCandidate(string Value, int CorrectionCount);

    private sealed record CoordinateCandidate(
        int X,
        int Y,
        string RawText,
        int CorrectionCount,
        int RawOrder,
        int CircularDx,
        int Dy)
    {
        public ParsedCoordinate ToParsedCoordinate() => new(X, Y, RawText);
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
            if (candidate.Count(char.IsLetter) < minLetters) continue;

            var city = _catalog.FindByName(candidate);
            if (city is not null) return city.Name;
        }

        return null;
    }

    private static string CleanCityCandidate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var beforeParenthesis = raw.Split('(', 2)[0];
        var candidate = Regex.Replace(beforeParenthesis, @"[^\p{L}\s\-']", " ").Trim();
        candidate = Regex.Replace(candidate, @"\s+", " ");
        return candidate;
    }
}

public interface IPriceParser
{
    IReadOnlyList<ParsedPriceLine> ParseLines(string text, bool allowPendingCandidates = false);
}

public sealed class PriceParser : IPriceParser
{
    private readonly ITradeGoodCatalog _catalog;
    private readonly IPendingTradeGoodService _pendingTradeGoods;

    public PriceParser(ITradeGoodCatalog catalog, IPendingTradeGoodService pendingTradeGoods)
    {
        _catalog = catalog;
        _pendingTradeGoods = pendingTradeGoods;
    }

    public IReadOnlyList<ParsedPriceLine> ParseLines(string text, bool allowPendingCandidates = false)
    {
        var results = new List<ParsedPriceLine>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        var tradeType = DetectTradeType(text);
        var tradeTypeKnown = PriceCaptureMergeService.IsKnownTradeType(tradeType);

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
                    if (tradeTypeKnown)
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
                }
                else if (allowPendingCandidates && tradeTypeKnown && !string.IsNullOrWhiteSpace(pendingUnknownItem))
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
        var match = Regex.Match(normalized, @"(?<price>\d[\d\.,]*)\s*[\(\s]+(?<mult>\d{1,3})\s*%", RegexOptions.Compiled);
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
