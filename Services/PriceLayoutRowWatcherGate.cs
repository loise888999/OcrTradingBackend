using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public static class PriceLayoutRowWatcherGate
{
    public static bool ShouldRun(
        OcrRuntimeSettings settings,
        bool useFastTradeTypeTemplate,
        bool hasKnownCity,
        bool coordinateRecentlyVisible,
        string? tradeType,
        bool scheduledPriceReadWillRun,
        DateTime? lastWatcherUtc,
        DateTime nowUtc)
    {
        if (!settings.PriceLayoutRowWatcherEnabled ||
            !useFastTradeTypeTemplate ||
            !hasKnownCity ||
            coordinateRecentlyVisible ||
            scheduledPriceReadWillRun ||
            !PriceCaptureMergeService.IsKnownTradeType(tradeType))
        {
            return false;
        }

        if (lastWatcherUtc is null)
            return true;

        return nowUtc - lastWatcherUtc.Value >= GetInterval(settings);
    }

    public static TimeSpan GetInterval(OcrRuntimeSettings settings)
        => TimeSpan.FromMilliseconds(
            Math.Clamp(settings.PriceLayoutRowWatcherIntervalMs, 25, 60_000));
}
