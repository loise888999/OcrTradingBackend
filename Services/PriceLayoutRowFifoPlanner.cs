namespace OcrTradingBackend.Services;

public static class PriceLayoutRowFifoPlanner
{
    public static IReadOnlyList<T> OrderRows<T>(
        IReadOnlyList<T> rows,
        int nextIndex)
    {
        if (rows.Count == 0)
            return Array.Empty<T>();

        var start = NormalizeNextIndex(nextIndex, rows.Count);
        var ordered = new List<T>(rows.Count);

        for (var offset = 0; offset < rows.Count; offset++)
        {
            ordered.Add(rows[(start + offset) % rows.Count]);
        }

        return ordered;
    }

    public static int AdvanceNextIndex(
        int nextIndex,
        int inspectedCount,
        int rowCount)
    {
        if (rowCount <= 0)
            return 0;

        return NormalizeNextIndex(
            NormalizeNextIndex(nextIndex, rowCount) + Math.Max(0, inspectedCount),
            rowCount);
    }

    public static int NormalizeNextIndex(int nextIndex, int rowCount)
    {
        if (rowCount <= 0)
            return 0;

        var normalized = nextIndex % rowCount;
        return normalized < 0 ? normalized + rowCount : normalized;
    }
}
