using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed class CoordinateFarJumpConfirmationGate
{
    private readonly object _gate = new();
    private PendingFarJump? _pending;

    public CoordinateFarJumpDecision Evaluate(
        ParsedCoordinate parsed,
        CoordinateCapture? previous,
        OcrRuntimeSettings settings)
    {
        lock (_gate)
        {
            if (!settings.CoordinateFarJumpConfirmationEnabled ||
                previous is null)
            {
                ClearUnsafe();
                return CoordinateFarJumpDecision.AcceptedImmediate;
            }

            var maxJumpX = Math.Max(1, settings.MaxCoordinateJumpX);
            var maxJumpY = Math.Max(1, settings.MaxCoordinateJumpY);
            var dx = CircularDistance(previous.X, parsed.X, settings.WorldWidth);
            var dy = Math.Abs(previous.Y - parsed.Y);

            if (dx <= maxJumpX && dy <= maxJumpY)
            {
                ClearUnsafe();
                return CoordinateFarJumpDecision.AcceptedImmediate;
            }

            var requiredReads = Math.Max(1, settings.CoordinateFarJumpRequiredReads);
            var toleranceX = Math.Max(0, settings.CoordinateFarJumpClusterToleranceX);
            var toleranceY = Math.Max(0, settings.CoordinateFarJumpClusterToleranceY);

            var resetPending = _pending is not null &&
                (CircularDistance(_pending.X, parsed.X, settings.WorldWidth) > toleranceX ||
                 Math.Abs(_pending.Y - parsed.Y) > toleranceY);

            if (_pending is null || resetPending)
            {
                _pending = new PendingFarJump(parsed.X, parsed.Y, 1);
                return new CoordinateFarJumpDecision(false, false, 1, requiredReads, resetPending);
            }

            _pending = _pending with { Count = _pending.Count + 1 };

            if (_pending.Count < requiredReads)
                return new CoordinateFarJumpDecision(false, false, _pending.Count, requiredReads, false);

            ClearUnsafe();
            return new CoordinateFarJumpDecision(true, true, requiredReads, requiredReads, false);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearUnsafe();
        }
    }

    private void ClearUnsafe() => _pending = null;

    private static int CircularDistance(int a, int b, int width)
    {
        var safeWidth = Math.Max(1, width);
        var dx = Math.Abs(a - b);
        return Math.Min(dx, Math.Abs(safeWidth - dx));
    }

    private sealed record PendingFarJump(int X, int Y, int Count);
}

public sealed record CoordinateFarJumpDecision(
    bool Accepted,
    bool AcceptedAfterConfirmation,
    int PendingCount,
    int RequiredCount,
    bool ResetPending)
{
    public static CoordinateFarJumpDecision AcceptedImmediate { get; } = new(true, false, 0, 0, false);
}
