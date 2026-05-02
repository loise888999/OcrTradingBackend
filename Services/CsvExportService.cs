using System.Globalization;
using System.Text;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public static class CsvExportService
{
    public static string ExportPrices(IEnumerable<PriceCapture> prices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("City,ItemName,TradeGoodType,Price,Multiplier,TradeType,CapturedAtUtc,RawText");

        foreach (var price in prices)
        {
            sb.AppendLine(string.Join(',', new[]
            {
                Csv(price.City),
                Csv(price.ItemName),
                Csv(price.TradeGoodType),
                Csv(price.Price.ToString(CultureInfo.InvariantCulture)),
                Csv(price.Multiplier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(price.TradeType),
                Csv(price.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                Csv(price.RawText)
            }));
        }

        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
