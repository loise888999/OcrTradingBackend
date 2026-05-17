using OcrTradingBackend.Models;

namespace OcrTradingBackend.Services;

public sealed record CoordinateSpeedSnapshot(
    int? CoordinateId,
    int X,
    int Y,
    string RawText,
    DateTime CapturedAtUtc,
    double SpeedWorldUnitsPerSecond,
    double SpeedKnots,
    double SpeedDistance,
    double SpeedDeltaMilliseconds,
    int SpeedSampleCount,
    bool SpeedReset,
    string? SpeedResetReason);

public sealed record CoordinateWithSpeedResponse(
    int Id,
    int X,
    int Y,
    string RawText,
    DateTime CapturedAtUtc,
    double SpeedWorldUnitsPerSecond,
    double SpeedKnots,
    double SpeedDistance,
    double SpeedDeltaMilliseconds,
    int SpeedSampleCount,
    bool SpeedReset,
    string? SpeedResetReason);

public interface ICoordinateSpeedService
{
    CoordinateSpeedSnapshot AddCoordinate(
        CoordinateCapture coordinate,
        OcrRuntimeSettings settings);

    CoordinateSpeedSnapshot GetLatestSnapshot();

    IReadOnlyList<CoordinateWithSpeedResponse> BuildTimeline(
        IReadOnlyList<CoordinateCapture> coordinates,
        OcrRuntimeSettings settings);
}

public sealed class CoordinateSpeedService : ICoordinateSpeedService
{
    private readonly object _sync = new();
    private readonly CoordinateSpeedState _state = new();

    public CoordinateSpeedSnapshot AddCoordinate(
        CoordinateCapture coordinate,
        OcrRuntimeSettings settings)
    {
        lock (_sync)
        {
            return AddCoordinate(_state, coordinate, settings);
        }
    }

    public CoordinateSpeedSnapshot GetLatestSnapshot()
    {
        lock (_sync)
        {
            return _state.Latest ?? EmptySnapshot("no-coordinate");
        }
    }

    public IReadOnlyList<CoordinateWithSpeedResponse> BuildTimeline(
        IReadOnlyList<CoordinateCapture> coordinates,
        OcrRuntimeSettings settings)
    {
        var state = new CoordinateSpeedState();
        var responses = new List<CoordinateWithSpeedResponse>(coordinates.Count);

        foreach (var coordinate in coordinates.OrderBy(x => x.CapturedAtUtc))
        {
            var speed = AddCoordinate(state, coordinate, settings);
            responses.Add(ToResponse(coordinate, speed));
        }

        return responses;
    }

    private static CoordinateSpeedSnapshot AddCoordinate(
        CoordinateSpeedState state,
        CoordinateCapture coordinate,
        OcrRuntimeSettings settings)
    {
        if (!settings.CoordinateSpeedEnabled)
        {
            var disabled = EmptySnapshot(
                "disabled",
                coordinate);
            state.Latest = disabled;
            state.Previous = coordinate;
            state.Velocities.Clear();
            state.RecentAverages.Clear();
            return disabled;
        }

        if (state.Previous is null)
        {
            state.Previous = coordinate;
            state.Latest = EmptySnapshot("first-coordinate", coordinate);
            return state.Latest;
        }

        var deltaMs = (coordinate.CapturedAtUtc - state.Previous.CapturedAtUtc).TotalMilliseconds;
        var resetAfterMs = Math.Max(1, settings.CoordinateSpeedResetAfterMilliseconds);

        if (deltaMs <= 0)
        {
            state.Previous = coordinate;
            state.Velocities.Clear();
            state.RecentAverages.Clear();
            state.Latest = EmptySnapshot("non-increasing-timestamp", coordinate);
            return state.Latest;
        }

        if (deltaMs > resetAfterMs)
        {
            state.Previous = coordinate;
            state.Velocities.Clear();
            state.RecentAverages.Clear();
            state.Latest = EmptySnapshot("coordinate-gap", coordinate, deltaMs);
            return state.Latest;
        }

        var distance = Distance(state.Previous, coordinate, settings.WorldWidth);
        var worldUnitsPerSecond = (distance / deltaMs) * 1000.0;

        state.Velocities.Enqueue(new VelocitySample(
            coordinate.CapturedAtUtc,
            worldUnitsPerSecond));

        RemoveOldVelocitySamples(
            state,
            coordinate.CapturedAtUtc,
            Math.Max(1, settings.CoordinateSpeedWindowMilliseconds));

        var rollingAverage = state.Velocities.Count == 0
            ? 0.0
            : state.Velocities.Average(x => x.WorldUnitsPerSecond);

        state.RecentAverages.Enqueue(rollingAverage);
        while (state.RecentAverages.Count > Math.Max(1, settings.CoordinateSpeedRecentAverageCount))
            state.RecentAverages.Dequeue();

        var displayedWorldUnitsPerSecond = state.RecentAverages.Count == 0
            ? 0.0
            : state.RecentAverages.Max();

        state.Previous = coordinate;
        state.Latest = new CoordinateSpeedSnapshot(
            CoordinateId: coordinate.Id == 0 ? null : coordinate.Id,
            X: coordinate.X,
            Y: coordinate.Y,
            RawText: coordinate.RawText,
            CapturedAtUtc: coordinate.CapturedAtUtc,
            SpeedWorldUnitsPerSecond: displayedWorldUnitsPerSecond,
            SpeedKnots: displayedWorldUnitsPerSecond * KnotFactor(settings.WorldWidth),
            SpeedDistance: distance,
            SpeedDeltaMilliseconds: deltaMs,
            SpeedSampleCount: state.Velocities.Count,
            SpeedReset: false,
            SpeedResetReason: null);

        return state.Latest;
    }

    private static void RemoveOldVelocitySamples(
        CoordinateSpeedState state,
        DateTime latestUtc,
        int windowMs)
    {
        while (state.Velocities.Count > 0 &&
               (latestUtc - state.Velocities.Peek().CapturedAtUtc).TotalMilliseconds > windowMs)
        {
            state.Velocities.Dequeue();
        }
    }

    private static double Distance(
        CoordinateCapture previous,
        CoordinateCapture current,
        int worldWidth)
    {
        var dx = current.X - previous.X;
        var halfWidth = Math.Max(1, worldWidth) / 2.0;

        if (dx > halfWidth)
            dx -= Math.Max(1, worldWidth);
        else if (dx < -halfWidth)
            dx += Math.Max(1, worldWidth);

        var dy = current.Y - previous.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public static double KnotFactor(int worldWidth)
    {
        var width = Math.Max(1, worldWidth);
        return (2 * Math.PI * 6378.137) / width / 0.4 / 1.852;
    }

    private static CoordinateWithSpeedResponse ToResponse(
        CoordinateCapture coordinate,
        CoordinateSpeedSnapshot speed)
        => new(
            Id: coordinate.Id,
            X: coordinate.X,
            Y: coordinate.Y,
            RawText: coordinate.RawText,
            CapturedAtUtc: coordinate.CapturedAtUtc,
            SpeedWorldUnitsPerSecond: speed.SpeedWorldUnitsPerSecond,
            SpeedKnots: speed.SpeedKnots,
            SpeedDistance: speed.SpeedDistance,
            SpeedDeltaMilliseconds: speed.SpeedDeltaMilliseconds,
            SpeedSampleCount: speed.SpeedSampleCount,
            SpeedReset: speed.SpeedReset,
            SpeedResetReason: speed.SpeedResetReason);

    private static CoordinateSpeedSnapshot EmptySnapshot(
        string reason,
        CoordinateCapture? coordinate = null,
        double deltaMs = 0)
        => new(
            CoordinateId: coordinate is null || coordinate.Id == 0 ? null : coordinate.Id,
            X: coordinate?.X ?? 0,
            Y: coordinate?.Y ?? 0,
            RawText: coordinate?.RawText ?? string.Empty,
            CapturedAtUtc: coordinate?.CapturedAtUtc ?? DateTime.UtcNow,
            SpeedWorldUnitsPerSecond: 0,
            SpeedKnots: 0,
            SpeedDistance: 0,
            SpeedDeltaMilliseconds: deltaMs,
            SpeedSampleCount: 0,
            SpeedReset: true,
            SpeedResetReason: reason);

    private sealed class CoordinateSpeedState
    {
        public CoordinateCapture? Previous { get; set; }
        public Queue<VelocitySample> Velocities { get; } = new();
        public Queue<double> RecentAverages { get; } = new();
        public CoordinateSpeedSnapshot? Latest { get; set; }
    }

    private sealed record VelocitySample(
        DateTime CapturedAtUtc,
        double WorldUnitsPerSecond);
}
